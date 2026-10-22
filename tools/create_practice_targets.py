#!/usr/bin/env python3
"""Build two original closed practice meshes; no game geometry or third-party data.

Box: 1.2 x .36 x .36 m. Tube: 1.2 m axial length, .22/.15 m outer/inner radius,
32 sides, complete inner/outer walls and annular endcaps. Both axes run along X.
The named geometry-only glTF meshes are consumed by native MeshAtlas XML assets.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import struct

ROOT = Path(__file__).resolve().parent.parent


class Mesh:
    def __init__(self, name):
        self.name = name
        self.positions, self.normals, self.uvs, self.indices = [], [], [], []

    def quad(self, positions, normals, uvs=None):
        first = len(self.positions)
        self.positions.extend(positions)
        self.normals.extend(normals)
        self.uvs.extend(uvs or [(0, 0), (1, 0), (1, 1), (0, 1)])
        self.indices.extend([first, first + 1, first + 2, first, first + 2, first + 3])


def billet():
    mesh = Mesh("Purr2D2_PracticeBillet_Mesh")
    half = (.6, .18, .18)
    for axis in range(3):
        for sign in (-1, 1):
            # Cyclic basis has u cross v = +axis. Swap it for the negative face.
            u, v = (axis + 1) % 3, (axis + 2) % 3
            if sign < 0:
                u, v = v, u
            points = []
            for su, sv in [(-1, -1), (1, -1), (1, 1), (-1, 1)]:
                p = [0., 0., 0.]
                p[axis], p[u], p[v] = sign * half[axis], su * half[u], sv * half[v]
                points.append(tuple(p))
            normal = tuple(sign if i == axis else 0 for i in range(3))
            mesh.quad(points, [normal] * 4)
    return mesh


def tube():
    mesh = Mesh("Purr2D2_PracticeTube_Mesh")
    outer, inner, half, count = .22, .15, .6, 32
    def point(x, radius, angle):
        return (x, radius * math.cos(angle), radius * math.sin(angle))
    def radial(angle, sign):
        return (0, sign * math.cos(angle), sign * math.sin(angle))
    for index in range(count):
        a, b = math.tau * index / count, math.tau * (index + 1) / count
        mesh.quad([point(-half, outer, a), point(-half, outer, b), point(half, outer, b), point(half, outer, a)],
                  [radial(a, 1), radial(b, 1), radial(b, 1), radial(a, 1)])
        mesh.quad([point(-half, inner, a), point(half, inner, a), point(half, inner, b), point(-half, inner, b)],
                  [radial(a, -1), radial(a, -1), radial(b, -1), radial(b, -1)])
        for x, sign in [(-half, -1), (half, 1)]:
            points = ([point(x, outer, a), point(x, inner, a), point(x, inner, b), point(x, outer, b)]
                      if sign < 0 else
                      [point(x, outer, a), point(x, outer, b), point(x, inner, b), point(x, inner, a)])
            mesh.quad(points, [(sign, 0, 0)] * 4,
                      [(y / (outer * 2) + .5, z / (outer * 2) + .5) for _, y, z in points])
    return mesh


def write_glb(meshes):
    binary, views, accessors, gltf_meshes = bytearray(), [], [], []
    def add(values, width, kind, component, target):
        while len(binary) % 4:
            binary.append(0)
        offset = len(binary)
        flat = [v for row in values for v in row] if width > 1 else values
        code = "f" if component == 5126 else "I"
        binary.extend(struct.pack("<" + code * len(flat), *flat))
        view = len(views)
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(binary) - offset, "target": target})
        accessor = {"bufferView": view, "componentType": component, "count": len(values), "type": kind}
        if kind == "VEC3" and target == 34962:
            accessor["min"] = [min(v[i] for v in values) for i in range(3)]
            accessor["max"] = [max(v[i] for v in values) for i in range(3)]
        accessors.append(accessor)
        return len(accessors) - 1
    for mesh in meshes:
        positions = add(mesh.positions, 3, "VEC3", 5126, 34962)
        normals = add(mesh.normals, 3, "VEC3", 5126, 34962)
        uvs = add(mesh.uvs, 2, "VEC2", 5126, 34962)
        indices = add(mesh.indices, 1, "SCALAR", 5125, 34963)
        gltf_meshes.append({"name": mesh.name, "primitives": [{"attributes": {
            "POSITION": positions, "NORMAL": normals, "TEXCOORD_0": uvs}, "indices": indices, "mode": 4}]})
    document = {"asset": {"version": "2.0", "generator": "Purr2D2 original practice-target generator"},
                "buffers": [{"byteLength": len(binary)}], "bufferViews": views, "accessors": accessors,
                "meshes": gltf_meshes, "nodes": [{"name": mesh.name, "mesh": i} for i, mesh in enumerate(meshes)],
                "scenes": [{"nodes": list(range(len(meshes)))}], "scene": 0}
    encoded = json.dumps(document, separators=(",", ":")).encode()
    encoded += b" " * (-len(encoded) % 4)
    binary += b"\0" * (-len(binary) % 4)
    output = ROOT / "Assets" / "Models" / "Purr2D2_Practice.glb"
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(encoded) + 8 + len(binary))
                       + struct.pack("<II", len(encoded), 0x4E4F534A) + encoded
                       + struct.pack("<II", len(binary), 0x004E4942) + binary)
    for mesh in meshes:
        print(f"{mesh.name}: {len(mesh.positions)} vertices, {len(mesh.indices) // 3} triangles")
    print(output)


if __name__ == "__main__":
    write_glb([billet(), tube()])
