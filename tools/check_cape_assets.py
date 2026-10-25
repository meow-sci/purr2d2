#!/usr/bin/env python3
"""Validate the Purr2D2 cape GLB, rig contract, cloth maps, and XML paths.

Run from any directory:
    python3 tools/check_cape_assets.py
    python3 tools/check_cape_assets.py --root /path/to/staged/purr2d2

This is a read-only check and uses only the Python standard library.
"""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path, PurePosixPath, PureWindowsPath

GLB_JSON = 0x4E4F534A
GLB_BIN = 0x004E4942
COMPONENTS = {5121: ("B", 1), 5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4)}
SHAPES = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


class CheckError(Exception):
    pass


def require(condition, message):
    if not condition:
        raise CheckError(message)


def close(a, b, tolerance=2e-6):
    return abs(a - b) <= tolerance


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def unit(a):
    length = math.sqrt(dot(a, a))
    require(length > 1e-12, "degenerate triangle or normal")
    return tuple(value / length for value in a)


def read_glb(path):
    raw = path.read_bytes()
    require(len(raw) >= 20, f"{path}: truncated GLB")
    magic, version, declared = struct.unpack_from("<4sII", raw, 0)
    require(magic == b"glTF" and version == 2 and declared == len(raw), f"{path}: invalid GLB 2 header")
    offset = 12
    doc = None
    binary = None
    first = True
    while offset < len(raw):
        require(offset + 8 <= len(raw), f"{path}: truncated chunk header")
        length, kind = struct.unpack_from("<II", raw, offset)
        offset += 8
        require(length % 4 == 0 and offset + length <= len(raw), f"{path}: invalid chunk length")
        payload = raw[offset:offset + length]
        offset += length
        if first:
            require(kind == GLB_JSON, f"{path}: JSON must be first chunk")
            first = False
        if kind == GLB_JSON:
            require(doc is None, f"{path}: multiple JSON chunks")
            doc = json.loads(payload.decode("utf-8").rstrip(" \t\r\n\0"))
        elif kind == GLB_BIN:
            require(binary is None, f"{path}: multiple BIN chunks")
            binary = payload
    require(isinstance(doc, dict) and isinstance(binary, bytes), f"{path}: missing JSON or BIN chunk")
    require(len(doc.get("buffers", [])) == 1 and "uri" not in doc["buffers"][0], f"{path}: expected one embedded buffer")
    require(doc["buffers"][0]["byteLength"] <= len(binary), f"{path}: BIN chunk is shorter than buffer.byteLength")
    return doc, binary


def accessor_values(doc, binary, index, expected_type=None):
    accessors = doc.get("accessors", [])
    views = doc.get("bufferViews", [])
    require(isinstance(index, int) and 0 <= index < len(accessors), f"bad accessor index {index!r}")
    accessor = accessors[index]
    kind = accessor.get("type")
    if expected_type is not None:
        require(kind == expected_type, f"accessor {index}: expected {expected_type}, got {kind!r}")
    component_type = accessor.get("componentType")
    require(component_type in COMPONENTS, f"accessor {index}: unsupported component type {component_type!r}")
    fmt, byte_size = COMPONENTS[component_type]
    shape = SHAPES.get(kind)
    require(shape is not None, f"accessor {index}: unsupported shape {kind!r}")
    count = accessor.get("count")
    view_index = accessor.get("bufferView")
    require(isinstance(count, int) and count > 0, f"accessor {index}: empty or invalid count")
    require(isinstance(view_index, int) and 0 <= view_index < len(views), f"accessor {index}: missing buffer view")
    view = views[view_index]
    require(view.get("buffer", 0) == 0, f"accessor {index}: must use embedded buffer")
    view_start = view.get("byteOffset", 0)
    view_length = view.get("byteLength")
    element_size = byte_size * shape
    stride = view.get("byteStride", element_size)
    start = view_start + accessor.get("byteOffset", 0)
    require(isinstance(view_length, int) and stride >= element_size, f"accessor {index}: malformed view/stride")
    require(start >= view_start and start + (count - 1) * stride + element_size <= view_start + view_length,
            f"accessor {index}: data exceeds buffer view")
    values = []
    for row in range(count):
        chunk = struct.unpack_from("<" + fmt * shape, binary, start + row * stride)
        values.append(chunk[0] if shape == 1 else chunk)
    return values


def cape_rest(u, t):
    r = 2.0 * u - 1.0
    return (
        (u - 0.5) * (0.48 + 0.38 * t),
        0.10 - 0.50 * t,
        -0.23 + 0.04 * r * r * (1.0 - t) - 0.04 * t
        + 0.018 * math.sin(u * 4.0 * math.pi) * math.sin(t * math.pi / 2.0)
        + 0.006 * math.sin(u * 8.0 * math.pi) * math.sin(t * 12.0 * math.pi)
        + 0.003 * math.sin(u * 16.0 * math.pi) * math.sin(t * 6.0 * math.pi),
    )


def expected_weights(u, t):
    gx, gy = u * 8.0, t * 12.0
    c0, r0 = min(8, int(gx)), min(12, int(gy))
    c1, r1 = min(8, c0 + 1), min(12, r0 + 1)
    fx, fy = gx - c0, gy - r0
    source = ((r0 * 9 + c0, (1 - fx) * (1 - fy)),
              (r0 * 9 + c1, fx * (1 - fy)),
              (r1 * 9 + c0, (1 - fx) * fy),
              (r1 * 9 + c1, fx * fy))
    result = {}
    for joint, weight in source:
        result[joint + 1] = result.get(joint + 1, 0.0) + weight
    return result


def check_glb(path, expected, root):
    context = path.relative_to(root).as_posix()
    require(expected.get("kind") == "cape", f"{context}: only the cape GLB is supported")
    doc, binary = read_glb(path)
    meshes = doc.get("meshes")
    nodes = doc.get("nodes")
    skins = doc.get("skins")
    require(len(meshes) == 1, f"{context}: expected exactly one mesh")
    require(len(skins) == 1, f"{context}: expected exactly one skin")
    mesh = meshes[0]
    require(mesh.get("name") == expected["mesh"], f"{context}: mesh must be named {expected['mesh']}")
    primitives = mesh.get("primitives")
    require(len(primitives) == 1, f"{context}: expected one primitive/material slot")
    primitive = primitives[0]
    require(primitive.get("mode", 4) == 4 and primitive.get("material") == 0,
            f"{context}: expected triangle primitive with material index 0")
    attributes = primitive.get("attributes", {})
    required = {"POSITION": "VEC3", "NORMAL": "VEC3", "TEXCOORD_0": "VEC2",
                "TANGENT": "VEC4", "JOINTS_0": "VEC4", "WEIGHTS_0": "VEC4"}
    decoded = {}
    for name, kind in required.items():
        require(name in attributes, f"{context}: missing {name}")
        decoded[name] = accessor_values(doc, binary, attributes[name], kind)
    vertex_count = len(decoded["POSITION"])
    require(all(len(decoded[name]) == vertex_count for name in required), f"{context}: vertex attribute counts differ")
    indices = accessor_values(doc, binary, primitive["indices"], "SCALAR")
    require(len(indices) % 3 == 0, f"{context}: index count is not divisible by three")
    require(all(isinstance(i, int) and 0 <= i < vertex_count for i in indices), f"{context}: index outside vertex range")
    require(len(doc.get("materials", [])) == 1, f"{context}: one material slot is required")
    gltf_material = doc["materials"][0]
    require(gltf_material.get("name") == "Purr2D2_Cloth_Light" and gltf_material.get("doubleSided") is True,
            f"{context}: expected the named, two-sided default cloth material")
    require(gltf_material["pbrMetallicRoughness"].get("metallicFactor") == 0.0,
            f"{context}: material default must be non-metallic")

    pos = decoded["POSITION"]
    normals = decoded["NORMAL"]
    tangents = decoded["TANGENT"]
    uvs = decoded["TEXCOORD_0"]
    joint_values = decoded["JOINTS_0"]
    weight_values = decoded["WEIGHTS_0"]
    for i, (normal, tangent, uv, joints, weights) in enumerate(zip(normals, tangents, uvs, joint_values, weight_values)):
        require(all(math.isfinite(v) for v in (*normal, *tangent, *uv, *weights)), f"{context}: vertex {i} has non-finite data")
        require(0.97 <= math.sqrt(sum(v * v for v in normal)) <= 1.03, f"{context}: vertex {i} normal is not unit length")
        require(0.97 <= math.sqrt(sum(v * v for v in tangent[:3])) <= 1.03 and abs(abs(tangent[3]) - 1.0) < 1e-5,
                f"{context}: vertex {i} tangent is invalid")
        require(abs(dot(normal, tangent[:3])) < 1e-3,
                f"{context}: vertex {i} tangent is not orthogonal to its normal")
        require(all(-1e-6 <= v <= 1.000001 for v in uv), f"{context}: vertex {i} UV is outside [0,1]")
        require(all(math.isfinite(v) and v >= -1e-7 for v in weights), f"{context}: vertex {i} has negative skin weight")
        require(close(sum(weights), 1.0, 1e-5), f"{context}: vertex {i} skin weights do not sum to one")
        require(all(int(j) == j and 0 <= j < len(skins[0]["joints"]) for j in joints),
                f"{context}: vertex {i} joint index is outside skin.joints")
    for tri in range(0, len(indices), 3):
        face = indices[tri:tri + 3]
        edge_a = sub(pos[face[1]], pos[face[0]])
        edge_b = sub(pos[face[2]], pos[face[0]])
        face_normal = unit(cross(edge_a, edge_b))
        smooth_normal = unit(tuple(sum(normals[index][axis] for index in face) for axis in range(3)))
        require(dot(face_normal, smooth_normal) > 0.5,
                f"{context}: triangle {tri // 3} winding disagrees with its smooth normals")

    skin = skins[0]
    require(skin.get("skeleton") == expected["root_index"], f"{context}: skin.skeleton must be the identity root joint")
    require(skin.get("joints") == expected["joint_nodes"], f"{context}: skin.joints order differs from contract")
    bind_accessor = skin.get("inverseBindMatrices")
    require(bind_accessor is not None, f"{context}: inverse bind matrices are required")
    inverse_binds = accessor_values(doc, binary, bind_accessor, "MAT4")
    require(len(inverse_binds) == len(skin["joints"]), f"{context}: inverse bind count differs from joints")
    for joint, matrix in enumerate(inverse_binds):
        require(all(math.isfinite(value) for value in matrix), f"{context}: bind matrix {joint} has non-finite value")
        require(all(close(matrix[i], expected["binds"][joint][i]) for i in range(16)),
                f"{context}: inverse bind matrix {joint} differs from expected bind pose")

    mesh_nodes = [i for i, node in enumerate(nodes) if node.get("mesh") == 0]
    require(len(mesh_nodes) == 1, f"{context}: expected one mesh node")
    mesh_node_index = mesh_nodes[0]
    mesh_node = nodes[mesh_node_index]
    require(mesh_node.get("name") == expected["mesh"] and mesh_node.get("skin") == 0,
            f"{context}: mesh node name/skin reference is incorrect")
    require(mesh_node_index not in expected["joint_nodes"], f"{context}: mesh node must be outside the rig hierarchy")
    require(mesh_node.get("translation", [0, 0, 0]) == [0, 0, 0]
            and mesh_node.get("rotation", [0, 0, 0, 1]) == [0, 0, 0, 1]
            and mesh_node.get("scale", [1, 1, 1]) == [1, 1, 1],
            f"{context}: mesh node transform must be identity")
    scene_nodes = doc["scenes"][doc.get("scene", 0)].get("nodes", [])
    require(mesh_node_index in scene_nodes and expected["root_index"] in scene_nodes,
            f"{context}: rig and mesh roots must be in the scene")
    mesh_parents = {child: i for i, node in enumerate(nodes) for child in node.get("children", [])}
    require(mesh_node_index not in mesh_parents, f"{context}: mesh node must be a scene-root sibling")

    if expected["kind"] == "cape":
        root_node = nodes[0]
        require(root_node.get("name") == "CapeRoot" and root_node.get("children") == list(range(1, 118)),
                f"{context}: CapeRoot must be the first joint and have 117 direct children")
        require(root_node.get("translation", [0, 0, 0]) == [0, 0, 0]
                and root_node.get("rotation", [0, 0, 0, 1]) == [0, 0, 0, 1]
                and root_node.get("scale", [1, 1, 1]) == [1, 1, 1],
                f"{context}: CapeRoot must be identity")
        require(len(nodes) == 119 and len(skin["joints"]) == 118, f"{context}: expected root + 117 joints + mesh node")
        for i in range(117):
            node = nodes[i + 1]
            row, col = divmod(i, 9)
            name = f"Cape_{i:03d}"
            require(node.get("name") == name, f"{context}: missing row-major joint {name}")
            expected_position = cape_rest(col / 8.0, row / 12.0)
            require(all(close(a, b) for a, b in zip(node.get("translation", []), expected_position)),
                    f"{context}: {name} rest translation does not match its formula")
        require(vertex_count == 17 * 25, f"{context}: expected a compact 17x25 cloth mesh")
        for i, (position, joints, weights) in enumerate(zip(pos, joint_values, weight_values)):
            row, col = divmod(i, 17)
            u, t = col / 16.0, row / 24.0
            expected_position = cape_rest(u, t)
            require(all(close(a, b) for a, b in zip(position, expected_position)),
                    f"{context}: mesh vertex {i} differs from generated surface")
            actual = {}
            for joint, weight in zip(joints, weights):
                if weight > 1e-7:
                    actual[int(joint)] = actual.get(int(joint), 0.0) + weight
            expected_weights_for_vertex = expected_weights(u, t)
            expected_weights_for_vertex = {joint: weight for joint, weight in expected_weights_for_vertex.items() if weight > 1e-7}
            require(set(actual) == set(expected_weights_for_vertex), f"{context}: vertex {i} has unexpected joint influences")
            require(all(close(actual[j], weight, 1e-5) for j, weight in expected_weights_for_vertex.items()),
                    f"{context}: vertex {i} has unexpected bilinear weights")
    return vertex_count, len(indices) // 3


def read_rgba_png(path, expected_size=256):
    raw = path.read_bytes()
    require(raw[:8] == b"\x89PNG\r\n\x1a\n" and raw[12:16] == b"IHDR", f"{path}: invalid PNG")
    width, height, depth, color_type = struct.unpack_from(">IIBB", raw, 16)
    require((width, height, depth, color_type) == (expected_size, expected_size, 8, 6),
            f"{path}: expected {expected_size}x{expected_size} RGBA8 texture")
    offset, compressed = 8, bytearray()
    while offset < len(raw):
        length = struct.unpack_from(">I", raw, offset)[0]
        kind = raw[offset + 4:offset + 8]
        payload = raw[offset + 8:offset + 8 + length]
        if kind == b"IDAT":
            compressed.extend(payload)
        offset += 12 + length
    pixels = zlib.decompress(bytes(compressed))
    stride = width * 4
    require(len(pixels) == height * (stride + 1), f"{path}: malformed PNG payload")
    rows = []
    at = 0
    for _ in range(height):
        filter_type = pixels[at]
        require(filter_type == 0, f"{path}: checker expects deterministic filter-0 PNG rows")
        rows.append(pixels[at + 1:at + 1 + stride])
        at += stride + 1
    return width, height, rows


def check_textures(root):
    paths = {
        "light_diffuse": root / "Assets/Textures/Purr2D2_Cloth_Light_Diffuse.png",
        "dark_diffuse": root / "Assets/Textures/Purr2D2_Cloth_Dark_Diffuse.png",
        "dark_normal": root / "Assets/Textures/Purr2D2_Cloth_Normal.png",
        "dark_orm": root / "Assets/Textures/Purr2D2_Cloth_ORM.png",
        "light_normal": root / "Assets/Textures/Purr2D2_Cloth_Light_Normal.png",
        "light_orm": root / "Assets/Textures/Purr2D2_Cloth_Light_ORM.png",
    }
    images = {key: read_rgba_png(path) for key, path in paths.items()}
    for key in ("dark_orm", "light_orm"):
        rough = []
        metal = []
        ao = []
        for row in images[key][2]:
            for x in range(0, len(row), 4):
                ao.append(row[x])
                rough.append(row[x + 1])
                metal.append(row[x + 2])
        require(max(metal) == 0, f"{key}: cloth metalness channel must be zero")
        require(min(ao) >= 230 and max(ao) == 255, f"{key}: cloth AO channel must stay near white")
        if key == "dark_orm":
            require(min(rough) >= 240 and max(rough) <= 245,
                    "Dark cloth ORM roughness must stay near 0.95")
        else:
            require(min(rough) >= 238 and max(rough) <= 254 and max(rough) - min(rough) >= 8,
                    "Light cloth ORM must be high, matte, and spatially varied")

    normal_rows = images["light_normal"][2]
    max_tilt_degrees = 0.0
    for row in normal_rows:
        for x in range(0, len(row), 4):
            nx, ny, nz = (row[x + channel] / 127.5 - 1.0 for channel in range(3))
            length = math.sqrt(nx * nx + ny * ny + nz * nz)
            require(math.isfinite(length) and 0.99 <= length <= 1.01 and nz > 0.99,
                    "Light normal map must contain finite near-flat tangent normals")
            tilt = math.degrees(math.acos(max(-1.0, min(1.0, nz / length))))
            max_tilt_degrees = max(max_tilt_degrees, tilt)
    require(max_tilt_degrees <= 2.5,
            f"Light normal map fiber relief exceeds the 2.5-degree limit ({max_tilt_degrees:.2f}°)")

    require(paths["light_diffuse"].stat().st_size > 2048
            and paths["dark_diffuse"].stat().st_size > 2048,
            "diffuse maps do not contain texture detail")
    require(images["light_normal"][2] != images["dark_normal"][2]
            and images["light_orm"][2] != images["dark_orm"][2],
            "Light maps must have their own restrained surface finish")

    metadata_path = root / "art/cape-cloth-metadata.json"
    if metadata_path.is_file():
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        texture_metadata = metadata.get("textures", {})
        require(texture_metadata.get("size") == "256x256",
                "cape texture metadata must keep cloth maps at 256x256")
        require(texture_metadata.get("lightMaps") == {
            "diffuse": "Assets/Textures/Purr2D2_Cloth_Light_Diffuse.png",
            "normal": "Assets/Textures/Purr2D2_Cloth_Light_Normal.png",
            "aoRoughMetal": "Assets/Textures/Purr2D2_Cloth_Light_ORM.png",
            "finish": "muted dusty brown fabric; worn edges and hem; irregular short fibers; matte",
        }, "cape texture metadata must describe the Light-side cloth finish/maps")
        require(texture_metadata.get("darkMaps") == {
            "diffuse": "Assets/Textures/Purr2D2_Cloth_Dark_Diffuse.png",
            "normal": "Assets/Textures/Purr2D2_Cloth_Normal.png",
            "aoRoughMetal": "Assets/Textures/Purr2D2_Cloth_ORM.png",
            "finish": "existing dark-side cloth maps, unchanged",
        }, "cape texture metadata must preserve the Dark-side map contract")
        roughness_metadata = texture_metadata.get("roughness", {})
        light_range = roughness_metadata.get("lightRange")
        require(roughness_metadata.get("dark") == 0.95 and light_range == [0.95, 0.98],
                "cape texture metadata must report the high matte roughness ranges")
        swatch = texture_metadata.get("brownSwatch")
        require(swatch is None or (root / swatch).is_file(),
                "cape texture metadata swatch path must exist when emitted")


def check_xml(root):
    path = root / "Assets/Capes.xml"
    tree = ET.parse(path)
    ids = {}
    for element in tree.getroot():
        asset_id = element.attrib.get("Id")
        if asset_id:
            require(asset_id not in ids, f"Assets/Capes.xml: duplicate id {asset_id}")
            ids[asset_id] = element
        for child in element.iter():
            value = child.attrib.get("Path")
            if value is None:
                continue
            normalized = value.replace("\\", "/")
            posix = PurePosixPath(normalized)
            windows = PureWindowsPath(value)
            require(not posix.is_absolute() and not windows.is_absolute() and not windows.drive and "://" not in normalized,
                    f"Assets/Capes.xml: path must be relative: {value}")
            target = (root / Path(*posix.parts)).resolve()
            try:
                target.relative_to(root.resolve())
            except ValueError as exc:
                raise CheckError(f"Assets/Capes.xml: path escapes root: {value}") from exc
            require(target.is_file(), f"Assets/Capes.xml: referenced file missing: {value}")
    expected = {"Purr2D2_Cape_Glb", "Purr2D2_Cloth_Light", "Purr2D2_Cloth_Dark"}
    require(set(ids) == expected,
            "Assets/Capes.xml: expected only the cape GLB and the two cloth materials")
    require("Purr2D2_Hood_Glb" not in ids,
            "Assets/Capes.xml: obsolete hood asset declaration must be absent")
    for glb_id, material_name in (("Purr2D2_Cape_Glb", "Purr2D2_Cloth_Light"),):
        ref = ids[glb_id].find("Material")
        require(ref is not None and ref.get("Name") == material_name and ref.get("AssetId") == material_name,
                f"Assets/Capes.xml: {glb_id} must map its default glTF slot to {material_name}")
    expected_material_maps = {
        "Purr2D2_Cloth_Light": {
            "Diffuse": "Assets/Textures/Purr2D2_Cloth_Light_Diffuse.png",
            "Normal": "Assets/Textures/Purr2D2_Cloth_Light_Normal.png",
            "AoRoughMetal": "Assets/Textures/Purr2D2_Cloth_Light_ORM.png",
        },
        "Purr2D2_Cloth_Dark": {
            "Diffuse": "Assets/Textures/Purr2D2_Cloth_Dark_Diffuse.png",
            "Normal": "Assets/Textures/Purr2D2_Cloth_Normal.png",
            "AoRoughMetal": "Assets/Textures/Purr2D2_Cloth_ORM.png",
        },
    }
    for material_id, expected_maps in expected_material_maps.items():
        material = ids[material_id]
        actual_maps = {channel.tag: channel.get("Path") for channel in material}
        require(actual_maps == expected_maps,
                f"Assets/Capes.xml: {material_id} texture mapping does not match the cloth map contract")
    return ids


def check_no_hood(root):
    hood_path = root / "Assets/Models/Purr2D2_Hood.glb"
    require(not hood_path.exists(), f"obsolete hood model must be absent: {hood_path}")
    metadata_path = root / "art/cape-cloth-metadata.json"
    if metadata_path.is_file():
        source = metadata_path.read_text(encoding="utf-8")
        metadata = json.loads(source)
        require("hood" not in metadata and "hood" not in source.lower()
                and "Purr2D2_Hood" not in source,
                "cape cloth metadata must not describe or reference a hood")


def expected_asset(kind):
    if kind != "cape":
        raise CheckError(f"unknown asset kind {kind!r}; only 'cape' is supported")
    joints = list(range(118))
    binds = [identity_matrix()]
    for row in range(13):
        for col in range(9):
            x, y, z = cape_rest(col / 8.0, row / 12.0)
            binds.append(translation_matrix(-x, -y, -z))
    return {"kind": kind, "mesh": "CapeMesh", "root_index": 0, "joint_nodes": joints, "binds": binds}


def identity_matrix():
    return [1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0]


def translation_matrix(x, y, z):
    matrix = identity_matrix()
    matrix[12:15] = [x, y, z]
    return matrix


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1],
                        help="source mod directory or staged mod root")
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        xml_ids = check_xml(root)
        check_no_hood(root)
        check_textures(root)
        glb_path = root / "Assets/Models/Purr2D2_Cape.glb"
        counts = check_glb(glb_path, expected_asset("cape"), root)
        print("PASS: cape rig has 117 named cloth joints, row-major binds, and normalized weights")
        print("PASS: Cape GLB is the only cape model asset; hood model/ID/metadata are absent")
        print("PASS: normals, tangents, UVs, indices, GLB material slots, and inverse binds")
        print("PASS: six 256px cloth maps, near-flat Light normals, and varied matte non-metal ORM values")
        print("PASS: Assets/Capes.xml IDs, material mappings, and texture/model paths")
        vertices, triangles = counts
        print(f"  cape: {vertices} vertices, {triangles} triangles")
        print(f"  assets declared: {', '.join(sorted(xml_ids))}")
    except (OSError, ValueError, KeyError, TypeError, ET.ParseError, CheckError, struct.error) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
