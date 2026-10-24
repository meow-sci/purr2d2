#!/usr/bin/env python3
"""Validate Purr2D2 source assets and an optional clean staging directory."""

from __future__ import annotations

import argparse
import ast
import json
import math
import os
import re
import subprocess
import struct
import sys
import textwrap
import wave
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath, PureWindowsPath

from check_saber_shaders import SHADER_PAIRS, run_shader_checks


DEFAULT_SOURCE = Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"\A(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\Z")
GLB_JSON_CHUNK = 0x4E4F534A
GLB_BIN_CHUNK = 0x004E4942
COMPONENT_BYTES = {5120: 1, 5121: 1, 5122: 2, 5123: 2, 5125: 4, 5126: 4}
TYPE_COMPONENTS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT2": 4, "MAT3": 9, "MAT4": 16}
REQUIRED_ASSET_EXTENSIONS = {".glb", ".png", ".wav", ".ogg", ".vert", ".frag"}
ROOT_STAGE_FILES = {
    "Purr2D2.dll",
    "Purr2D2.deps.json",
    "mod.toml",
    "LICENSE",
    "README.md",
}


class ValidationError(Exception):
    pass


def _relative_asset_path(root: Path, raw: str, context: str, base: Path | None = None) -> Path:
    normalized = raw.replace("\\", "/")
    posix = PurePosixPath(normalized)
    windows = PureWindowsPath(raw)
    if not normalized or posix.is_absolute() or windows.is_absolute() or windows.drive or "://" in normalized:
        raise ValidationError(f"{context}: expected a relative file path, got {raw!r}")
    target = ((base or root) / Path(*posix.parts)).resolve()
    resolved_root = root.resolve()
    try:
        target.relative_to(resolved_root)
    except ValueError as exc:
        raise ValidationError(f"{context}: path escapes the mod root: {raw!r}") from exc
    return target


def _read_manifest(root: Path) -> tuple[dict, str]:
    manifest_path = root / "mod.toml"
    if not manifest_path.is_file():
        raise ValidationError(f"missing {manifest_path}")
    try:
        manifest = _parse_simple_toml(manifest_path.read_text(encoding="utf-8"))
    except (OSError, ValueError, SyntaxError) as exc:
        raise ValidationError(f"cannot parse {manifest_path}: {exc}") from exc

    name = manifest.get("name")
    version = manifest.get("version")
    if name != "purr2d2":
        raise ValidationError(f"{manifest_path}: name must be 'purr2d2', got {name!r}")
    if not isinstance(version, str) or not SEMVER.fullmatch(version):
        raise ValidationError(f"{manifest_path}: version must be semantic x.y.z, got {version!r}")
    assets = manifest.get("assets")
    if not isinstance(assets, list) or not assets or not all(isinstance(item, str) for item in assets):
        raise ValidationError(f"{manifest_path}: assets must be a non-empty list of relative paths")
    for item in assets:
        target = _relative_asset_path(root, item, f"{manifest_path} assets")
        if not target.is_file():
            raise ValidationError(f"{manifest_path}: listed asset manifest is missing: {item}")
    required_manifests = {"Assets/purr2d2.xml", "Assets/Sounds.xml", "Assets/PracticeTargets.xml"}
    if not required_manifests.issubset(set(assets)) or len(assets) != len(set(assets)):
        raise ValidationError(f"{manifest_path}: assets must include the saber, sound, and practice-target manifests without duplicates")
    starmap = manifest.get("StarMap")
    if not isinstance(starmap, dict) or starmap.get("EntryAssembly") != "Purr2D2":
        raise ValidationError(f"{manifest_path}: [StarMap].EntryAssembly must be 'Purr2D2'")
    return manifest, version


def _parse_simple_toml(contents: str) -> dict:
    """Read the scalar/string-list TOML subset used by the mod manifest."""
    result: dict = {}
    current = result
    for line_number, raw_line in enumerate(contents.splitlines(), 1):
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            section = line[1:-1].strip()
            if not section or "." in section:
                raise ValueError(f"unsupported section on line {line_number}")
            current = result.setdefault(section, {})
            continue
        if "=" not in line:
            raise ValueError(f"expected key = value on line {line_number}")
        key, raw_value = (piece.strip() for piece in line.split("=", 1))
        if not re.fullmatch(r"[A-Za-z0-9_-]+", key):
            raise ValueError(f"invalid key on line {line_number}")
        if raw_value.startswith(('"', "'", "[")):
            value = ast.literal_eval(raw_value)
        elif raw_value.lower() in {"true", "false"}:
            value = raw_value.lower() == "true"
        else:
            value = raw_value
        current[key] = value
    return result


def _project_version(root: Path) -> str:
    project_path = root / "Purr2D2.csproj"
    if not project_path.is_file():
        raise ValidationError(f"missing {project_path}")
    try:
        tree = ET.parse(project_path)
    except (OSError, ET.ParseError) as exc:
        raise ValidationError(f"cannot parse {project_path}: {exc}") from exc
    versions = [node.text.strip() for node in tree.iter() if node.tag.rsplit("}", 1)[-1] == "Version" and node.text]
    if len(versions) != 1:
        raise ValidationError(f"{project_path}: expected exactly one Version property")
    version = versions[0]
    if not SEMVER.fullmatch(version):
        raise ValidationError(f"{project_path}: invalid semantic version {version!r}")
    return version


def _wave_info(path: Path, context: str) -> tuple[int, int]:
    try:
        with wave.open(str(path), "rb") as audio:
            channels = audio.getnchannels()
            sample_rate = audio.getframerate()
            frames = audio.getnframes()
            width = audio.getsampwidth()
            compression = audio.getcomptype()
    except (OSError, wave.Error, EOFError) as exc:
        raise ValidationError(f"{context}: invalid WAV: {exc}") from exc
    if channels != 1:
        raise ValidationError(f"{context}: expected mono audio, found {channels} channels")
    if not 8_000 <= sample_rate <= 192_000:
        raise ValidationError(f"{context}: sample rate {sample_rate} Hz is outside 8–192 kHz")
    if frames <= 0:
        raise ValidationError(f"{context}: WAV contains no PCM frames")
    if width not in {1, 2, 3, 4} or compression != "NONE":
        raise ValidationError(f"{context}: expected non-empty uncompressed PCM WAV")
    return sample_rate, frames


def _validate_png(path: Path, context: str) -> None:
    try:
        with path.open("rb") as stream:
            header = stream.read(24)
    except OSError as exc:
        raise ValidationError(f"{context}: cannot read PNG: {exc}") from exc
    if len(header) != 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise ValidationError(f"{context}: invalid PNG signature/IHDR")
    width, height = struct.unpack(">II", header[16:24])
    if width <= 0 or height <= 0:
        raise ValidationError(f"{context}: PNG has empty dimensions")


def _gltf_accessor(document: dict, buffers: list[dict], bin_bytes: bytes, accessor_index: int,
                   expected_type: str, context: str) -> tuple[int, list[float] | None]:
    accessors = document.get("accessors", [])
    views = document.get("bufferViews", [])
    if not isinstance(accessors, list) or not isinstance(views, list):
        raise ValidationError(f"{context}: glTF accessors or bufferViews are malformed")
    if not isinstance(accessor_index, int) or accessor_index < 0 or accessor_index >= len(accessors):
        raise ValidationError(f"{context}: invalid accessor index {accessor_index!r}")
    accessor = accessors[accessor_index]
    if not isinstance(accessor, dict):
        raise ValidationError(f"{context}: accessor is malformed")
    if accessor.get("type") != expected_type:
        raise ValidationError(f"{context}: expected {expected_type} accessor, found {accessor.get('type')!r}")
    count = accessor.get("count")
    if not isinstance(count, int) or count <= 0:
        raise ValidationError(f"{context}: accessor is empty or has invalid count")
    component_size = COMPONENT_BYTES.get(accessor.get("componentType"))
    components = TYPE_COMPONENTS.get(expected_type)
    if component_size is None or components is None:
        raise ValidationError(f"{context}: unsupported glTF accessor type/componentType")
    view_index = accessor.get("bufferView")
    if not isinstance(view_index, int) or view_index < 0 or view_index >= len(views):
        raise ValidationError(f"{context}: accessor must reference a non-empty embedded bufferView")
    view = views[view_index]
    if not isinstance(view, dict):
        raise ValidationError(f"{context}: bufferView is malformed")
    if view.get("buffer", 0) != 0:
        raise ValidationError(f"{context}: GLB asset must use its embedded binary buffer")
    view_offset = view.get("byteOffset", 0)
    view_length = view.get("byteLength")
    accessor_offset = accessor.get("byteOffset", 0)
    stride = view.get("byteStride", component_size * components)
    element_size = component_size * components
    if not all(isinstance(value, int) and value >= 0 for value in (view_offset, view_length, accessor_offset, stride)):
        raise ValidationError(f"{context}: invalid byte offsets in accessor")
    if stride < element_size:
        raise ValidationError(f"{context}: byteStride is smaller than the attribute element")
    end = accessor_offset + (count - 1) * stride + element_size
    if end > view_length or view_offset + end > len(bin_bytes):
        raise ValidationError(f"{context}: accessor data exceeds its GLB bufferView")
    if accessor.get("componentType") == 5126:
        values: list[float] = []
        component_format = {2: "e", 4: "f"}.get(component_size)
        if component_format:
            for row in range(min(count, 1024)):
                row_offset = view_offset + accessor_offset + row * stride
                values.extend(struct.unpack_from("<" + component_format * components, bin_bytes, row_offset))
            if not all(math.isfinite(value) for value in values):
                raise ValidationError(f"{context}: accessor contains non-finite values")
        return count, values
    return count, None


def _validate_glb(path: Path, root: Path, expected_mesh_name: str | None = None) -> None:
    context = path.relative_to(root).as_posix()
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise ValidationError(f"{context}: cannot read GLB: {exc}") from exc
    if len(data) < 20:
        raise ValidationError(f"{context}: GLB is truncated")
    magic, version, declared_length = struct.unpack_from("<4sII", data)
    if magic != b"glTF" or version != 2 or declared_length != len(data):
        raise ValidationError(f"{context}: expected valid glTF 2 binary header and exact total length")
    offset = 12
    document = None
    bin_chunks: list[bytes] = []
    chunk_index = 0
    while offset < len(data):
        if offset + 8 > len(data):
            raise ValidationError(f"{context}: truncated GLB chunk header")
        chunk_length, chunk_type = struct.unpack_from("<II", data, offset)
        offset += 8
        if chunk_length % 4 or offset + chunk_length > len(data):
            raise ValidationError(f"{context}: invalid GLB chunk length")
        chunk = data[offset:offset + chunk_length]
        offset += chunk_length
        if chunk_index == 0 and chunk_type != GLB_JSON_CHUNK:
            raise ValidationError(f"{context}: JSON must be the first GLB chunk")
        if chunk_type == GLB_JSON_CHUNK:
            if document is not None:
                raise ValidationError(f"{context}: multiple JSON chunks")
            try:
                document = json.loads(chunk.decode("utf-8").rstrip(" \t\r\n\0"))
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise ValidationError(f"{context}: invalid glTF JSON chunk: {exc}") from exc
        elif chunk_type == GLB_BIN_CHUNK:
            bin_chunks.append(chunk)
        chunk_index += 1
    if not isinstance(document, dict):
        raise ValidationError(f"{context}: missing glTF JSON chunk")
    buffers = document.get("buffers")
    if not isinstance(buffers, list) or len(buffers) != 1 or not isinstance(buffers[0], dict) or "uri" in buffers[0]:
        raise ValidationError(f"{context}: expected a single embedded GLB binary buffer")
    if len(bin_chunks) != 1:
        raise ValidationError(f"{context}: expected exactly one GLB BIN chunk")
    binary = bin_chunks[0]
    declared_bin_length = buffers[0].get("byteLength")
    if not isinstance(declared_bin_length, int) or declared_bin_length <= 0 or declared_bin_length > len(binary):
        raise ValidationError(f"{context}: invalid embedded buffer byteLength")
    buffer_views = document.get("bufferViews", [])
    if not isinstance(buffer_views, list):
        raise ValidationError(f"{context}: glTF bufferViews are malformed")
    for index, view in enumerate(buffer_views):
        if not isinstance(view, dict):
            raise ValidationError(f"{context}: bufferView {index} is malformed")
        if view.get("buffer", 0) != 0:
            raise ValidationError(f"{context}: bufferView {index} does not use the embedded buffer")
        start = view.get("byteOffset", 0)
        length = view.get("byteLength")
        if not isinstance(start, int) or not isinstance(length, int) or start < 0 or length <= 0 or start + length > declared_bin_length:
            raise ValidationError(f"{context}: bufferView {index} exceeds the embedded buffer")
    images = document.get("images", [])
    if not isinstance(images, list):
        raise ValidationError(f"{context}: glTF images are malformed")
    for image in images:
        if not isinstance(image, dict):
            raise ValidationError(f"{context}: glTF image is malformed")
        uri = image.get("uri")
        if uri is None:
            continue
        if not isinstance(uri, str):
            raise ValidationError(f"{context}: glTF image URI must be a string")
        if uri.startswith("data:"):
            continue
        external = _relative_asset_path(root, uri, f"{context} image URI", base=path.parent)
        if not external.is_file():
            raise ValidationError(f"{context}: referenced image is missing: {uri}")

    meshes = document.get("meshes")
    if not isinstance(meshes, list) or not meshes or not all(isinstance(mesh, dict) for mesh in meshes):
        raise ValidationError(f"{context}: glTF contains no meshes")
    mesh_names = [mesh.get("name") for mesh in meshes]
    if not all(isinstance(name, str) and name for name in mesh_names) or len(mesh_names) != len(set(mesh_names)):
        raise ValidationError(f"{context}: every mesh must have a unique non-empty name")
    if expected_mesh_name is not None and mesh_names != [expected_mesh_name]:
        raise ValidationError(f"{context}: expected exactly one mesh named {expected_mesh_name!r}, found {mesh_names!r}")
    nodes = document.get("nodes", [])
    if not isinstance(nodes, list) or not all(isinstance(node, dict) for node in nodes):
        raise ValidationError(f"{context}: glTF nodes are malformed")
    for mesh_index, mesh in enumerate(meshes):
        mesh_name = mesh["name"]
        matching_nodes = [node for node in nodes
                          if node.get("mesh") == mesh_index and node.get("name") == mesh_name]
        if len(matching_nodes) != 1:
            raise ValidationError(f"{context}: expected one node named {mesh_name!r} using mesh {mesh_index}")
        primitives = mesh.get("primitives")
        if not isinstance(primitives, list) or not primitives or not all(isinstance(item, dict) for item in primitives):
            raise ValidationError(f"{context}: mesh {mesh_name!r} has no primitives")
        for primitive_index, primitive in enumerate(primitives):
            if primitive.get("mode", 4) != 4:
                raise ValidationError(f"{context}: mesh {mesh_name!r} primitive {primitive_index} must use triangle mode")
            attributes = primitive.get("attributes", {})
            if not isinstance(attributes, dict):
                raise ValidationError(f"{context}: mesh {mesh_name!r} primitive {primitive_index} attributes are malformed")
            prefix = f"{context} mesh {mesh_name} primitive {primitive_index}"
            if "POSITION" not in attributes or "NORMAL" not in attributes or "TEXCOORD_0" not in attributes:
                raise ValidationError(f"{prefix}: POSITION, NORMAL, and TEXCOORD_0 are required")
            position_count, _ = _gltf_accessor(document, buffers, binary, attributes["POSITION"], "VEC3", prefix + " POSITION")
            normal_count, normal_values = _gltf_accessor(document, buffers, binary, attributes["NORMAL"], "VEC3", prefix + " NORMAL")
            uv_count, _ = _gltf_accessor(document, buffers, binary, attributes["TEXCOORD_0"], "VEC2", prefix + " TEXCOORD_0")
            if not (position_count == normal_count == uv_count):
                raise ValidationError(f"{prefix}: position, normal, and UV vertex counts differ")
            if normal_values is not None and not any(abs(value) > 1e-8 for value in normal_values):
                raise ValidationError(f"{prefix}: NORMAL data contains only zero vectors")


def _xml_path_references(root: Path, xml_path: Path, issues: list[str]) -> list[tuple[Path, ET.Element]]:
    refs: list[tuple[Path, ET.Element]] = []
    try:
        tree = ET.parse(xml_path)
    except (OSError, ET.ParseError) as exc:
        issues.append(f"{xml_path.relative_to(root).as_posix()}: invalid XML: {exc}")
        return refs
    for element in tree.iter():
        for key, value in element.attrib.items():
            if key.rsplit("}", 1)[-1].lower() != "path":
                continue
            context = f"{xml_path.relative_to(root).as_posix()} <{element.tag.rsplit('}', 1)[-1]} Path>"
            try:
                target = _relative_asset_path(root, value, context)
            except ValidationError as exc:
                issues.append(str(exc))
                continue
            if not target.is_file():
                issues.append(f"{context}: referenced file is missing: {value}")
            else:
                refs.append((target, element))
    return refs


def _check_xml_loop_bounds(root: Path, xml_path: Path, refs: list[tuple[Path, ET.Element]],
                           wave_cache: dict[Path, tuple[int, int]], issues: list[str]) -> None:
    try:
        tree = ET.parse(xml_path)
    except (OSError, ET.ParseError):
        return
    for sound_file in tree.iter():
        if sound_file.tag.rsplit("}", 1)[-1].lower() != "soundfile":
            continue
        raw_path = next((v for k, v in sound_file.attrib.items() if k.rsplit("}", 1)[-1].lower() == "path"), None)
        loops = [child for child in sound_file if child.tag.rsplit("}", 1)[-1].lower() == "loop"]
        if not loops:
            continue
        if raw_path is None:
            issues.append(f"{xml_path.relative_to(root).as_posix()}: <SoundFile> loop has no Path")
            continue
        try:
            audio_path = _relative_asset_path(root, raw_path, f"{xml_path.relative_to(root).as_posix()} loop")
            # Full-file compressed loops let FMOD use the decoded sample count.
            # This validator only supports explicit PCM bounds for WAV assets.
            if audio_path.suffix.lower() == ".ogg":
                if any(len(loop) for loop in loops):
                    issues.append(f"{raw_path}: Ogg loops must use whole-file <Loop /> bounds")
                continue
            if audio_path not in wave_cache:
                wave_cache[audio_path] = _wave_info(audio_path, f"{xml_path.relative_to(root).as_posix()} -> {raw_path}")
            _, frame_count = wave_cache[audio_path]
        except ValidationError as exc:
            issues.append(str(exc))
            continue
        for loop in loops:
            values: dict[str, tuple[int, str]] = {}
            for child in loop:
                key = child.tag.rsplit("}", 1)[-1].lower()
                if key not in {"start", "end"}:
                    continue
                try:
                    value = int(child.attrib["Value"])
                    unit = child.attrib["Unit"]
                except (KeyError, ValueError):
                    issues.append(f"{xml_path.relative_to(root).as_posix()}: loop {key} needs integer Value and Unit")
                    continue
                values[key] = (value, unit)
            if "start" not in values or "end" not in values:
                issues.append(f"{xml_path.relative_to(root).as_posix()}: loop must define Start and End")
                continue
            start, start_unit = values["start"]
            end, end_unit = values["end"]
            if start_unit.lower() not in {"pcm", "frame", "frames"} or end_unit.lower() not in {"pcm", "frame", "frames"}:
                issues.append(f"{xml_path.relative_to(root).as_posix()}: loop bounds must use PCM frames")
            if start < 0 or end < 0 or start >= frame_count or end >= frame_count or end <= start:
                issues.append(f"{xml_path.relative_to(root).as_posix()}: loop bounds {start}..{end} exceed {frame_count} PCM frames")


def _validate_asset_tree(root: Path, label: str) -> list[str]:
    issues: list[str] = []
    if not root.is_dir():
        return [f"{label} directory does not exist: {root}"]
    try:
        manifest, _ = _read_manifest(root)
    except ValidationError as exc:
        return [f"{label}: {exc}"]
    entries = list(root.rglob("*"))
    files = [path for path in entries if path.is_file()]
    for path in entries:
        if path.is_symlink():
            issues.append(f"{label}: symlink is not allowed: {path.relative_to(root).as_posix()}")
    assets_root = root / "Assets"
    if not assets_root.is_dir():
        issues.append(f"{label}: missing Assets directory")
        return issues
    for style, stem in SHADER_PAIRS:
        for suffix in (".vert", ".frag"):
            shader = assets_root / "Shaders" / (stem + suffix)
            if not shader.is_file():
                issues.append(f"{label}: missing {style} saber shader: {shader.relative_to(root).as_posix()}")
    asset_files = [path for path in assets_root.rglob("*") if path.is_file()]
    ext_counts: dict[str, int] = {}
    for path in asset_files:
        suffix = path.suffix.lower()
        ext_counts[suffix] = ext_counts.get(suffix, 0) + 1
        if suffix not in REQUIRED_ASSET_EXTENSIONS and suffix not in {".xml", ".glsl"}:
            issues.append(f"{label}: unsupported runtime asset extension: {path.relative_to(root).as_posix()}")
    for suffix in sorted(REQUIRED_ASSET_EXTENSIONS):
        if ext_counts.get(suffix, 0) < 1:
            issues.append(f"{label}: expected at least one owned {suffix} asset")
    glbs = [path for path in asset_files if path.suffix.lower() == ".glb"]
    hilt = root / "Assets/Models/Purr2D2_Hilt.glb"
    if not hilt.is_file():
        issues.append(f"{label}: missing Assets/Models/Purr2D2_Hilt.glb")
    for glb in glbs:
        try:
            _validate_glb(glb, root, "Purr2D2_Hilt" if glb == hilt else None)
        except ValidationError as exc:
            issues.append(f"{label}: {exc}")

    wave_cache: dict[Path, tuple[int, int]] = {}
    for path in asset_files:
        context = f"{label} {path.relative_to(root).as_posix()}"
        if path.suffix.lower() == ".png":
            try:
                _validate_png(path, context)
            except ValidationError as exc:
                issues.append(str(exc))
        elif path.suffix.lower() == ".ogg":
            header = path.read_bytes()[:64]
            if not header.startswith(b"OggS") or b"\x01vorbis" not in header:
                issues.append(f"{context}: expected an Ogg Vorbis sound")
        elif path.suffix.lower() == ".wav":
            try:
                wave_cache[path.resolve()] = _wave_info(path, context)
            except ValidationError as exc:
                issues.append(str(exc))
        elif path.suffix.lower() in {".vert", ".frag", ".glsl"}:
            try:
                contents = path.read_text(encoding="utf-8").strip()
            except (OSError, UnicodeDecodeError) as exc:
                issues.append(f"{context}: cannot read shader: {exc}")
                continue
            if not contents:
                issues.append(f"{context}: shader is empty")
            # Production entrypoints may live in shared GLSL includes. Compile/link
            # checks establish entrypoint validity; validate packaged dependencies here.
            for include in re.findall(r'^\s*#\s*include\s*[<"]([^>"\r\n]+)[>"]', contents, re.MULTILINE):
                try:
                    target = _relative_asset_path(root, include, context, path.parent)
                    if not target.is_file():
                        issues.append(f"{context}: included shader is missing: {include}")
                except ValidationError as exc:
                    issues.append(str(exc))

    xml_files = sorted(path for path in asset_files if path.suffix.lower() == ".xml")
    for required in (root / "Assets/purr2d2.xml", root / "Assets/Sounds.xml", root / "Assets/PracticeTargets.xml"):
        if required not in xml_files:
            issues.append(f"{label}: required XML asset manifest missing: {required.relative_to(root).as_posix()}")
    for xml_path in xml_files:
        refs = _xml_path_references(root, xml_path, issues)
        _check_xml_loop_bounds(root, xml_path, refs, wave_cache, issues)
    return issues


def validate_source(source: Path) -> tuple[str | None, list[str]]:
    source = source.resolve()
    issues = _validate_asset_tree(source, "source")
    try:
        _, manifest_version = _read_manifest(source)
    except ValidationError as exc:
        issues.append(f"source: {exc}")
        manifest_version = None
    try:
        project_version = _project_version(source)
    except ValidationError as exc:
        issues.append(f"source: {exc}")
        project_version = None
    if manifest_version and project_version and manifest_version != project_version:
        issues.append(f"source: mod.toml version {manifest_version} does not match Purr2D2.csproj version {project_version}")
    return manifest_version, issues


def validate_stage(stage: Path, expected_version: str | None = None) -> list[str]:
    stage = stage.resolve()
    issues = _validate_asset_tree(stage, "stage")
    if not stage.is_dir():
        return issues
    for path in stage.rglob("*"):
        relative = path.relative_to(stage).as_posix()
        if path.is_dir():
            if relative != "Assets" and not relative.startswith("Assets/"):
                issues.append(f"stage: unexpected directory: {relative}")
            continue
        if relative.startswith("Assets/"):
            continue
        if relative not in ROOT_STAGE_FILES:
            issues.append(f"stage: file is not on the package allowlist: {relative}")
    dlls = sorted(path.relative_to(stage).as_posix() for path in stage.rglob("*.dll"))
    if dlls != ["Purr2D2.dll"]:
        issues.append(f"stage: expected exactly one DLL named Purr2D2.dll, found {dlls!r}")
    for required in (stage / "Purr2D2.dll", stage / "Purr2D2.deps.json"):
        if not required.is_file() or required.stat().st_size == 0:
            issues.append(f"stage: required file missing or empty: {required.name}")
    if expected_version:
        try:
            _, staged_version = _read_manifest(stage)
            if staged_version != expected_version:
                issues.append(f"stage: mod.toml version {staged_version} does not match source version {expected_version}")
        except ValidationError:
            pass
    return issues


def run_managed_checks(source: Path, stage: Path | None = None) -> tuple[int, str]:
    source = source.resolve()
    core_sources = [
        source / "src/Core/BladeState.cs",
        source / "src/Core/AssistedSliceSequence.cs",
        source / "src/Core/SaberSwing.cs",
        source / "src/Core/SaberOverlayDrag.cs",
        source / "src/Core/SaberOverlayLayout.cs",
        source / "tests/OverlayDragChecks.cs",
        source / "src/Runtime/SaberRuntime.Activation.cs",
        source / "tests/ActivationChecks.cs",
        source / "src/Core/ForceOverlayDrag.cs",
        source / "src/Core/ForceOverlayLayout.cs",
        source / "tests/force/ForceOverlayChecks.cs",
        source / "src/Core/BackgroundSliceWork.cs",
        source / "tests/BackgroundSliceChecks.cs",
        source / "src/Core/BackgroundJitWarmup.cs",
        source / "tests/BackgroundJitWarmupChecks.cs",
        source / "src/Slicing/SliceJitCallGraph.cs",
        source / "tests/SliceJitCallGraphChecks.cs",
        source / "src/Slicing/SliceDiagnosticCounters.cs",
        source / "src/Slicing/SliceFrameDiagnostics.cs",
        source / "src/Slicing/SliceWorkerDiagnostics.cs",
        source / "src/Slicing/SliceCommitTimings.cs",
        source / "tests/SliceDiagnosticsChecks.cs",
        source / "tests/SliceWorkerDiagnosticsChecks.cs",
        source / "src/Cutting/AssistedSliceGeometry.cs",
        source / "tests/AssistedSliceChecks.cs",
        source / "src/Core/SaberSettings.cs",
        source / "src/Cutting/CutGeometry.cs",
        source / "src/Cutting/PlaneSlicer.cs",
        source / "src/Cutting/SliceShellBuilder.cs",
        source / "src/Slicing/SliceMeshPrecision.cs",
        source / "src/Pose/SaberPoseMath.cs",
        source / "src/Rendering/FragmentGlbWriter.cs",
        source / "src/Slicing/SlicePrincipalFrame.cs",
        source / "src/Slicing/SliceSourceFrame.cs",
        source / "tests/PlaneSliceChecks.cs",
        source / "tests/ShellSliceChecks.cs",
        source / "tests/CoreChecks.cs",
        source / "tests/PoseChecks.cs",
        source / "tests/SlicingPhysicsChecks.cs",
        source / "src/Core/SomersaultMotion.cs",
        source / "src/Core/SomersaultSequence.cs",
        source / "tests/SomersaultMotionChecks.cs",
    ]
    target_sources = [
        source / "src/Cutting/PartSliceTargeting.cs",
        source / "src/Cutting/PartSliceTargeting.Selection.cs",
        source / "src/Cutting/CutGeometry.cs",
        source / "src/Cutting/PlaneSlicer.cs",
        source / "tests/contact/GameStubs.cs",
        source / "tests/contact/TargetSelectionChecks.cs",
        source / "tests/contact/ContactEffectsChecks.cs",
        source / "src/Rendering/SaberContactProbe.cs",
    ]
    core_program = (
        "using System;\n"
        "internal static class Program { private static void Main() { CoreChecks.Run(); Purr2D2.Runtime.SaberRuntime.RunActivationChecks(); OverlayDragChecks.Run(); ForceOverlayChecks.Run(); AssistedSliceChecks.Run(); BackgroundSliceChecks.Run(); BackgroundJitWarmupChecks.Run(); SliceJitCallGraphChecks.Run(); SliceDiagnosticsChecks.Run(); SliceWorkerDiagnosticsChecks.Run(); PlaneSliceChecks.Run(); ShellSliceChecks.Run(); PoseChecks.Run(); SlicingPhysicsChecks.Run(); SomersaultMotionChecks.Run(); } }\n"
    )
    kitten_sources = [
        source / "src/Runtime/SaberRuntime.Kittens.cs",
        source / "src/Pose/SaberPoseController.cs",
        source / "src/Pose/SaberPoseMath.cs",
        source / "src/Core/BladeState.cs",
        source / "src/Core/SaberSwing.cs",
        source / "src/Core/SaberSettings.cs",
        source / "src/Core/AssistedSliceSequence.cs",
        source / "tests/kittens/HostStubs.cs",
        source / "tests/kittens/KittenSaberChecks.cs",
    ]
    fragment_sources = [
        source / "src/Slicing/DetachedSliceFragment.cs",
        source / "tests/slicing/GameStubs.cs",
        source / "tests/slicing/DetachedSliceChecks.cs",
    ]
    isolation_sources = [
        source / "src/Slicing/NativeSliceIsolation.cs",
        source / "src/Slicing/SliceCommitTimings.cs",
        source / "src/Slicing/SliceDiagnosticCounters.cs",
        source / "src/Slicing/SliceFrameDiagnostics.cs",
        source / "src/Slicing/SliceWorkerDiagnostics.cs",
        source / "tests/slicing/IsolationHostStubs.cs",
        source / "tests/slicing/IsolationChecks.cs",
    ]
    fragment_asset_sources = [
        source / "src/Rendering/FragmentTopology.cs",
        source / "src/Cutting/CutGeometry.cs",
        source / "src/Cutting/PlaneSlicer.cs",
        source / "src/Cutting/SliceShellBuilder.cs",
        source / "src/Cutting/AssistedSliceGeometry.cs",
        source / "src/Slicing/SlicePrincipalFrame.cs",
        source / "src/Slicing/SliceMeshPrecision.cs",
        source / "src/Slicing/SliceColliderGeometry.cs",
        source / "src/Slicing/SliceGeometryWork.cs",
        source / "src/Rendering/FragmentGlbWriter.cs",
        source / "src/Rendering/FragmentAssetWriter.cs",
        source / "tests/FragmentAssetChecks.cs",
    ]
    somersault_runtime_sources = [
        source / "src/Core/SomersaultMotion.cs",
        source / "src/Core/AssistedSliceSequence.cs",
        source / "src/Core/SaberSwing.cs",
        source / "src/Runtime/SaberRuntime.Somersault.cs",
        source / "tests/somersault/RuntimeHostStubs.cs",
        source / "tests/somersault/RuntimeChecks.cs",
    ]
    somersault_worker_sources = [
        source / "src/Core/SomersaultMotion.cs",
        source / "src/Core/SomersaultSequence.cs",
        source / "src/Runtime/SomersaultMotionController.cs",
        source / "tests/motion/HostStubs.cs",
        source / "tests/motion/SomersaultWorkerChecks.cs",
    ]
    somersault_pose_sources = [
        source / "src/Core/SomersaultMotion.cs",
        source / "src/Core/SaberSwing.cs",
        source / "src/Pose/SaberPoseController.cs",
        source / "src/Pose/SaberPoseMath.cs",
        source / "src/Pose/SomersaultPoseController.cs",
        source / "src/Pose/SomersaultPoseMath.cs",
        source / "tests/somersault/PoseHostStubs.cs",
        source / "tests/SomersaultPoseChecks.cs",
    ]
    force_pose_sources = [*somersault_pose_sources[:-1],
        source / "src/Pose/ForcePoseMath.cs",
        source / "src/Pose/ForcePoseController.cs",
        source / "tests/ForcePoseChecks.cs",
    ]
    force_math_sources = [source / "src/Force/ForceMath.cs", source / "src/Cutting/CutGeometry.cs",
                          source / "tests/force/ForceMathChecks.cs"]
    force_ui_sources = [source / "src/Runtime/SaberRuntime.Force.cs",
        source / "src/Core/ForceOverlayDrag.cs", source / "src/Core/ForceOverlayLayout.cs",
        source / "src/Core/SaberOverlayDrag.cs", source / "src/Core/SaberOverlayLayout.cs",
        source / "tests/force/ForceUiChecks.cs", source / "tests/force/ForceUiHostStubs.cs",
        source / "tests/force/ForceOverlayChecks.cs"]
    force_controller_sources = [source / "src/Force/ForceController.cs", source / "src/Force/ForceController.Follow.cs",
        source / "src/Force/ForceTarget.cs", source / "src/Force/ForceTargeting.Proximity.cs",
        source / "src/Force/ForcePhysicsPatches.cs", source / "src/Force/ForceMath.cs",
        source / "src/Cutting/CutGeometry.cs", source / "tests/force/ControllerHostStubs.cs",
        source / "tests/force/ForceControllerChecks.cs"]
    shader_code, shader_output = run_shader_checks(source, stage)
    outputs = [shader_output]
    result = shader_code
    vfx_sources = [source / "src/Rendering/SaberVfx.cs", source / "src/Core/SaberSettings.cs", source / "tests/SaberVfxChecks.cs",
                   source / "artifacts/checks/SaberVfxChecks/GlowPush.cs"]
    for name, required, program in (
        ("CoreChecks", core_sources, core_program),
        ("TargetSelectionChecks", target_sources, None),
        ("KittenSaberChecks", kitten_sources, None),
        ("AudioChecks", [source / "src/Audio/SaberAudio.cs", source / "src/Core/SaberSettings.cs",
                         source / "tests/audio/AudioChecks.cs"], None),
        ("DetachedSliceChecks", fragment_sources, None),
        ("IsolationChecks", isolation_sources, None),
        ("FragmentAssetChecks", fragment_asset_sources,
         "internal static class Program { static void Main() => FragmentAssetChecks.Run(); }"),
        ("SomersaultRuntimeChecks", somersault_runtime_sources, None),
        ("SomersaultWorkerChecks", somersault_worker_sources, None),
        ("SomersaultPoseChecks", somersault_pose_sources, None),
        ("ForcePoseChecks", force_pose_sources, None),
        ("ForceMathChecks", force_math_sources, None),
        ("ForceUiChecks", force_ui_sources, None),
        ("ForceControllerChecks", force_controller_sources, None),
        ("SaberVfxChecks", vfx_sources, None),
    ):
        code, output = _run_managed_project(source, name, required, program,
                                           harmony=name in ("SomersaultWorkerChecks", "ForceControllerChecks"))
        outputs.append(output)
        result = result or code
    return result, "\n".join(outputs)


def _run_managed_project(source: Path, name: str, required: list[Path], program: str | None,
                         *, harmony: bool = False) -> tuple[int, str]:
    """Generate isolated projects so game stubs cannot leak into the core checks or mod."""
    numerics = (source.parent / "ksa-game-assemblies/current/dll/Brutal.Numerics.dll").resolve()
    missing = [path.relative_to(source).as_posix() for path in required if not path.is_file()]
    if not numerics.is_file():
        missing.append("../ksa-game-assemblies/current/dll/Brutal.Numerics.dll")
    if missing:
        return 1, "managed checks cannot be generated; missing: " + ", ".join(missing)
    check_dir = source / "artifacts/checks" / name
    check_dir.mkdir(parents=True, exist_ok=True)
    project_path = check_dir / f"{name}.csproj"
    if program is not None:
        program_path = check_dir / "Program.cs"
        program_path.write_text(program, encoding="utf-8")
        required = [*required, program_path]
    required = [*required, source / "src/Core/SaberLimits.cs"]
    includes = "\n".join(
        f'    <Compile Include="{Path(os.path.relpath(path, check_dir)).as_posix()}" />'
        for path in required
    )
    numerics_path = Path(os.path.relpath(numerics, check_dir)).as_posix()
    extra_references = "\n".join(f'<Reference Include="{name}"><HintPath>{Path(numerics_path).with_name(name + ".dll").as_posix()}</HintPath><Private>true</Private></Reference>' for name in ("BepuPhysics", "BepuUtilities"))
    extra_packages = '<PackageReference Include="Lib.Harmony" Version="2.4.2" />' if harmony else ''
    project = textwrap.dedent(f"""\
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
          </PropertyGroup>
          <ItemGroup>
        {includes}
          </ItemGroup>
          <ItemGroup>
            {extra_references}
            {extra_packages}
            <Reference Include="Brutal.Numerics">
              <HintPath>{numerics_path}</HintPath>
              <Private>true</Private>
            </Reference>
          </ItemGroup>
        </Project>
        """)
    project_path.write_text(project, encoding="utf-8")
    try:
        completed = subprocess.run(
            ["dotnet", "run", "--project", str(project_path), "--configuration", "Release", "--no-launch-profile"],
            cwd=source,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            check=False,
            timeout=300,
        )
    except FileNotFoundError:
        return 1, "managed checks require the .NET 10 SDK (`dotnet` was not found on PATH)"
    except subprocess.TimeoutExpired as exc:
        output = exc.stdout or ""
        if isinstance(output, bytes):
            output = output.decode("utf-8", errors="replace")
        return 1, "managed checks timed out after 300 seconds\n" + output
    return completed.returncode, completed.stdout


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE, help="mod source root (default: repository root)")
    parser.add_argument("--stage", type=Path, help="optional staging directory; defaults to artifacts/stage/purr2d2 when present")
    parser.add_argument("--run-tests", action="store_true", help="compile/link saber GLSL and run the isolated .NET 10 managed checks")
    args = parser.parse_args(argv)

    version, issues = validate_source(args.source)
    stage = args.stage
    if stage is None:
        candidate = args.source / "artifacts/stage/purr2d2"
        stage = candidate if candidate.is_dir() else None
    if stage is not None:
        issues.extend(validate_stage(stage, version))
    test_output = ""
    if args.run_tests:
        test_code, test_output = run_managed_checks(args.source, stage)
        if test_code != 0:
            issues.append("managed checks failed (see managed test output above)")
        print(test_output, end="" if test_output.endswith("\n") else "\n")
    if issues:
        print("Validation failed:", file=sys.stderr)
        for issue in issues:
            print(f"- {issue}", file=sys.stderr)
        return 1
    print(f"PASS: source assets and metadata (version {version})")
    if stage is not None:
        print(f"PASS: allowlisted stage at {stage.resolve()}")
    else:
        print("INFO: no staging directory found; stage checks skipped")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
