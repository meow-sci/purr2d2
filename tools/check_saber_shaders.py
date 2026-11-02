#!/usr/bin/env python3
"""Check KSA include compatibility, compile/link GLSL and check its reflected ABI."""

from __future__ import annotations

import argparse
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import subprocess
import struct


def _compiler() -> Path | None:
    executable = "glslangValidator.exe" if os.name == "nt" else "glslangValidator"
    located = shutil.which(executable)
    if located:
        return Path(located)
    sdk = os.environ.get("VULKAN_SDK")
    candidates = [Path(sdk) / folder / executable for folder in ("bin", "Bin")] if sdk else []
    candidates += sorted((Path.home() / "VulkanSDK").glob(f"*/macOS/bin/{executable}"), reverse=True)
    return next((path for path in candidates if path.is_file()), None)


SHADER_PAIRS = (("Dark", "SaberGlow"), ("Light", "SaberLight"))
WATER_PAIRS = (("Dark", "SaberGlow", "SaberWaterGlow"), ("Light", "SaberLight", "SaberWaterLight"))
NATIVE_WATER_INCLUDES = {"../Common/Shared.glsl", "../Common/Camera.glsl", "OceanFunctions.glsl"}


def _expand_water_shader(path: Path, shaders: Path, active: set[Path] | None = None) -> str:
    path, shaders = path.resolve(), shaders.resolve()
    active = set() if active is None else active
    if path in active or not path.is_relative_to(shaders):
        raise ValueError("water shader include escaped packaged tree or is cyclic")
    active.add(path)
    try:
        def include(match):
            name = match.group(1)
            if name in NATIVE_WATER_INCLUDES:
                return match.group(0)
            if "\\" in name or PureWindowsPath(name).drive or Path(name).is_absolute():
                raise ValueError("invalid packaged water shader include")
            return _expand_water_shader(path.parent / name, shaders, active)
        return re.sub(r'^[ \t]*#include[ \t]+"([^"\r\n]+)"[ \t]*$',include,path.read_text(encoding="utf-8"),flags=re.MULTILINE)
    finally:
        active.remove(path)


def _spirv_bindings(path: Path) -> dict[str, tuple[int, int]]:
    data = path.read_bytes()
    words = struct.unpack("<" + "I" * (len(data) // 4), data)
    names, sets, bindings = {}, {}, {}
    index = 5
    while index < len(words):
        count, opcode = words[index] >> 16, words[index] & 0xffff
        if count == 0 or index + count > len(words):
            raise ValueError("invalid SPIR-V instruction")
        operands = words[index + 1:index + count]
        if opcode == 5:
            raw = struct.pack("<" + "I" * (len(operands) - 1), *operands[1:])
            names[operands[0]] = raw.split(b"\0",1)[0].decode("utf-8")
        if opcode == 71 and len(operands) >= 3:
            if operands[1] == 34: sets[operands[0]] = operands[2]
            if operands[1] == 33: bindings[operands[0]] = operands[2]
        index += count
    return {name: (sets[identity],bindings[identity]) for identity,name in names.items() if identity in sets and identity in bindings}



def check_shader_includes(shaders: Path) -> list[str]:
    """Check literal includes using shaderc's requesting-file-relative resolution."""
    shaders = shaders.resolve()
    pending = [shaders / (stem + suffix) for _, stem in SHADER_PAIRS
               for suffix in (".vert", ".frag")]
    pending += [shaders / (fragment + ".frag") for _, _, fragment in WATER_PAIRS]
    visited: set[Path] = set()
    issues: list[str] = []
    while pending:
        shader = pending.pop()
        if shader in visited:
            continue
        visited.add(shader)
        relative = shader.relative_to(shaders).as_posix()
        try:
            text = shader.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError) as exc:
            issues.append(f"{relative}: cannot read shader/include: {exc}")
            continue
        text = re.sub(r"/\*.*?\*/|//[^\r\n]*", "", text, flags=re.DOTALL)
        for include in re.findall(r'^\s*#\s*include\s*["<]([^">\r\n]+)[">]', text, re.MULTILINE):
            if shader.name in {p[2]+".frag" for p in WATER_PAIRS} and include in NATIVE_WATER_INCLUDES:
                continue
            if "\\" in include or PurePosixPath(include).is_absolute() or PureWindowsPath(include).drive:
                issues.append(f"{relative}: include must use a relative path with forward slashes: {include!r}")
                continue
            target = (shader.parent / include).resolve()
            try:
                target_relative = target.relative_to(shaders)
            except ValueError:
                issues.append(f"{relative}: include escapes the packaged shader tree: {include!r}")
                continue
            # KSA GetModPath (unchanged through 2026.10.25.5707) uses Substring(IndexOf("Content")),
            # even when shaders compile from the user's mods folder. Do not let
            # an incidental "Content" in this checkout's parent hide the bug.
            if "Content" not in target_relative.parts[:-1]:
                issues.append(f"{relative}: include {include!r} needs a Content directory for KSA dependency tracking")
            pending.append(target)
    return issues


def _compile_shader_pair(compiler: Path, shaders: Path, output: Path, style: str,
                         stem: str, label: str, fragment: Path | None = None, native: Path | None = None) -> tuple[int, str]:
    result = subprocess.run(
        [str(compiler), "-V", "--target-env", "vulkan1.2", "-l", "-q",
         "-I" + str(shaders), *(["-I" + str(native)] if native else []), "-o",
         str(output / f"{label}-{fragment.stem if fragment else stem}.linked.spv"),
         str(shaders / f"{stem}.vert"), str(fragment or shaders / f"{stem}.frag")],
        cwd=shaders, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        check=False, timeout=60)
    if result.returncode:
        return result.returncode, f"FAIL: {label} {style} saber GLSL compilation/linking\n" + result.stdout
    expected = {"mvp": (0, "8b5c"), "color": (64, "8b52"), "shape": (80, "8b52"),
                "animation": (96, "8b52"), "camera": (112, "8b52")}
    reflected = {name: (int(offset), kind.lower()) for name, offset, kind in re.findall(
        r"^GlowPush\.(\w+): offset (\d+), type ([0-9a-fA-F]+),", result.stdout, re.MULTILINE)}
    block = re.search(r"^GlowPush:.*? size (\d+),.*? numMembers (\d+)\s*$", result.stdout, re.MULTILINE)
    if reflected != expected or block is None or tuple(map(int, block.groups())) != (128, 5):
        return 1, f"FAIL: {label} {style} shader GlowPush must match the 128-byte CPU ABI\n" + result.stdout
    if fragment:
        reflected_bindings = _spirv_bindings(output / f"{label}-{fragment.stem}.linked.spv")
        expected_bindings = {"saberPostOceanDepth": (1,0), "saberWater": (1,1), "sceneDepth": (2,0),
                             "fftDisplacements": (2,2), "colorMap": (3,1), "uboStaticOceanData": (3,3),
                             "uboDynamicOceanData": (3,4), "global": (0,0)}
        if any(reflected_bindings.get(name) != value for name,value in expected_bindings.items()):
            return 1, f"FAIL: {label} water descriptor ABI mismatch: {reflected_bindings}\n"
        water_block = re.search(r"^SaberWaterData:.*? size (\d+),.*? numMembers (\d+)\s*$",result.stdout,re.MULTILINE)
        if water_block is None or tuple(map(int,water_block.groups())) != (16,1):
            return 1, f"FAIL: {label} water uniform must retain the 16-byte CPU ABI\n" + result.stdout
    return 0, f"PASS: {label} {style} saber GLSL compiles/links; reflected push constants match the 128-byte CPU ABI" + ("; native water descriptors/16-byte uniform match" if fragment else "") + "\n"


def run_shader_checks(source: Path, stage: Path | None = None) -> tuple[int, str]:
    source = source.resolve()
    output = source / "artifacts/checks/SaberVfxChecks"
    output.mkdir(parents=True, exist_ok=True)
    try:
        # Compile the production declaration verbatim in the game-free harness.
        # Only its enclosing type/accessibility changes; field order, types, and
        # StructLayout come from SaberRenderer rather than a test duplicate.
        renderer = (source / "src/Rendering/SaberRenderer.cs").read_text(encoding="utf-8")
        declaration = re.search(
            r"\[StructLayout\([^\]]+\)\]\s*private struct GlowPush\s*\{[^{}]*\}", renderer)
        if declaration is None:
            return 1, "FAIL: cannot locate the production GlowPush declaration for ABI checks\n"
        (output / "GlowPush.cs").write_text(
            "using System.Runtime.InteropServices;\nusing Brutal.Numerics;\n"
            + declaration.group().replace("private struct GlowPush", "internal struct GlowPush", 1)
            + "\n", encoding="utf-8")

        compiler = _compiler()
        if compiler is None:
            return 1, "FAIL: shader checks require glslangValidator on PATH or in VULKAN_SDK (Vulkan SDK)\n"
        native_default = Path(os.environ["KSA_DLL_DIR"]).resolve().parent / "Content" if os.environ.get("KSA_DLL_DIR") else source.parent / "ksa-game-assemblies/current/Content"
        native_root = Path(os.environ.get("KSA_CONTENT_DIR", native_default)).resolve()
        native_water = native_root / "Core/Shaders/Water"
        if not (native_water / "OceanFunctions.glsl").is_file():
            return 1, "FAIL: water shader checks need matching native Content (set KSA_CONTENT_DIR)\n"
        roots = [("source", source / "Assets/Shaders")]
        if stage is not None:
            roots.append(("stage", stage.resolve() / "Assets/Shaders"))
        outputs = []
        code = 0
        for label, shaders in roots:
            issues = check_shader_includes(shaders)
            if issues:
                code = 1
                outputs.extend(f"FAIL: {label} {issue}\n" for issue in issues)
                continue
            outputs.append(f"PASS: {label} shader includes resolve with KSA-compatible Content paths\n")
            for style, stem in SHADER_PAIRS:
                result, message = _compile_shader_pair(compiler, shaders, output, style, stem, label)
                code = code or result
                outputs.append(message)
            for style, vertex, fragment in WATER_PAIRS:
                expanded = output / (label + "-" + fragment + ".frag")
                expanded.write_text(_expand_water_shader(shaders / (fragment+".frag"),shaders),encoding="utf-8")
                result,message = _compile_shader_pair(compiler,shaders,output,style+" Water",vertex,label,expanded,native_water)
                code = code or result
                outputs.append(message)
        return code, "".join(outputs)
    except (OSError, UnicodeDecodeError, ValueError, subprocess.TimeoutExpired) as exc:
        return 1, f"FAIL: saber shader checks: {exc}\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--stage", type=Path, help="also compile the packaged shaders and shared includes")
    arguments = parser.parse_args()
    code, message = run_shader_checks(arguments.source, arguments.stage)
    print(message, end="")
    raise SystemExit(code)
