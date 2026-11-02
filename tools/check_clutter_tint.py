#!/usr/bin/env python3
"""Validate durable clutter tint math and compile/link guarded native PartModel shaders.

Usage: python3 tools/check_clutter_tint.py ../ksa-game-assemblies/current/Content/Core
Native shader source and compiled files stay in ignored artifacts/checks.
"""
import argparse
import json
from pathlib import Path
import subprocess
import struct
import sys

from check_saber_shaders import _compiler
from validate import DEFAULT_SOURCE, _run_managed_project


def check_shadow_layout(path):
    """Check the compiler's real std430 offsets rather than a source-text assumption."""
    blob = path.read_bytes()
    words = struct.unpack('<' + 'I' * (len(blob) // 4), blob)
    names, offsets, arrays, strides = {}, {}, {}, {}
    cursor = 5
    while cursor < len(words):
        length, opcode = words[cursor] >> 16, words[cursor] & 0xffff
        operands = words[cursor + 1:cursor + length]
        if not length:
            raise RuntimeError('Invalid shader instruction length')
        if opcode == 5:  # OpName
            names[operands[0]] = struct.pack('<' + 'I' * (len(operands) - 1), *operands[1:]).split(b'\0')[0].decode()
        elif opcode == 29:  # OpTypeRuntimeArray
            arrays[operands[0]] = operands[1]
        elif opcode == 71 and operands[1] == 6:  # OpDecorate ArrayStride
            strides[operands[0]] = operands[2]
        elif opcode == 72 and operands[2] == 35:  # OpMemberDecorate Offset
            offsets[operands[0], operands[1]] = operands[3]
        cursor += length
    instance = next(key for key, value in names.items() if value == 'InstanceData')
    if [offsets.get((instance, member)) for member in range(4)] != [0, 64, 68, 72] or not any(
            element == instance and strides.get(array) == 80 for array, element in arrays.items()):
        raise RuntimeError('Compiled foliage shadow shader changed the native 80-byte instance ABI')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('content', type=Path)
    args = parser.parse_args()
    native = args.content.resolve() / 'Shaders'
    output = DEFAULT_SOURCE / 'artifacts/checks/ClutterTintShaders'
    code, log = _run_managed_project(DEFAULT_SOURCE, 'ClutterTintChecks',
        [DEFAULT_SOURCE / 'src/Rendering/ClutterSurfaceTint.cs', DEFAULT_SOURCE / 'src/Rendering/ClutterFoliageSurface.cs',
         DEFAULT_SOURCE / 'tests/ClutterTintChecks.cs'],
        'internal static class Program { static void Main() => ClutterTintChecks.Run('
        + json.dumps(str(native)) + ',' + json.dumps(str(output)) + '); }')
    print(log)
    if code:
        sys.exit(code)
    compiler = _compiler()
    if compiler is None:
        sys.exit('glslangValidator is required for native clutter tint shader checks')
    variants = [
        ('opaque', 'MeshIndirect.frag', ['ENABLE_EMISSIVE', 'ENABLE_THIN_FILM']),
        ('wet', 'MeshIndirect.frag', ['ENABLE_EMISSIVE', 'ENABLE_THIN_FILM', 'ENABLE_WETNESS']),
        ('frost', 'MeshIndirect.frag', ['ENABLE_EMISSIVE', 'ENABLE_THIN_FILM', 'ENABLE_WETNESS', 'ENABLE_FROST', 'ENABLE_TEMPERATURE']),
        ('dynamic-unmarked', 'MeshIndirect.frag', ['ENABLE_TEMPERATURE', 'ENABLE_THIN_FILM']),
        ('raytraced', 'MeshIndirectRaytraced.frag', ['ENABLE_EMISSIVE', 'ENABLE_THIN_FILM']),
        ('alpha-prepass', 'MeshNormalIndirect.frag', []),
    ]
    for label, fragment, macros in variants:
        result = subprocess.run([str(compiler), '-V', '--target-env', 'vulkan1.2', '-l',
                                 '-I' + str(native / 'Mesh'), *['-D' + m + '=1' for m in macros],
                                 '-o', str(output / (label + '.spv')), str(output / 'MeshIndirect.vert'), str(output / fragment)],
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
        if result.returncode:
            print(result.stdout)
            sys.exit(result.returncode)
        print('PASS: actual native PartModel tint/foliage shaders compile/link: ' + label)
    for shader in ('ShadowPassNoPush.vert', 'ShadowPassNoPushCSM.vert'):
        for dented in (False, True):
            label = shader.removesuffix('.vert') + ('-dented' if dented else '')
            result = subprocess.run([str(compiler), '-V', '--target-env', 'vulkan1.2', '-l',
                                     '-I' + str(native), *(['-DENABLE_DEFORMATION=1'] if dented else []),
                                     '-o', str(output / (label + '.spv')), str(output / shader)],
                                    text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
            if result.returncode:
                print(result.stdout)
                sys.exit(result.returncode)
            check_shadow_layout(output / (label + '.spv'))
            print('PASS: actual native foliage shadow guard compiles with offsets 0/64/68/72 and stride80: ' + label)
