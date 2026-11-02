#!/usr/bin/env python3
"""Check actual stock rock and tree-trunk GLBs through the production clutter worker.

Game geometry is read locally into ignored artifacts/checks; it is never committed.
Usage: python3 tools/check_clutter_slices.py ../ksa-game-assemblies/current/Content/Core
"""
import argparse
import json
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET

from validate import DEFAULT_SOURCE, _run_managed_project


def load_meshes(path):
    data = path.read_bytes()
    length = struct.unpack_from('<I', data, 12)[0]
    gltf = json.loads(data[20:20 + length])
    binary = data[28 + length:]

    def accessor(index):
        a = gltf['accessors'][index]
        v = gltf['bufferViews'][a['bufferView']]
        form = '<' + {5121: 'B', 5123: 'H', 5125: 'I', 5126: 'f'}[a['componentType']] * {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3}[a['type']]
        return [struct.unpack_from(form, binary, v.get('byteOffset', 0) + a.get('byteOffset', 0)
                                  + i * v.get('byteStride', struct.calcsize(form))) for i in range(a['count'])]

    meshes = {}
    for mesh in gltf['meshes']:
        primitives = []
        for primitive in mesh['primitives']:
            attrs = primitive['attributes']
            if not all(k in attrs for k in ('POSITION', 'NORMAL', 'TEXCOORD_0')):
                continue
            positions, normals, uvs = [accessor(attrs[key]) for key in ('POSITION', 'NORMAL', 'TEXCOORD_0')]
            indices = [v[0] for v in accessor(primitive['indices'])]
            triangles = [[list(positions[j] + normals[j] + uvs[j]) for j in indices[i:i + 3]] for i in range(0, len(indices), 3)]
            primitives.append((primitive.get('material', 0), triangles))
        meshes[mesh['name']] = primitives
    return meshes


def fixtures(content):
    for filename, tree in [('GenericRockAssets.xml', False), ('EarthTreesAssets.xml', True)]:
        root = ET.parse(content / 'GroundClutter' / filename).getroot()
        atlas = root.find('MeshAtlas')
        meshes = load_meshes(content / atlas.get('Path'))
        materials = {m.get('Id'): m for m in root.findall('GroundClutterMaterial')}
        for obj in root.findall('ClutterObject')[:3 if not tree else 1]:
            lod = obj.find('LODs/LOD')
            mesh_ids = [m.get('Id') for m in lod.findall('Mesh')]
            material_names = [m.get('Id') for m in lod.findall('Material')]
            atlas_ids = sorted({material for name in mesh_ids for material, _ in meshes[name]})
            indirection = dict(zip(atlas_ids, material_names))
            groups, trunk = [], []
            native_foliage = False
            for name in mesh_ids:
                faces, trunk_faces = [], []
                for material, triangles in meshes[name]:
                    material_name = indirection[material]
                    definition = materials[material_name]
                    native = definition.find('Opacity') is not None or definition.find('Thickness') is not None or definition.find('DoubleSided') is not None
                    native_foliage |= native
                    for triangle in triangles:
                        face = [vertex + [material] for vertex in triangle]
                        faces.append(face)
                        if not native:
                            trunk_faces.append(face)
                if faces:
                    groups.append(faces)
                if trunk_faces:
                    trunk.append(trunk_faces)
            yield {'Name': obj.get('Id'), 'Tree': tree, 'NativeFoliage': native_foliage, 'Groups': groups}
            if tree:
                yield {'Name': obj.get('Id') + '_TrunkOnly', 'Tree': True, 'NativeFoliage': False, 'Groups': trunk}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('content', type=Path)
    args = parser.parse_args()
    output = DEFAULT_SOURCE / 'artifacts/checks/clutter-stock-slices.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(list(fixtures(args.content.resolve()))))
    required = [DEFAULT_SOURCE / p for p in (
        'src/Cutting/CutGeometry.cs', 'src/Cutting/PlaneSlicer.cs', 'src/Cutting/ClosedMeshPreparation.cs',
        'src/Cutting/ContactSliceGeometry.cs', 'src/Cutting/ClutterSliceGeometry.cs',
        'src/Rendering/FragmentGlbWriter.cs', 'src/Rendering/FragmentAssetWriter.cs', 'src/Rendering/FragmentTopology.cs',
        'src/Rendering/ClutterSurfaceTint.cs',
        'src/Slicing/SliceGeometryWork.cs', 'src/Slicing/QuietSubpartBreakup.cs', 'src/Slicing/SliceColliderGeometry.cs',
        'src/Cutting/SliceShellBuilder.cs', 'src/Cutting/AssistedSliceGeometry.cs', 'src/Slicing/SlicePrincipalFrame.cs',
        'src/Slicing/SliceMeshPrecision.cs', 'tests/ClutterStockSliceChecks.cs')]
    code, log = _run_managed_project(DEFAULT_SOURCE, 'ClutterStockSliceChecks', required,
        'internal static class Program { static void Main() => ClutterStockSliceChecks.Run(' + json.dumps(str(output)) + '); }')
    print(log)
    sys.exit(code)
