#!/usr/bin/env python3
"""Exercise the production shell/plane slicer against a user's installed stock GLBs.

Usage: python3 tools/check_stock_slices.py /path/to/Content/Core
   or: python3 tools/check_stock_slices.py --fixture artifacts/checks/stock-slices.json
Game assets remain local and are never included in the mod or repository.
"""
import argparse
import json
import math
from pathlib import Path
import struct
import sys
import xml.etree.ElementTree as ET

from validate import DEFAULT_SOURCE, _run_managed_project


def fixtures(content):
    for family, target in [('FuelTankA', 'CoreFuelTankA_Prefab_LF3W3HA'),
                           ('CommandA', 'CoreCommandA_Prefab_MediumCapsuleVariantA'),
                           *[('ServiceModuleA', f'CoreServiceModuleA_Prefab_SetAHeight{height}') for height in 'ABCD']]:
        def load_meshes(atlas):
            data = (content / 'Meshes' / f'{atlas}_MeshAtlas.glb').read_bytes()
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
                triangles = []
                for primitive in mesh['primitives']:
                    attrs = primitive['attributes']
                    if 'NORMAL' not in attrs or 'TEXCOORD_0' not in attrs:
                        continue
                    p, n, uv = [accessor(attrs[key]) for key in ('POSITION', 'NORMAL', 'TEXCOORD_0')]
                    indices = [v[0] for v in accessor(primitive['indices'])]
                    triangles.extend([[list(p[j] + n[j] + uv[j]) for j in indices[i:i + 3]] for i in range(0, len(indices), 3)])
                meshes[mesh['name']] = triangles
            return meshes
        meshes = load_meshes("Core" + family)
        definitions = {}
        for xml in content.glob("*Assets.xml"):
            definitions.update({e.get("Id"): e for e in ET.parse(xml).getroot() if e.tag in ("Part", "SubPart") and e.get("Id")})
        groups = []
        materials = {}

        def vector(node, default):
            return [float(node.get(a, default)) if node is not None else float(default) for a in 'XYZ']

        def transform(point, instance, normal=False):
            tx = instance.find('Transform')
            if tx is None:
                return point
            pos, rot, scale = vector(tx.find('Position'), 0), vector(tx.find('Rotation'), 0), vector(tx.find('Scale'), 1)
            x, y, z = [p / s if normal else p * s for p, s in zip(point, scale)]
            # KSA QuaternionEx.CreateFromXyzRadians applies X, Y, then Z.
            for axis, angle in enumerate(rot):
                c, s = math.cos(angle), math.sin(angle)
                if axis == 0:
                    y, z = c * y - s * z, s * y + c * z
                elif axis == 1:
                    x, z = c * x + s * z, -s * x + c * z
                else:
                    x, y = c * x - s * y, s * x + c * y
            return [x, y, z] if normal else [x + pos[0], y + pos[1], z + pos[2]]

        def visit(node, transforms):
            definition = definitions[node.get('InstanceOf')] if node.get('InstanceOf') else node
            faces = []
            for model in definition:
                if model.tag in ('PartModel', 'PartModelDynamic', 'PartModelGlass'):
                    mesh_id = model.find('Mesh').get('Id')
                    material_key = model.find('Material').get('Id') + ':' + model.tag
                    material_id = materials.setdefault(material_key, len(materials))
                    if mesh_id not in meshes:
                        meshes.update(load_meshes(mesh_id.split('_')[0]))
                    for triangle in meshes[mesh_id]:
                        out = []
                        for v in triangle:
                            p, n = v[:3], v[3:6]
                            for instance in transforms:
                                p, n = transform(p, instance), transform(n, instance, True)
                            out.append(p + n + v[6:] + [material_id])
                        faces.append(out)
            if faces:
                groups.append(faces)
            for sub in definition.findall('SubPart'):
                visit(sub, [sub] + transforms)
        visit(definitions[target], [])
        yield {'Name': target, 'Groups': groups}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('content', nargs='?', type=Path, help='installed Content/Core directory, including Meshes')
    parser.add_argument('--fixture', type=Path, help='reuse an existing local stock fixture without reading game GLBs')
    args = parser.parse_args()
    if (args.content is None) == (args.fixture is None):
        parser.error('provide either Content/Core or --fixture')
    if args.fixture is not None:
        output = args.fixture.resolve()
        if not output.is_file():
            parser.error(f'fixture does not exist: {output}')
    else:
        output = DEFAULT_SOURCE / 'artifacts/checks/stock-slices.json'
        # Read every required asset before replacing a usable cache. Generated game
        # fixtures stay in ignored artifacts/ and are never bundled with the mod.
        try:
            data = json.dumps(list(fixtures(args.content.resolve())))
        except (OSError, KeyError, ET.ParseError) as error:
            parser.error(f'could not read installed stock assets (cached fixture preserved): {error}')
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(data)
    required = [DEFAULT_SOURCE / p for p in ('src/Cutting/CutGeometry.cs', 'src/Cutting/PlaneSlicer.cs',
                'src/Rendering/FragmentGlbWriter.cs', 'src/Rendering/FragmentAssetWriter.cs', 'src/Slicing/SliceGeometryWork.cs', 'src/Slicing/SliceColliderGeometry.cs', 'src/Cutting/SliceShellBuilder.cs', 'src/Cutting/AssistedSliceGeometry.cs', 'src/Slicing/SlicePrincipalFrame.cs', 'src/Slicing/SliceMeshPrecision.cs', 'tests/StockSliceChecks.cs')]
    code, log = _run_managed_project(DEFAULT_SOURCE, 'StockSliceChecks', required,
        'internal static class Program { static void Main() => StockSliceChecks.Run(' + json.dumps(str(output)) + '); }')
    print(log)
    sys.exit(code)
