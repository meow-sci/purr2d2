#!/usr/bin/env python3
"""Build deterministic Purr2D2 cape/hood GLBs, cloth maps, and a CPU preview.

The mesh and maps use only the Python standard library. Pillow is used only for
the review image. That image is a software render, not a game screenshot.
"""

from __future__ import annotations

import json
import math
import struct
import zlib
from pathlib import Path

try:
    from PIL import Image, ImageDraw, ImageFont
except ImportError:
    Image = ImageDraw = ImageFont = None

ROOT = Path(__file__).resolve().parents[1]
MODELS = ROOT / "Assets" / "Models"
TEXTURES = ROOT / "Assets" / "Textures"
ART = ROOT / "art"
TAU = math.tau
TEX_SIZE = 256
LIGHT_MATERIAL = "Purr2D2_Cloth_Light"


def dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def sub(a, b):
    return tuple(x - y for x, y in zip(a, b))


def add(a, b):
    return tuple(x + y for x, y in zip(a, b))


def mul(a, value):
    return tuple(x * value for x in a)


def cross(a, b):
    return (a[1] * b[2] - a[2] * b[1],
            a[2] * b[0] - a[0] * b[2],
            a[0] * b[1] - a[1] * b[0])


def unit(a):
    length = math.sqrt(dot(a, a))
    return tuple(value / length for value in a) if length > 1e-18 else (0.0, 0.0, 1.0)


def identity_matrix():
    # glTF matrix arrays are column-major; identity is the same in either order.
    return [1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0]


def translation_matrix(x, y, z):
    matrix = identity_matrix()
    matrix[12:15] = [x, y, z]
    return matrix


def png_chunk(kind, data):
    return (struct.pack(">I", len(data)) + kind + data
            + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF))


def write_png(path, pixel_fn, size=TEX_SIZE):
    """Write RGBA PNG data using zlib and the Python standard library."""
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            value = pixel_fn(x, y, size)
            if len(value) == 3:
                value = (*value, 255)
            raw.extend(max(0, min(255, int(round(channel)))) for channel in value)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
        + png_chunk(b"IEND", b"")
    )


def noise(x, y, salt):
    value = (x * 0x45D9F3B) ^ (y * 0x119DE1F3) ^ salt
    value = (value ^ (value >> 16)) * 0x45D9F3B
    value = (value ^ (value >> 16)) * 0x45D9F3B
    value ^= value >> 16
    return (value & 0xFFFF) / 32767.5 - 1.0


def seam(x, y, size):
    edge = min(x, size - 1 - x)
    edge_line = max(0.0, 1.0 - abs(edge - 7.0) / 2.4)
    center_line = max(0.0, 1.0 - abs(x - (size - 1) * 0.5) / 1.65)
    hem = max(0.0, min(1.0, (y - (size - 14)) / 2.0))
    return max(edge_line, center_line, hem * 0.88)


def stitches(x, y, size):
    edge_distance = min(x, size - 1 - x)
    if 13 <= edge_distance <= 16 and y % 10 < 4:
        return 1.0
    if size - 21 <= y <= size - 18 and x % 10 < 4:
        return 1.0
    return 0.0


def weave_height(x, y, size):
    weave = (0.0024 * math.sin(TAU * x / 7.0)
             + 0.0018 * math.cos(TAU * y / 7.0)
             + 0.0010 * math.sin(TAU * (x + y) / 14.0))
    return weave + 0.007 * seam(x, y, size) + 0.003 * stitches(x, y, size)


def make_textures():
    for label, base, salt in (
        ("Light", (102, 66, 42), 0xCA9E),
        ("Dark", (31, 34, 39), 0xD4A1),
    ):
        def diffuse(x, y, size, base=base, salt=salt):
            pattern = (0.012 * math.sin(TAU * x / 7.0)
                       + 0.010 * math.cos(TAU * y / 7.0)
                       + 0.006 * math.sin(TAU * (x + y) / 14.0))
            variation = 0.012 * noise(x, y, salt)
            amount = 1.0 + pattern + variation - seam(x, y, size) * 0.20 + stitches(x, y, size) * 0.13
            return tuple(max(0, min(255, round(channel * amount))) for channel in base) + (255,)

        write_png(TEXTURES / f"Purr2D2_Cloth_{label}_Diffuse.png", diffuse)

    def normal(x, y, size):
        dx = weave_height(min(size - 1, x + 1), y, size) - weave_height(max(0, x - 1), y, size)
        dy = weave_height(x, min(size - 1, y + 1), size) - weave_height(x, max(0, y - 1), size)
        n = unit((-dx * 36.0, dy * 36.0, 1.0))
        return tuple(round((component * 0.5 + 0.5) * 255) for component in n) + (255,)

    def orm(x, y, size):
        ao = 255 - round(seam(x, y, size) * 22)
        roughness = 242 + round(noise(x, y, 0xB347) * 2)
        return ao, max(0, min(255, roughness)), 0, 255

    write_png(TEXTURES / "Purr2D2_Cloth_Normal.png", normal)
    write_png(TEXTURES / "Purr2D2_Cloth_ORM.png", orm)


def cape_position(u, t):
    # Cape design contract: meters, chest origin, +X left, +Y up, +Z forward.
    # Two subtle extra folds vanish on the 9x13 joint lattice.
    x = (u - 0.5) * (0.64 + 0.56 * t)
    y = 0.10 - 0.88 * t
    z = -0.30 - 0.06 * t + 0.025 * math.sin(u * 4.0 * math.pi) * math.sin(t * math.pi / 2.0)
    z += 0.012 * math.sin(u * 8.0 * math.pi) * math.sin(t * 12.0 * math.pi)
    z += 0.005 * math.sin(u * 16.0 * math.pi) * math.sin(t * 6.0 * math.pi)
    return (x, y, z)


def make_cape():
    # 17x25 keeps two mesh samples per authored bone interval: 425 vertices total.
    cols, rows = 17, 25
    positions, uvs, joints, weights = [], [], [], []
    for row in range(rows):
        t = row / (rows - 1)
        for col in range(cols):
            u = col / (cols - 1)
            positions.append(cape_position(u, t))
            uvs.append((u, t))
            grid_x, grid_y = u * 8.0, t * 12.0
            c0, r0 = min(8, int(grid_x)), min(12, int(grid_y))
            c1, r1 = min(8, c0 + 1), min(12, r0 + 1)
            fx, fy = grid_x - c0, grid_y - r0
            candidates = (
                (r0 * 9 + c0, (1.0 - fx) * (1.0 - fy)),
                (r0 * 9 + c1, fx * (1.0 - fy)),
                (r1 * 9 + c0, (1.0 - fx) * fy),
                (r1 * 9 + c1, fx * fy),
            )
            combined = {}
            for joint, weight in candidates:
                combined[joint] = combined.get(joint, 0.0) + weight
            ordered = sorted(combined.items(), key=lambda item: (-item[1], item[0]))
            while len(ordered) < 4:
                ordered.append((ordered[0][0], 0.0))
            values = [weight for _, weight in ordered[:4]]
            total = sum(values)
            joints.append(tuple(joint + 1 for joint, _ in ordered[:4]))  # skin joint 0 is CapeRoot
            weights.append(tuple(value / total for value in values))

    def at(row, col):
        return positions[row * cols + col]

    normals, tangents = [], []
    for row in range(rows):
        for col in range(cols):
            du = sub(at(row, min(cols - 1, col + 1)), at(row, max(0, col - 1)))
            dv = sub(at(min(rows - 1, row + 1), col), at(max(0, row - 1), col))
            normal = unit(cross(dv, du))
            tangent = unit(sub(du, mul(normal, dot(normal, du))))
            w = -1.0 if dot(cross(normal, tangent), dv) < 0.0 else 1.0
            normals.append(normal)
            tangents.append((*tangent, w))

    indices = []
    for row in range(rows - 1):
        for col in range(cols - 1):
            a = row * cols + col
            b, d = a + 1, a + cols
            c = d + 1
            indices.extend((a, c, b, a, d, c))

    nodes = [{"name": "CapeRoot", "children": list(range(1, 118))}]
    for row in range(13):
        t = row / 12.0
        for col in range(9):
            u = col / 8.0
            nodes.append({"name": f"Cape_{row * 9 + col:03d}", "translation": list(cape_position(u, t))})
    inverse_bind = [identity_matrix()]
    for node in nodes[1:]:
        x, y, z = node["translation"]
        inverse_bind.append(translation_matrix(-x, -y, -z))
    nodes.append({"name": "CapeMesh", "mesh": 0, "skin": 0})
    return {
        "name": "Purr2D2_Cape",
        "node_name": "CapeMesh",
        "positions": positions, "normals": normals, "uvs": uvs, "tangents": tangents,
        "joints": joints, "weights": weights, "indices": indices, "nodes": nodes,
        "skin": {"name": "Purr2D2_Cape_Skin", "skeleton": 0, "joints": list(range(118)),
                 "inverseBindMatrices": inverse_bind},
        "material_name": LIGHT_MATERIAL,
        "extras": {
            "units": "meters",
            "coordinateFrame": "+X left, +Y up, +Z kitten forward",
            "origin": "chest",
            "clothNodes": "Cape_000..Cape_116; row-major index=row*9+col",
            "jointRestPosition": "x=(u-.5)*(.64+.56*t); y=.10-.88*t; z=-.30-.06*t+.025*sin(u*4*pi)*sin(t*pi/2)",
            "meshSubdivisions": "2x per authored cloth-node interval",
        },
    }


def hood_position(u, v):
    # An open +Z hood surface, not a closed helmet or cone.
    theta_open = 0.66
    theta = theta_open + u * (TAU - 2.0 * theta_open)
    crown = math.sin(min(1.0, v / 0.44) * math.pi / 2.0)
    lower = min(1.0, max(0.0, (v - 0.82) / 0.18))
    lower = lower * lower * (3.0 - 2.0 * lower)
    # Rounded cap over the helmet, near-vertical side flaps, then a cowl flare.
    width = 0.025 + 0.31 * crown + 0.085 * lower
    depth = 0.030 + 0.21 * crown + 0.040 * lower
    y = 0.38 - 0.66 * v
    x = width * math.sin(theta)
    z = -0.07 + depth * math.cos(theta) + 0.012 * math.sin(theta * 3.0) * math.sin(v * math.pi)
    return (x, y, z)


def make_hood():
    cols, rows = 33, 13
    positions, uvs = [], []
    for row in range(rows):
        v = row / (rows - 1)
        for col in range(cols):
            u = col / (cols - 1)
            positions.append(hood_position(u, v))
            uvs.append((u, v))
    apex = len(positions)
    positions.append((0.0, 0.392, -0.07))
    uvs.append((0.5, 0.0))

    def at(row, col):
        return positions[row * cols + col]

    normals, tangents = [], []
    for row in range(rows):
        for col in range(cols):
            du = sub(at(row, min(cols - 1, col + 1)), at(row, max(0, col - 1)))
            dv = sub(at(min(rows - 1, row + 1), col), at(max(0, row - 1), col))
            normal = unit(cross(dv, du))
            tangent = unit(sub(du, mul(normal, dot(normal, du))))
            w = -1.0 if dot(cross(normal, tangent), dv) < 0.0 else 1.0
            normals.append(normal)
            tangents.append((*tangent, w))
    normals.append((0.0, 1.0, 0.0))
    tangents.append((1.0, 0.0, 0.0, -1.0))

    indices = []
    for row in range(rows - 1):
        for col in range(cols - 1):
            a = row * cols + col
            b, d = a + 1, a + cols
            c = d + 1
            indices.extend((a, d, c, a, c, b))
    for col in range(cols - 1):
        indices.extend((apex, col, col + 1))
    return {
        "name": "Purr2D2_Hood",
        "node_name": "HoodMesh",
        "positions": positions, "normals": normals, "uvs": uvs,
        "tangents": tangents, "joints": [(0, 0, 0, 0)] * len(positions),
        "weights": [(1.0, 0.0, 0.0, 0.0)] * len(positions), "indices": indices,
        "nodes": [{"name": "HoodRoot"}, {"name": "HoodMesh", "mesh": 0, "skin": 0}],
        "skin": {"name": "Purr2D2_Hood_Skin", "skeleton": 0, "joints": [0],
                 "inverseBindMatrices": [identity_matrix()]},
        "material_name": LIGHT_MATERIAL,
        "extras": {
            "units": "meters",
            "coordinateFrame": "+X left, +Y up, +Z kitten forward",
            "origin": "Head_M socket center",
            "design": "open +Z face aperture with draped sides and flared cowl; no front panel",
            "approximateBounds": "x +/-0.42m; y -0.28m..+0.392m; rear z about -0.35m",
            "faceOpening": "theta 0.66 rad on either side of +Z",
        },
    }


class GlbBuilder:
    def __init__(self):
        self.binary = bytearray()
        self.views = []
        self.accessors = []

    def accessor(self, values, fmt, component_type, kind, target=None, bounds=False):
        while len(self.binary) % 4:
            self.binary.append(0)
        offset = len(self.binary)
        flat = list(values) if kind == "SCALAR" else [component for row in values for component in row]
        self.binary.extend(struct.pack("<" + fmt * len(flat), *flat))
        view = {"buffer": 0, "byteOffset": offset, "byteLength": len(self.binary) - offset}
        if target is not None:
            view["target"] = target
        self.views.append(view)
        accessor = {"bufferView": len(self.views) - 1, "componentType": component_type,
                    "count": len(values), "type": kind}
        if bounds:
            accessor["min"] = [min(row[i] for row in values) for i in range(3)]
            accessor["max"] = [max(row[i] for row in values) for i in range(3)]
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def finish(self, document):
        document["buffers"] = [{"byteLength": len(self.binary)}]
        document["bufferViews"] = self.views
        document["accessors"] = self.accessors
        json_bytes = json.dumps(document, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        json_bytes += b" " * (-len(json_bytes) % 4)
        bin_bytes = bytes(self.binary) + b"\0" * (-len(self.binary) % 4)
        total = 12 + 8 + len(json_bytes) + 8 + len(bin_bytes)
        return (struct.pack("<4sII", b"glTF", 2, total)
                + struct.pack("<I4s", len(json_bytes), b"JSON") + json_bytes
                + struct.pack("<I4s", len(bin_bytes), b"BIN\0") + bin_bytes)


def make_glb(mesh):
    builder = GlbBuilder()
    position = builder.accessor(mesh["positions"], "f", 5126, "VEC3", 34962, True)
    normal = builder.accessor(mesh["normals"], "f", 5126, "VEC3", 34962)
    uv = builder.accessor(mesh["uvs"], "f", 5126, "VEC2", 34962)
    tangent = builder.accessor(mesh["tangents"], "f", 5126, "VEC4", 34962)
    joints = builder.accessor(mesh["joints"], "H", 5123, "VEC4", 34962)
    weights = builder.accessor(mesh["weights"], "f", 5126, "VEC4", 34962)
    indices = builder.accessor(mesh["indices"], "I", 5125, "SCALAR", 34963)
    inverse_bind = builder.accessor(mesh["skin"]["inverseBindMatrices"], "f", 5126, "MAT4")
    document = {
        "asset": {"version": "2.0", "generator": "Purr2D2 deterministic procedural cloth generator"},
        "scene": 0, "scenes": [{"nodes": [0, len(mesh["nodes"]) - 1]}], "nodes": mesh["nodes"],
        "meshes": [{"name": mesh["node_name"], "primitives": [{
            "attributes": {"POSITION": position, "NORMAL": normal, "TEXCOORD_0": uv,
                           "TANGENT": tangent, "JOINTS_0": joints, "WEIGHTS_0": weights},
            "indices": indices, "material": 0, "mode": 4,
        }]}],
        "materials": [{"name": mesh["material_name"], "doubleSided": True,
                       "pbrMetallicRoughness": {"baseColorFactor": [1, 1, 1, 1],
                                                "metallicFactor": 0.0, "roughnessFactor": 1.0}}],
        "skins": [{"name": mesh["skin"]["name"], "skeleton": mesh["skin"]["skeleton"],
                   "joints": mesh["skin"]["joints"], "inverseBindMatrices": inverse_bind}],
        "extras": mesh["extras"],
    }
    return builder.finish(document)


def write_assets(cape, hood):
    MODELS.mkdir(parents=True, exist_ok=True)
    (MODELS / "Purr2D2_Cape.glb").write_bytes(make_glb(cape))
    (MODELS / "Purr2D2_Hood.glb").write_bytes(make_glb(hood))
    xml = '''<?xml version="1.0" encoding="utf-8"?>
<!-- Original Purr2D2 cloth assets. Both GLBs are in meters, +Y up and +Z forward.
     CapeRoot is the identity skin root; its 117 children are row-major local translations.
     CapeMesh is a separate identity scene node. HoodRoot is the identity skin root;
     HoodMesh is a separate identity scene node at the Head_M socket origin. -->
<Assets>
  <GltfFile Id="Purr2D2_Cape_Glb">
    <Source Path="Assets/Models/Purr2D2_Cape.glb" />
    <Material Name="Purr2D2_Cloth_Light" AssetId="Purr2D2_Cloth_Light" />
  </GltfFile>
  <GltfFile Id="Purr2D2_Hood_Glb">
    <Source Path="Assets/Models/Purr2D2_Hood.glb" />
    <Material Name="Purr2D2_Cloth_Light" AssetId="Purr2D2_Cloth_Light" />
  </GltfFile>
  <PbrMaterial Id="Purr2D2_Cloth_Light">
    <Diffuse Path="Assets/Textures/Purr2D2_Cloth_Light_Diffuse.png" Category="Default" />
    <Normal Path="Assets/Textures/Purr2D2_Cloth_Normal.png" Category="Default" />
    <AoRoughMetal Path="Assets/Textures/Purr2D2_Cloth_ORM.png" Category="Default" />
  </PbrMaterial>
  <PbrMaterial Id="Purr2D2_Cloth_Dark">
    <Diffuse Path="Assets/Textures/Purr2D2_Cloth_Dark_Diffuse.png" Category="Default" />
    <Normal Path="Assets/Textures/Purr2D2_Cloth_Normal.png" Category="Default" />
    <AoRoughMetal Path="Assets/Textures/Purr2D2_Cloth_ORM.png" Category="Default" />
  </PbrMaterial>
</Assets>
'''
    (ROOT / "Assets" / "Capes.xml").write_text(xml, encoding="utf-8")


def guide_head_mesh():
    # Preview-only stylized head and visor, not written into either GLB.
    positions, normals, uvs, indices = [], [], [], []
    segments, rows = 24, 12
    for row in range(rows + 1):
        phi = -math.pi / 2 + math.pi * row / rows
        for col in range(segments + 1):
            theta = TAU * col / segments
            positions.append((0.14 * math.cos(phi) * math.sin(theta),
                              0.05 + 0.205 * math.sin(phi),
                              -0.02 + 0.12 * math.cos(phi) * math.cos(theta)))
            normals.append(unit((math.cos(phi) * math.sin(theta) / 0.14,
                                 math.sin(phi) / 0.205,
                                 math.cos(phi) * math.cos(theta) / 0.12)))
            uvs.append((col / segments, row / rows))
    for row in range(rows):
        for col in range(segments):
            a = row * (segments + 1) + col
            b, d = a + 1, a + segments + 1
            indices.extend((a, b, d + 1, a, d + 1, d))
    base = len(positions)
    visor_start = base
    positions.append((0.0, 0.095, 0.102))
    normals.append((0.0, 0.0, 1.0))
    uvs.append((0.5, 0.5))
    for col in range(25):
        angle = TAU * col / 24
        positions.append((0.105 * math.cos(angle), 0.095 + 0.065 * math.sin(angle), 0.108))
        normals.append((0.0, 0.0, 1.0))
        uvs.append((0.5 + 0.5 * math.cos(angle), 0.5 + 0.5 * math.sin(angle)))
    for col in range(24):
        indices.extend((base, base + col + 1, base + col + 2))
    return {"positions": positions, "normals": normals, "uvs": uvs, "indices": indices,
            "visor_start": visor_start}


def projection_for(mesh, rect, target, eye_offset):
    positions, normals, uvs, indices = mesh["positions"], mesh["normals"], mesh["uvs"], mesh["indices"]
    eye = add(target, eye_offset)
    forward = unit(sub(target, eye))
    right = unit(cross(forward, (0.0, 1.0, 0.0)))
    up = unit(cross(right, forward))
    xlo, ylo, xhi, yhi = rect
    projected = [(dot(sub(p, target), right), dot(sub(p, target), up)) for p in positions]
    span_x = max(p[0] for p in projected) - min(p[0] for p in projected)
    span_y = max(p[1] for p in projected) - min(p[1] for p in projected)
    scale = min((xhi - xlo - 42) / max(span_x, 1e-6),
                (yhi - ylo - 36) / max(span_y, 1e-6)) * 0.91
    cx, cy = (xlo + xhi) * 0.5, (ylo + yhi) * 0.5 + 8
    return eye, forward, right, up, scale, cx, cy


def render_mesh(canvas, mesh, texture, rect, target, eye_offset, flat_color=None, visor_guide=False,
                zbuffer=None, projection=None):
    positions, normals, uvs, indices = mesh["positions"], mesh["normals"], mesh["uvs"], mesh["indices"]
    if projection is None:
        projection = projection_for(mesh, rect, target, eye_offset)
    eye, forward, right, up, scale, cx, cy = projection
    xlo, ylo, xhi, yhi = rect
    projected = [(dot(sub(p, target), right), dot(sub(p, target), up)) for p in positions]
    screen = [(cx + x * scale, cy - y * scale) for x, y in projected]
    depth = [dot(sub(p, eye), forward) for p in positions]
    local_w, local_h = xhi - xlo, yhi - ylo
    if zbuffer is None:
        zbuffer = [float("inf")] * (local_w * local_h)
    pixels = canvas.load()
    tex_pixels = texture.load() if texture is not None else None
    tex_w, tex_h = texture.size if texture is not None else (1, 1)
    lights = (unit((0.35, 0.78, 0.55)), unit((-0.75, 0.18, 0.64)), unit((0.1, -0.8, -0.4)))

    for tri in range(0, len(indices), 3):
        ia, ib, ic = indices[tri:tri + 3]
        pa, pb, pc = screen[ia], screen[ib], screen[ic]
        area = (pb[0] - pa[0]) * (pc[1] - pa[1]) - (pb[1] - pa[1]) * (pc[0] - pa[0])
        if abs(area) < 1e-8:
            continue
        sx0 = max(xlo, int(math.floor(min(pa[0], pb[0], pc[0]))))
        sx1 = min(xhi - 1, int(math.ceil(max(pa[0], pb[0], pc[0]))))
        sy0 = max(ylo, int(math.floor(min(pa[1], pb[1], pc[1]))))
        sy1 = min(yhi - 1, int(math.ceil(max(pa[1], pb[1], pc[1]))))
        for sy in range(sy0, sy1 + 1):
            for sx in range(sx0, sx1 + 1):
                px, py = sx + 0.5, sy + 0.5
                wa = ((pb[0] - px) * (pc[1] - py) - (pb[1] - py) * (pc[0] - px)) / area
                wb = ((pc[0] - px) * (pa[1] - py) - (pc[1] - py) * (pa[0] - px)) / area
                wc = 1.0 - wa - wb
                if min(wa, wb, wc) < -1e-6:
                    continue
                z = wa * depth[ia] + wb * depth[ib] + wc * depth[ic]
                local = (sy - ylo) * local_w + sx - xlo
                if z >= zbuffer[local]:
                    continue
                zbuffer[local] = z
                n = unit(tuple(wa * normals[ia][k] + wb * normals[ib][k] + wc * normals[ic][k] for k in range(3)))
                if dot(n, forward) > 0.0:
                    n = mul(n, -1.0)
                uv = tuple(wa * uvs[ia][k] + wb * uvs[ib][k] + wc * uvs[ic][k] for k in range(2))
                if flat_color is not None:
                    base_color = flat_color
                else:
                    tx = max(0, min(tex_w - 1, int(uv[0] * (tex_w - 1))))
                    ty = max(0, min(tex_h - 1, int(uv[1] * (tex_h - 1))))
                    base_color = tex_pixels[tx, ty][:3] if tex_pixels is not None else (90, 90, 90)
                if visor_guide and all(i >= mesh["visor_start"] for i in (ia, ib, ic)):
                    base_color = (72, 101, 119)
                illumination = 0.25 + sum(max(0.0, dot(n, light)) * power
                                          for light, power in zip(lights, (0.58, 0.32, 0.12)))
                pixels[sx, sy] = tuple(max(0, min(255, int(channel * illumination))) for channel in base_color)


def make_preview(cape, hood):
    if Image is None:
        return False
    width, height = 1360, 780
    image = Image.new("RGB", (width, height), (12, 17, 22))
    draw = ImageDraw.Draw(image)
    try:
        title = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 28)
        label = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 19)
        small = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 15)
    except OSError:
        title = label = small = ImageFont.load_default()
    draw.text((48, 32), "PURR2D2  /  CLOTH ASSETS", fill=(211, 221, 224), font=title)
    draw.text((48, 73), "Original procedural meshes  •  software preview only, not in-game",
              fill=(122, 145, 156), font=small)
    left, right = (40, 118, 670, 710), (690, 118, 1320, 710)
    for box in (left, right):
        draw.rounded_rectangle(box, radius=14, outline=(43, 58, 67), width=2, fill=(16, 23, 29))
    draw.text((65, 132), "TRAVEL CAPE  /  117 JOINTS", fill=(213, 194, 160), font=label)
    draw.text((715, 132), "OVERSIZED HOOD  /  OPEN +Z FACE", fill=(195, 202, 207), font=label)
    light = Image.open(TEXTURES / "Purr2D2_Cloth_Light_Diffuse.png").convert("RGB")
    render_mesh(image, cape, light, (70, 185, 640, 665), (0.0, -0.34, -0.33), (0.72, 0.12, 1.0))
    hood_rect = (715, 185, 1295, 665)
    hood_target = (0.0, 0.04, -0.02)
    hood_eye = (0.08, 0.05, 1.0)
    hood_projection = projection_for(hood, hood_rect, hood_target, hood_eye)
    hood_depth = [float("inf")] * ((hood_rect[2] - hood_rect[0]) * (hood_rect[3] - hood_rect[1]))
    render_mesh(image, guide_head_mesh(), None, hood_rect,
                hood_target, hood_eye, flat_color=(49, 67, 78), visor_guide=True,
                zbuffer=hood_depth, projection=hood_projection)
    render_mesh(image, hood, light, hood_rect,
                hood_target, hood_eye, zbuffer=hood_depth, projection=hood_projection)
    draw.text((70, 681), "Brown weave • chest origin • soft cloth folds", fill=(144, 160, 166), font=small)
    draw.text((715, 681), "Brown preview; black variant included • visor guide is display-only",
              fill=(144, 160, 166), font=small)
    ART.mkdir(parents=True, exist_ok=True)
    image.save(ART / "cape-cloth-preview.png")
    return True


def main():
    for folder in (MODELS, TEXTURES, ART):
        folder.mkdir(parents=True, exist_ok=True)
    make_textures()
    cape, hood = make_cape(), make_hood()
    write_assets(cape, hood)
    preview = make_preview(cape, hood)
    metadata = {
        "authoring": "original deterministic procedural mesh and texture generator",
        "preview": "CPU software material render; not captured in-game or in Blender" if preview
                   else "Pillow unavailable; preview omitted",
        "cape": {"glb": "Assets/Models/Purr2D2_Cape.glb", "mesh": "CapeMesh",
                 "vertices": len(cape["positions"]), "triangles": len(cape["indices"]) // 3,
                 "skinJoints": 118, "clothJoints": 117, "units": "meters", "origin": "chest"},
        "hood": {"glb": "Assets/Models/Purr2D2_Hood.glb", "mesh": "HoodMesh",
                 "vertices": len(hood["positions"]), "triangles": len(hood["indices"]) // 3,
                 "skinJoints": 1, "units": "meters", "origin": "Head_M socket center",
                 "openFaceDirection": "+Z"},
        "textures": {"size": f"{TEX_SIZE}x{TEX_SIZE}",
                     "channels": "diffuse RGB, tangent normal, AO/roughness/metalness",
                     "roughness": 0.95, "metalness": 0.0},
    }
    (ART / "cape-cloth-metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    print("Wrote cape/hood GLBs, four 256px cloth maps, and Assets/Capes.xml" +
          (", plus art/cape-cloth-preview.png." if preview else "."))
    print(f"Cape: {len(cape['positions'])} vertices / {len(cape['indices']) // 3} triangles / 117 cloth joints")
    print(f"Hood: {len(hood['positions'])} vertices / {len(hood['indices']) // 3} triangles / open +Z face")


if __name__ == "__main__":
    main()
