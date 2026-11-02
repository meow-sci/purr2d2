#!/usr/bin/env python3
"""Validate durable clutter tint math and compile/link guarded native PartModel shaders.

Usage: python3 tools/check_clutter_tint.py ../ksa-game-assemblies/current/Content/Core
Native shader source and compiled files stay in ignored artifacts/checks.
"""
import argparse
import json
from pathlib import Path
import subprocess
import sys

from check_saber_shaders import _compiler
from validate import DEFAULT_SOURCE, _run_managed_project


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('content', type=Path)
    args = parser.parse_args()
    native = args.content.resolve() / 'Shaders'
    output = DEFAULT_SOURCE / 'artifacts/checks/ClutterTintShaders'
    code, log = _run_managed_project(DEFAULT_SOURCE, 'ClutterTintChecks',
        [DEFAULT_SOURCE / 'src/Rendering/ClutterSurfaceTint.cs', DEFAULT_SOURCE / 'tests/ClutterTintChecks.cs'],
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
        ('dynamic-unmarked', 'MeshIndirect.frag', ['ENABLE_TEMPERATURE', 'ENABLE_THIN_FILM']),
        ('raytraced', 'MeshIndirectRaytraced.frag', ['ENABLE_EMISSIVE', 'ENABLE_THIN_FILM']),
    ]
    for label, fragment, macros in variants:
        result = subprocess.run([str(compiler), '-V', '--target-env', 'vulkan1.2', '-l',
                                 '-I' + str(native / 'Mesh'), *['-D' + m + '=1' for m in macros],
                                 '-o', str(output / (label + '.spv')), str(output / 'MeshIndirect.vert'), str(output / fragment)],
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
        if result.returncode:
            print(result.stdout)
            sys.exit(result.returncode)
        print('PASS: actual native PartModel tint shaders compile/link: ' + label)
