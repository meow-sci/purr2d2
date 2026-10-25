#!/usr/bin/env python3
"""Check KSA include compatibility, compile/link GLSL and check its reflected ABI."""

from __future__ import annotations

import argparse
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import subprocess


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


def check_shader_includes(shaders: Path) -> list[str]:
    """Check literal includes using shaderc's requesting-file-relative resolution."""
    shaders = shaders.resolve()
    pending = [shaders / (stem + suffix) for _, stem in SHADER_PAIRS
               for suffix in (".vert", ".frag")]
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
            if "\\" in include or PurePosixPath(include).is_absolute() or PureWindowsPath(include).drive:
                issues.append(f"{relative}: include must use a relative path with forward slashes: {include!r}")
                continue
            target = (shader.parent / include).resolve()
            try:
                target_relative = target.relative_to(shaders)
            except ValueError:
                issues.append(f"{relative}: include escapes the packaged shader tree: {include!r}")
                continue
            # KSA 2026.10.19.5638 GetModPath uses Substring(IndexOf("Content")),
            # even when shaders compile from the user's mods folder. Do not let
            # an incidental "Content" in this checkout's parent hide the bug.
            if "Content" not in target_relative.parts[:-1]:
                issues.append(f"{relative}: include {include!r} needs a Content directory for KSA dependency tracking")
            pending.append(target)
    return issues


def _compile_shader_pair(compiler: Path, shaders: Path, output: Path, style: str,
                         stem: str, label: str) -> tuple[int, str]:
    result = subprocess.run(
        [str(compiler), "-V", "--target-env", "vulkan1.2", "-l", "-q",
         "-I" + str(shaders), "-o", str(output / f"{label}-{stem}.linked.spv"),
         str(shaders / f"{stem}.vert"), str(shaders / f"{stem}.frag")],
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
    return 0, f"PASS: {label} {style} saber GLSL compiles/links; reflected push constants match the 128-byte CPU ABI\n"


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
        return code, "".join(outputs)
    except (OSError, UnicodeDecodeError, subprocess.TimeoutExpired) as exc:
        return 1, f"FAIL: saber shader checks: {exc}\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--stage", type=Path, help="also compile the packaged shaders and shared includes")
    arguments = parser.parse_args()
    code, message = run_shader_checks(arguments.source, arguments.stage)
    print(message, end="")
    raise SystemExit(code)
