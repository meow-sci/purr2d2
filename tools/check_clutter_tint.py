#!/usr/bin/env python3
"""Validate durable clutter tint math and compile/link guarded native PartModel shaders.

Usage: python3 tools/check_clutter_tint.py ../ksa-game-assemblies/current/Content/Core
Native shader source and compiled files stay in ignored artifacts/checks.
"""
import argparse
import itertools
import json
import math
from pathlib import Path
import re
import subprocess
import struct
import sys
from types import SimpleNamespace
import xml.etree.ElementTree as ET

from check_saber_shaders import _compiler
from validate import DEFAULT_SOURCE, _run_managed_project


class Vec3(tuple):
    """Small numeric GLSL vec3 value for evaluating the actual emitted assignments."""
    def __new__(cls, *values):
        return tuple.__new__(cls, values if len(values) == 3 else (values[0],) * 3)

    def apply(self, other, operation):
        other = other if isinstance(other, Vec3) else Vec3(other)
        return Vec3(*(operation(a, b) for a, b in zip(self, other)))

    def __add__(self, other): return self.apply(other, lambda a, b: a + b)
    def __sub__(self, other): return self.apply(other, lambda a, b: a - b)
    def __mul__(self, other): return self.apply(other, lambda a, b: a * b)
    def __truediv__(self, other): return self.apply(other, lambda a, b: a / b)
    def __pow__(self, other): return self.apply(other, lambda a, b: a ** b)


def shader_expression(expression, values):
    # Only local shader arithmetic is evaluated; these environments contain numeric
    # vectors/samples and the GLSL functions used by the native diffuse rule/payload.
    expression = re.sub(r'\b(0x[0-9a-fA-F]+|\d+)u\b', r'\1', expression)
    return eval(expression, {'__builtins__': {}, 'vec3': Vec3,
                            'uintBitsToFloat': lambda bits: struct.unpack('<f', struct.pack('<I', bits))[0],
                            'pow': lambda a, b: a ** b, 'mix': lambda a, b, t: a * (1 - t) + b * t}, values)


def shader_assignments(source, values):
    """Evaluate extracted GLSL assignments, rather than a separately mirrored formula."""
    for statement in re.sub(r'//[^\n]*', '', source).split(';'):
        statement = re.sub(r'^\s*(?:vec3|float)\s+', '', statement).strip()
        if not statement:
            continue
        match = re.fullmatch(r'([\w.]+)\s*(\*=|\+=|=)\s*(.+)', statement, re.S)
        if not match:
            raise RuntimeError('Native clutter arithmetic contract changed: ' + statement)
        target, operator, expression = match.groups()
        value = shader_expression(expression, values)
        if operator != '=':
            original = shader_expression(target, values)
            value = original * value if operator == '*=' else original + value
        if '.' in target:
            name, member = target.split('.')
            setattr(values[name], member, value)
        else:
            values[target] = value
    return values


def check_albedo_contract(content, native, output):
    ground = (native / 'Planet/GroundClutter/Solid.frag').read_text()
    # This is the native relative-color block, including its actual division by L.
    block = re.search(r'vec3 diffuseSrgb\s*=.*?diffuseSample\.rgb\s*\*=.*?;', ground, re.S).group()
    for endpoint in (r'getPBREnvironmentLightingPresampled\([^;]*surfaceColor\s*\*\s*averageLuminosity',
                     r'SampleForwardLights\([^;]*surfaceColor\s*\*\s*averageLuminosity'):
        if not re.search(endpoint, ground, re.S):
            raise RuntimeError('Native environment/punctual absolute-albedo contract changed')
    native_transmission = re.search(r'outColor\.rgb\s*\+=\s*global\.lighting\.sunColor\.rgb\s*\*\s*(.*?)\s*\*\s*leafAttenuationWavelengthMult', ground, re.S).group(1)
    astronomicals = ET.parse(content / 'Astronomicals.xml')
    earth = astronomicals.find(".//Scattering[@Ref='Earth_Scattering']/MeanDiffuseLuminosity")
    if earth is None:
        earth = astronomicals.find(".//Scattering[@Id='Earth_Scattering']/MeanDiffuseLuminosity")
    if earth is None:
        raise RuntimeError('Native Earth scattering mean luminosity was not found')
    earth_luminosity = float(earth.get('Value')) ** 2.2
    capture = (DEFAULT_SOURCE / 'src/Cutting/ClutterSliceSourceCapture.cs').read_text()
    default = re.search(r'MeanDiffuseLuminosity\s*\?\?\s*([.\d]+)f,\s*([.\d]+)', capture)
    default_luminosity = float(default.group(1)) ** float(default.group(2))
    cases = list(itertools.product((earth_luminosity, default_luminosity, .025, .1, .6, 1.0),
                                  (0, .125, .5, .875, 1), ((.5, .5, .5), (.01, .3, .8), (.9, .2, .7)),
                                  ((0, 1023, 1), (103, 205, 51), (917, 301, 72), (1023, 1023, 1023))))

    def native_color(diffuse, alpha, color, luminosity):
        sample = SimpleNamespace(rgb=Vec3(*diffuse), a=alpha)
        return shader_assignments(block, {'diffuseSample': sample, 'instanceColor': color,
            'uboPlanet': SimpleNamespace(meanDiffuseLuminosity=luminosity)})['diffuseSample'].rgb

    def near(actual, expected, relative=2e-12):
        if any(not math.isfinite(a) or abs(a - e) > max(1e-12, abs(e) * relative) for a, e in zip(actual, expected)):
            raise RuntimeError(f'Clutter absolute-albedo regression: actual={actual}, native={expected}')

    # Independent endpoints catch normalization and alpha interpretation mistakes.
    golden = ((0, (.0826759688,) * 3), (.5, (.1040357489, .1399176310, .0860948079)), (1, (.1, .2, .05)))
    for alpha, expected in golden:
        near(native_color((.5,) * 3, alpha, Vec3(.1, .2, .05), earth_luminosity) * earth_luminosity, expected, 2e-9)

    for filename in ('MeshIndirect.frag', 'MeshIndirectRaytraced.frag'):
        production = (output / filename).read_text()
        assignments = re.search(r'sampledColor = mix\(pow\(purrDiffuse.*?sampledColor\s*\*=.*?;', production, re.S).group()
        decode_luminosity = re.search(r'float purrLuminosity = (.*?);', production).group(1)
        transmission = re.search(r'lightColor\s*\+=\s*global\.lighting\.sunColor\.rgb\s*\*\s*(.*?)\n\s*\*\s*vec3\(1\.0, 0\.8, 0\.7\)', production, re.S).group(1)
        for exact_luminosity, alpha, diffuse, rgb10 in cases:
            bits = struct.unpack('<I', struct.pack('<f', exact_luminosity))[0] & 0xfffff000
            luminosity = struct.unpack('<f', struct.pack('<I', bits))[0]
            packed_color = rgb10[0] << 22 | rgb10[1] << 12 | rgb10[2] << 2
            # Evaluate the actual emitted GLSL unpacking with every native flag set.
            flags = 0x1fff | 0x2000 | (bits & 0xffffc000)
            emission = packed_color | ((bits >> 12) & 3)
            decoded = shader_expression(decode_luminosity, {'inStateFlags': flags, 'inEmissiveColor': emission})
            if decoded != luminosity:
                raise RuntimeError('Runtime tint payload lost durable luminosity bits or consumed native highlight flags')
            color = Vec3(*(((packed_color >> shift) & 1023) / 1023 for shift in (22, 12, 2)))
            relative = native_color(diffuse, alpha, color, luminosity)
            absolute = relative * luminosity
            values = {'purrDiffuse': SimpleNamespace(rgb=Vec3(*diffuse), a=alpha), 'purrColor': color,
                      'purrLuminosity': luminosity}
            actual = shader_assignments(assignments, values)['sampledColor']
            near(actual, absolute)
            # Storage quantization is the only expected deviation from native L.
            near(actual, native_color(diffuse, alpha, color, exact_luminosity) * exact_luminosity, .0005)
            native_leaf = shader_expression(native_transmission, {'sampledColor': SimpleNamespace(rgb=relative),
                                                                  'averageLuminosity': luminosity})
            near(shader_expression(transmission, values), native_leaf)
            if alpha == 1:
                near(actual, Vec3(*diffuse) * 2 * color)
        print(f'PASS: {filename}: {len(cases)} evaluated native/production albedo and transmission cases, alpha0/partial/1, RGB10, durable luminosity quantization')
    print(f'PASS: actual Earth L={earth_luminosity:.9f}, default L={default_luminosity:.9f}; old relative-albedo amplification={1 / earth_luminosity:.5f}x on Earth')


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
    check_albedo_contract(args.content.resolve(), native, output)
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
