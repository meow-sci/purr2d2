#!/usr/bin/env python3
"""Build deterministic Purr2D2 cape GLB, cloth maps, and a CPU preview.

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

# Source-only dimensions for the original proxy in art previews. These match
# the local stock helmet/visor bounds after converting native centimeters to m;
# the stock meshes and textures are never copied into the mod.
HELMET_PROXY_BOUNDS = {
    "x": (-0.4031762314, 0.4031753540),
    "y": (-0.0805445957, 0.6374948502),
    "z": (-0.4117322159, 0.3477876663),
}
VISOR_PROXY_BOUNDS = {
    "x": (-0.3377220917, 0.3377267075),
    "y": (0.0011220413, 0.4882292938),
    "z": (0.1220224380, 0.3745520782),
}
HEAD_FROM_CHEST_METERS = (0.0, 0.10899, 0.00958)
TORSO_PROXY_BOUNDS_CHEST_METERS = {
    "x": (-0.3479055464, 0.3479055762),
    "y": (-0.4309300069, 0.1022684758),
    "z": (-0.1916999287, 0.2001998358),
}
# (chest-local y, measured half-width, measured rear z, measured front z).
# These binned bounds come from the stock KittyBody POSITION point cloud after
# subtracting Chest_M. The preview reconstructs an original ring proxy from
# these metrics; no stock character mesh or texture is included.
TORSO_PROXY_PROFILE = (
    (-0.430930, 0.2112, -0.0643, 0.1298),
    (-0.392500, 0.2000, -0.0584, 0.1208),
    (-0.367500, 0.1916, -0.0584, 0.1085),
    (-0.342500, 0.1740, -0.0582, 0.0826),
    (-0.317500, 0.1744, -0.0560, 0.0828),
    (-0.292500, 0.1670, -0.0681, 0.1041),
    (-0.267500, 0.1756, -0.0910, 0.1307),
    (-0.242500, 0.3433, -0.1040, 0.1476),
    (-0.217500, 0.3479, -0.1178, 0.1567),
    (-0.192500, 0.3475, -0.1236, 0.1613),
    (-0.167500, 0.3433, -0.1255, 0.1557),
    (-0.142500, 0.3352, -0.1242, 0.1624),
    (-0.117500, 0.3284, -0.1178, 0.1609),
    (-0.092500, 0.3025, -0.1131, 0.1727),
    (-0.067500, 0.2772, -0.1109, 0.1679),
    (-0.042500, 0.2800, -0.1105, 0.1575),
    (-0.017500, 0.2569, -0.1100, 0.1335),
    (0.007500, 0.2165, -0.1102, 0.1385),
    (0.032500, 0.1904, -0.1094, 0.1898),
    (0.057500, 0.2050, -0.1917, 0.1989),
    (0.082500, 0.2050, -0.1904, 0.2002),
    (0.102268, 0.1817, -0.1813, -0.0307),
)


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


def smooth_noise(x, y, size, cells_x, cells_y, salt):
    """Sample deterministic, tileable value noise at one octave."""
    grid_x = x * cells_x / size
    grid_y = y * cells_y / size
    ix, iy = math.floor(grid_x), math.floor(grid_y)
    fx, fy = grid_x - ix, grid_y - iy
    fx = fx * fx * (3.0 - 2.0 * fx)
    fy = fy * fy * (3.0 - 2.0 * fy)

    def sample(px, py):
        return noise(px % cells_x, py % cells_y, salt)

    top = sample(ix, iy) * (1.0 - fx) + sample(ix + 1, iy) * fx
    bottom = sample(ix, iy + 1) * (1.0 - fx) + sample(ix + 1, iy + 1) * fx
    return top * (1.0 - fy) + bottom * fy


def micro_fibers(x, y, size):
    """Return sparse short, softly sampled cloth fibers without a grid pattern."""
    cells = 72
    cell_size = size / cells
    px, py = x + 0.5, y + 0.5
    cell_x, cell_y = math.floor(px / cell_size), math.floor(py / cell_size)
    field = 0.0

    for cy in range(cell_y - 1, cell_y + 2):
        for cx in range(cell_x - 1, cell_x + 2):
            wrapped_x, wrapped_y = cx % cells, cy % cells
            if noise(wrapped_x, wrapped_y, 0xA099) < -0.28:
                continue
            start_x = (cx + 0.12 + 0.76 * (noise(wrapped_x, wrapped_y, 0xA0A7) + 1.0) * 0.5) * cell_size
            start_y = (cy + 0.12 + 0.76 * (noise(wrapped_x, wrapped_y, 0xA0B5) + 1.0) * 0.5) * cell_size
            angle = noise(wrapped_x, wrapped_y, 0xA0C3) * 0.42
            direction_x, direction_y = math.sin(angle), math.cos(angle)
            half_length = 0.60 + 0.95 * (noise(wrapped_x, wrapped_y, 0xA0D1) + 1.0) * 0.5
            offset_x, offset_y = px - start_x, py - start_y
            along = max(-half_length, min(half_length,
                                          offset_x * direction_x + offset_y * direction_y))
            distance = math.hypot(offset_x - along * direction_x,
                                  offset_y - along * direction_y)
            if distance < 1.25:
                coverage = (1.0 - distance / 1.25) ** 2
                polarity = 1.0 if noise(wrapped_x, wrapped_y, 0xA0DF) >= 0.0 else -1.0
                strength = 0.65 + 0.35 * abs(noise(wrapped_x, wrapped_y, 0xA0ED))
                field += polarity * strength * coverage
    return max(-1.0, min(1.0, field))


def light_fabric_fields(x, y, size):
    """Muted, irregular color/height fields for the worn Light-side cloth."""
    broad = (0.52 * smooth_noise(x, y, size, 3, 4, 0xA01D)
             + 0.31 * smooth_noise(x, y, size, 7, 9, 0xA02B)
             + 0.17 * smooth_noise(x, y, size, 15, 18, 0xA039))
    mottle = (0.62 * smooth_noise(x, y, size, 23, 29, 0xA047)
              + 0.38 * smooth_noise(x, y, size, 39, 47, 0xA055))
    fiber = smooth_noise(x, y, size, 57, 73, 0xA063)
    grain = noise(x, y, 0xA071)
    strands = micro_fibers(x, y, size)
    temperature = (0.65 * smooth_noise(x, y, size, 5, 6, 0xA07F)
                   + 0.35 * smooth_noise(x, y, size, 13, 11, 0xA08D))
    return broad, mottle, fiber, grain, temperature, strands


def soft_ramp(value, start, end):
    t = max(0.0, min(1.0, (value - start) / (end - start)))
    return t * t * (3.0 - 2.0 * t)


def light_height(x, y, size):
    broad, mottle, fiber, grain, _temperature, strands = light_fabric_fields(x, y, size)
    # Very shallow, non-periodic relief: only enough to keep fibers from reading
    # as a perfectly smooth surface when lit, with no raised seam or woven grid.
    return (0.00018 * broad + 0.00020 * mottle
            + 0.00060 * fiber + 0.000060 * grain + 0.00010 * strands)


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
    # Retain the Dark-side map formula exactly: its generated pixels are a
    # compatibility contract while the Light cloth receives a separate finish.
    def dark_diffuse(x, y, size):
        base, salt = (31, 34, 39), 0xD4A1
        pattern = (0.012 * math.sin(TAU * x / 7.0)
                   + 0.010 * math.cos(TAU * y / 7.0)
                   + 0.006 * math.sin(TAU * (x + y) / 14.0))
        variation = 0.012 * noise(x, y, salt)
        amount = 1.0 + pattern + variation - seam(x, y, size) * 0.20 + stitches(x, y, size) * 0.13
        return tuple(max(0, min(255, round(channel * amount))) for channel in base) + (255,)

    def light_diffuse(x, y, size):
        base = (92.0, 72.0, 55.0)
        broad, mottle, fiber, grain, temperature, strands = light_fabric_fields(x, y, size)
        luma = (0.12 * broad + 0.045 * mottle + 0.018 * fiber
                + 0.005 * grain + 0.075 * strands)
        # Subtle warm/cool drift keeps aged cloth from looking like a flat color
        # wash while preserving the original subdued brown value.
        warmth = 0.018 * temperature
        edge = min(x, size - 1 - x)
        side_wear = (1.0 - soft_ramp(edge, 1.0, 19.0))
        bottom_edge = soft_ramp(y, size - 38.0, size - 5.0)
        abrasion = side_wear * (0.014 + 0.018 * max(0.0, mottle))
        lower_grime = bottom_edge * (0.038 + 0.018 * max(0.0, -mottle + 0.25 * grain))
        amount = 1.0 + luma + abrasion - lower_grime
        channels = (base[0] * (amount + warmth * 0.62),
                    base[1] * (amount + warmth * 0.08),
                    base[2] * (amount - warmth * 0.72))
        return tuple(max(0, min(255, round(channel))) for channel in channels) + (255,)

    write_png(TEXTURES / "Purr2D2_Cloth_Light_Diffuse.png", light_diffuse)
    write_png(TEXTURES / "Purr2D2_Cloth_Dark_Diffuse.png", dark_diffuse)

    def normal(x, y, size):
        dx = weave_height(min(size - 1, x + 1), y, size) - weave_height(max(0, x - 1), y, size)
        dy = weave_height(x, min(size - 1, y + 1), size) - weave_height(x, max(0, y - 1), size)
        n = unit((-dx * 36.0, dy * 36.0, 1.0))
        return tuple(round((component * 0.5 + 0.5) * 255) for component in n) + (255,)

    def orm(x, y, size):
        ao = 255 - round(seam(x, y, size) * 22)
        roughness = 242 + round(noise(x, y, 0xB347) * 2)
        return ao, max(0, min(255, roughness)), 0, 255

    def light_normal(x, y, size):
        dx = light_height(min(size - 1, x + 1), y, size) - light_height(max(0, x - 1), y, size)
        dy = light_height(x, min(size - 1, y + 1), size) - light_height(x, max(0, y - 1), size)
        n = unit((-dx * 36.0, dy * 36.0, 1.0))
        return tuple(round((component * 0.5 + 0.5) * 255) for component in n) + (255,)

    def light_orm(x, y, size):
        broad, mottle, fiber, grain, _temperature, strands = light_fabric_fields(x, y, size)
        bottom_edge = soft_ramp(y, size - 38.0, size - 5.0)
        ao = 255 - round(2.0 * max(0.0, -broad) + 2.0 * bottom_edge
                         + 1.5 * max(0.0, -mottle))
        roughness = round(246.0 + 2.8 * broad + 2.4 * mottle
                          + 1.0 * fiber + 0.6 * grain + 0.7 * strands)
        return max(0, min(255, ao)), max(0, min(255, roughness)), 0, 255

    write_png(TEXTURES / "Purr2D2_Cloth_Normal.png", normal)
    write_png(TEXTURES / "Purr2D2_Cloth_ORM.png", orm)
    write_png(TEXTURES / "Purr2D2_Cloth_Light_Normal.png", light_normal)
    write_png(TEXTURES / "Purr2D2_Cloth_Light_ORM.png", light_orm)


def cape_position(u, t):
    # Cape design contract: meters, chest origin, +X left, +Y up, +Z forward.
    # The 9x13 pin lattice follows the shared measured geometry. Two finer
    # mesh-only folds vanish at all 117 bones and add a subtle cloth surface.
    r = 2.0 * u - 1.0
    x = (u - 0.5) * (0.48 + 0.38 * t)
    y = 0.10 - 0.50 * t
    z = (-0.23 + 0.04 * r * r * (1.0 - t) - 0.04 * t
         + 0.018 * math.sin(u * 4.0 * math.pi) * math.sin(t * math.pi / 2.0))
    z += 0.006 * math.sin(u * 8.0 * math.pi) * math.sin(t * 12.0 * math.pi)
    z += 0.003 * math.sin(u * 16.0 * math.pi) * math.sin(t * 6.0 * math.pi)
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
            "jointRestPosition": "r=2u-1; x=(u-.5)*(.48+.38*t); y=.10-.50*t; z=-.23+.04*r*r*(1-t)-.04*t+.018*sin(4*pi*u)*sin(pi*t/2)",
            "meshSubdivisions": "2x per authored cloth-node interval",
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


def write_assets(cape):
    MODELS.mkdir(parents=True, exist_ok=True)
    (MODELS / "Purr2D2_Cape.glb").write_bytes(make_glb(cape))
    xml = '''<?xml version="1.0" encoding="utf-8"?>
<!-- Original Purr2D2 cape asset in meters, +Y up and +Z forward.
     CapeRoot is the identity skin root; its 117 children are row-major local translations.
     CapeMesh is a separate identity scene node. -->
<Assets>
  <GltfFile Id="Purr2D2_Cape_Glb">
    <Source Path="Assets/Models/Purr2D2_Cape.glb" />
    <Material Name="Purr2D2_Cloth_Light" AssetId="Purr2D2_Cloth_Light" />
  </GltfFile>
  <PbrMaterial Id="Purr2D2_Cloth_Light">
    <Diffuse Path="Assets/Textures/Purr2D2_Cloth_Light_Diffuse.png" Category="Default" />
    <Normal Path="Assets/Textures/Purr2D2_Cloth_Light_Normal.png" Category="Default" />
    <AoRoughMetal Path="Assets/Textures/Purr2D2_Cloth_Light_ORM.png" Category="Default" />
  </PbrMaterial>
  <PbrMaterial Id="Purr2D2_Cloth_Dark">
    <Diffuse Path="Assets/Textures/Purr2D2_Cloth_Dark_Diffuse.png" Category="Default" />
    <Normal Path="Assets/Textures/Purr2D2_Cloth_Normal.png" Category="Default" />
    <AoRoughMetal Path="Assets/Textures/Purr2D2_Cloth_ORM.png" Category="Default" />
  </PbrMaterial>
</Assets>
'''
    (ROOT / "Assets" / "Capes.xml").write_text(xml, encoding="utf-8")


def helmet_proxy_mesh():
    # Original, preview-only ellipsoid matching measured stock helmet bounds.
    positions, normals, uvs, indices = [], [], [], []
    (xmin, xmax) = HELMET_PROXY_BOUNDS["x"]
    (ymin, ymax) = HELMET_PROXY_BOUNDS["y"]
    (zmin, zmax) = HELMET_PROXY_BOUNDS["z"]
    center = ((xmin + xmax) * 0.5, (ymin + ymax) * 0.5, (zmin + zmax) * 0.5)
    radii = ((xmax - xmin) * 0.5, (ymax - ymin) * 0.5, (zmax - zmin) * 0.5)
    segments, rows = 40, 20
    for row in range(rows + 1):
        phi = -math.pi / 2 + math.pi * row / rows
        for col in range(segments + 1):
            theta = TAU * col / segments
            point = (center[0] + radii[0] * math.cos(phi) * math.sin(theta),
                     center[1] + radii[1] * math.sin(phi),
                     center[2] + radii[2] * math.cos(phi) * math.cos(theta))
            positions.append(point)
            normals.append(unit(((point[0] - center[0]) / (radii[0] * radii[0]),
                                 (point[1] - center[1]) / (radii[1] * radii[1]),
                                 (point[2] - center[2]) / (radii[2] * radii[2]))))
            uvs.append((col / segments, row / rows))
    for row in range(rows):
        for col in range(segments):
            a = row * (segments + 1) + col
            b, d = a + 1, a + segments + 1
            indices.extend((a, b, d + 1, a, d + 1, d))
    return {"positions": positions, "normals": normals, "uvs": uvs, "indices": indices,
            "proxy": "original ellipsoid matching measured stock helmet bounds"}


def visor_proxy_mesh():
    # Original curved shield proxy fitted to the measured stock visor bounds.
    positions, normals, uvs, indices = [], [], [], []
    (xmin, xmax) = VISOR_PROXY_BOUNDS["x"]
    (ymin, ymax) = VISOR_PROXY_BOUNDS["y"]
    (zmin, zmax) = VISOR_PROXY_BOUNDS["z"]
    center_y = (ymin + ymax) * 0.5
    radius_x = (xmax - xmin) * 0.5
    radius_y = (ymax - ymin) * 0.5
    columns, rows = 32, 12
    for row in range(rows + 1):
        vertical = -1.0 + 2.0 * row / rows
        for col in range(columns + 1):
            angle = -math.pi / 2.0 + math.pi * col / columns
            cos_angle = math.cos(angle)
            x = radius_x * math.sin(angle)
            y = center_y + radius_y * cos_angle * vertical
            z = zmin + (zmax - zmin) * cos_angle
            positions.append((x, y, z))
            normals.append(unit((math.sin(angle) * 0.38, vertical * 0.18, 1.0)))
            uvs.append((col / columns, row / rows))
    for row in range(rows):
        for col in range(columns):
            a = row * (columns + 1) + col
            b, d = a + 1, a + columns + 1
            indices.extend((a, b, d + 1, a, d + 1, d))
    return {"positions": positions, "normals": normals, "uvs": uvs, "indices": indices,
            "proxy": "original curved shield matching measured stock visor bounds"}


def torso_proxy_mesh():
    # Original mannequin proxy reconstructed from measured KittyBody section
    # bounds, in Chest_M-local meters. The stock character mesh is not included.
    positions, normals, uvs, indices = [], [], [], []
    segments = 48
    rows = len(TORSO_PROXY_PROFILE)
    for row, (y, width, rear_z, front_z) in enumerate(TORSO_PROXY_PROFILE):
        center_z = (rear_z + front_z) * 0.5
        depth = (front_z - rear_z) * 0.5
        for col in range(segments + 1):
            theta = TAU * col / segments
            positions.append((width * math.sin(theta), y, center_z + depth * math.cos(theta)))
            uvs.append((col / segments, row / (rows - 1)))
    for row in range(rows):
        for col in range(segments + 1):
            before = max(0, col - 1)
            after = min(segments, col + 1)
            across = sub(positions[row * (segments + 1) + after],
                         positions[row * (segments + 1) + before])
            upper = min(rows - 1, row + 1)
            lower = max(0, row - 1)
            vertical = sub(positions[upper * (segments + 1) + col],
                           positions[lower * (segments + 1) + col])
            normals.append(unit(cross(across, vertical)))
    for row in range(rows - 1):
        for col in range(segments):
            a = row * (segments + 1) + col
            b, d = a + 1, a + segments + 1
            indices.extend((a, b, d + 1, a, d + 1, d))
    return {"positions": positions, "normals": normals, "uvs": uvs, "indices": indices,
            "proxy": "original ring mannequin from measured KittyBody bounds; not stock geometry"}


def translated_mesh(mesh, translation):
    tx, ty, tz = translation
    return {**mesh, "positions": [(x + tx, y + ty, z + tz) for x, y, z in mesh["positions"]]}


def combined_projection_mesh(meshes):
    return {"positions": [point for mesh in meshes for point in mesh["positions"]]}


def projection_for(mesh, rect, target, eye_offset):
    positions = mesh["positions"]
    eye = add(target, eye_offset)
    forward = unit(sub(target, eye))
    right = unit(cross(forward, (0.0, 1.0, 0.0)))
    up = unit(cross(right, forward))
    xlo, ylo, xhi, yhi = rect
    projected = [(dot(sub(p, target), right), dot(sub(p, target), up)) for p in positions]
    x_min, x_max = min(p[0] for p in projected), max(p[0] for p in projected)
    y_min, y_max = min(p[1] for p in projected), max(p[1] for p in projected)
    span_x = x_max - x_min
    span_y = y_max - y_min
    scale = min((xhi - xlo - 42) / max(span_x, 1e-6),
                (yhi - ylo - 36) / max(span_y, 1e-6)) * 0.91
    cx = (xlo + xhi) * 0.5 - (x_min + x_max) * 0.5 * scale
    cy = (ylo + yhi) * 0.5 + (y_min + y_max) * 0.5 * scale
    return eye, forward, right, up, scale, cx, cy


def render_mesh(canvas, mesh, texture, rect, target, eye_offset, flat_color=None,
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
    lights = (unit((0.35, 0.78, 0.55)), unit((-0.75, 0.18, 0.64)), unit((-0.12, -0.35, -0.93)))

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
                illumination = 0.36 + sum(max(0.0, dot(n, light)) * power
                                          for light, power in zip(lights, (0.50, 0.25, 0.32)))
                pixels[sx, sy] = tuple(max(0, min(255, int(channel * illumination))) for channel in base_color)


def make_preview(cape):
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
    draw.text((48, 32), "PURR2D2  /  CAPE ONLY", fill=(211, 221, 224), font=title)
    draw.text((48, 73), "Software preview • measured-size original proxies • not an in-game capture",
              fill=(122, 145, 156), font=small)
    left, right = (40, 118, 670, 710), (690, 118, 1320, 710)
    for box in (left, right):
        draw.rounded_rectangle(box, radius=14, outline=(43, 58, 67), width=2, fill=(16, 23, 29))
    draw.text((65, 132), "REAR THREE-QUARTER  /  CAPE", fill=(213, 194, 160), font=label)
    draw.text((715, 132), "SIDE PROFILE  /  CAPE", fill=(195, 202, 207), font=label)
    light = Image.open(TEXTURES / "Purr2D2_Cloth_Light_Diffuse.png").convert("RGB")
    torso = torso_proxy_mesh()
    head_from_chest = HEAD_FROM_CHEST_METERS
    helmet = translated_mesh(helmet_proxy_mesh(), head_from_chest)
    visor = translated_mesh(visor_proxy_mesh(), head_from_chest)
    parts = [cape, torso, helmet, visor]

    rear_rect = (70, 185, 640, 665)
    rear_target = (0.0, -0.03, -0.06)
    rear_eye = (0.92, 0.20, -1.45)
    rear_projection = projection_for(combined_projection_mesh(parts), rear_rect, rear_target, rear_eye)
    rear_depth = [float("inf")] * ((rear_rect[2] - rear_rect[0]) * (rear_rect[3] - rear_rect[1]))
    render_mesh(image, cape, light, rear_rect, rear_target, rear_eye,
                flat_color=(104, 69, 45), zbuffer=rear_depth, projection=rear_projection)
    render_mesh(image, torso, None, rear_rect, rear_target, rear_eye,
                flat_color=(43, 51, 56), zbuffer=rear_depth, projection=rear_projection)
    render_mesh(image, helmet, None, rear_rect, rear_target, rear_eye,
                flat_color=(49, 67, 78), zbuffer=rear_depth, projection=rear_projection)
    render_mesh(image, visor, None, rear_rect, rear_target, rear_eye,
                flat_color=(83, 132, 154), zbuffer=rear_depth, projection=rear_projection)

    side_rect = (715, 185, 1295, 665)
    side_target = (0.0, -0.03, -0.06)
    side_eye = (1.45, 0.14, -0.06)
    side_projection = projection_for(combined_projection_mesh(parts), side_rect, side_target, side_eye)
    side_depth = [float("inf")] * ((side_rect[2] - side_rect[0]) * (side_rect[3] - side_rect[1]))
    render_mesh(image, cape, light, side_rect, side_target, side_eye,
                flat_color=(104, 69, 45), zbuffer=side_depth, projection=side_projection)
    render_mesh(image, torso, None, side_rect, side_target, side_eye,
                flat_color=(43, 51, 56), zbuffer=side_depth, projection=side_projection)
    render_mesh(image, helmet, None, side_rect, side_target, side_eye,
                flat_color=(49, 67, 78), zbuffer=side_depth, projection=side_projection)
    render_mesh(image, visor, None, side_rect, side_target, side_eye,
                flat_color=(83, 132, 154), zbuffer=side_depth, projection=side_projection)
    draw.text((72, 681), "Measured torso and helmet/visor proxies at chest offset (0, +0.109, +0.0096)m",
              fill=(144, 160, 166), font=small)
    draw.text((715, 681), "Original measured-size cape mesh • software preview only",
              fill=(144, 160, 166), font=small)
    ART.mkdir(parents=True, exist_ok=True)
    image.save(ART / "cape-cloth-preview.png")
    return True


def make_brown_cloth_preview():
    """Write a deterministic 1:1 cloth swatch and an unfiltered fiber close-up."""
    if Image is None:
        return False
    texture = Image.open(TEXTURES / "Purr2D2_Cloth_Light_Diffuse.png").convert("RGB")
    image = Image.new("RGB", (640, 384), (34, 39, 42))
    draw = ImageDraw.Draw(image)
    try:
        title = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 16)
        small = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 12)
    except OSError:
        title = small = ImageFont.load_default()
    draw.text((20, 14), "Light cloth | dusty aged brown", fill=(221, 224, 218), font=title)
    draw.text((20, 38), "whole texture · 1:1", fill=(167, 177, 174), font=small)
    draw.text((336, 38), "fiber detail · 4× pixel zoom", fill=(167, 177, 174), font=small)
    image.paste(texture, (20, 60))
    detail = texture.crop((96, 96, 160, 160)).resize((256, 256), Image.Resampling.NEAREST)
    image.paste(detail, (336, 60))
    draw.text((20, 332), "Original deterministic procedural texture; no game lighting",
              fill=(167, 177, 174), font=small)
    ART.mkdir(parents=True, exist_ok=True)
    image.save(ART / "brown-cloth-preview.png")
    return True


def main():
    for folder in (MODELS, TEXTURES, ART):
        folder.mkdir(parents=True, exist_ok=True)
    make_textures()
    cape = make_cape()
    write_assets(cape)
    preview = make_preview(cape)
    brown_swatch = make_brown_cloth_preview()
    metadata = {
        "authoring": "original deterministic procedural mesh and texture generator",
        "preview": "software preview with full-body rear and side cape views; not in-game" if preview
                   else "Pillow unavailable; preview omitted",
        "cape": {"glb": "Assets/Models/Purr2D2_Cape.glb", "mesh": "CapeMesh",
                 "vertices": len(cape["positions"]), "triangles": len(cape["indices"]) // 3,
                 "skinJoints": 118, "clothJoints": 117, "units": "meters", "origin": "chest"},
        "previewProxy": {
            "helmetBoundsMeters": HELMET_PROXY_BOUNDS,
            "visorBoundsMeters": VISOR_PROXY_BOUNDS,
            "headFromChestMeters": [0.0, 0.10899, 0.00958],
            "torsoBoundsChestMeters": TORSO_PROXY_BOUNDS_CHEST_METERS,
            "torso": "original ring mannequin reconstructed from measured KittyBody bounds; not stock geometry",
        },
        "textures": {"size": f"{TEX_SIZE}x{TEX_SIZE}",
                     "channels": "diffuse RGB, tangent normal, AO/roughness/metalness",
                     "brownSwatch": "art/brown-cloth-preview.png" if brown_swatch else None,
                     "lightMaps": {"diffuse": "Assets/Textures/Purr2D2_Cloth_Light_Diffuse.png",
                                   "normal": "Assets/Textures/Purr2D2_Cloth_Light_Normal.png",
                                   "aoRoughMetal": "Assets/Textures/Purr2D2_Cloth_Light_ORM.png",
                                   "finish": "muted dusty brown fabric; worn edges and hem; irregular short fibers; matte"},
                     "darkMaps": {"diffuse": "Assets/Textures/Purr2D2_Cloth_Dark_Diffuse.png",
                                  "normal": "Assets/Textures/Purr2D2_Cloth_Normal.png",
                                  "aoRoughMetal": "Assets/Textures/Purr2D2_Cloth_ORM.png",
                                  "finish": "existing dark-side cloth maps, unchanged"},
                     "roughness": {"lightRange": [0.95, 0.98], "dark": 0.95},
                     "metalness": 0.0},
    }
    (ART / "cape-cloth-metadata.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    suffix = []
    if preview:
        suffix.append("art/cape-cloth-preview.png")
    if brown_swatch:
        suffix.append("art/brown-cloth-preview.png")
    print("Wrote cape GLB, six 256px cloth maps, and Assets/Capes.xml" +
          (f", plus {', '.join(suffix)}." if suffix else "."))
    print(f"Cape: {len(cape['positions'])} vertices / {len(cape['indices']) // 3} triangles / 117 cloth joints")


if __name__ == "__main__":
    main()
