#!/usr/bin/env python3
"""Exercise generated XML decoding against the actual host's managed game types."""
from pathlib import Path
import os
import subprocess
import platform
import struct
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
host = Path(os.environ.get('KSA_DLL_DIR', root.parent / 'ksa-game-assemblies/current/dll')).resolve()
if not (host / 'KSA.dll').is_file():
    raise SystemExit('Set KSA_DLL_DIR to the matching game DLL snapshot.')
folder = root / 'artifacts/checks/FragmentTemplateChecks'
folder.mkdir(parents=True, exist_ok=True)

def managed_reference(dll):
    data = bytearray(dll.read_bytes())
    if data[:2] != b'MZ':
        return None
    coff = struct.unpack_from('<I', data, 0x3c)[0] + 4
    optional = coff + 20
    magic = struct.unpack_from('<H', data, optional)[0]
    directories = optional + (112 if magic == 0x20b else 96)
    cli_rva = struct.unpack_from('<I', data, directories + 14 * 8)[0]
    if not cli_rva:
        return None  # Native dependencies are not part of this managed-only test.
    if dll.name != 'KSA.dll' or platform.machine().lower() in ('x86_64', 'amd64'):
        return dll
    # KSA is unsigned IL-only but tagged AMD64. Adapt only the COFF machine fields
    # in an ignored test copy so its unchanged managed XML types can run on arm64.
    # Never modify the game snapshot or use this copy in the mod distribution.
    sections = optional + struct.unpack_from('<H', data, coff + 16)[0]
    for i in range(struct.unpack_from('<H', data, coff + 2)[0]):
        section = sections + i * 40
        size, rva, raw_size, raw = struct.unpack_from('<IIII', data, section + 8)
        if rva <= cli_rva < rva + max(size, raw_size):
            flags = struct.unpack_from('<I', data, raw + cli_rva - rva + 16)[0]
            if flags != 1:
                raise RuntimeError('Only unsigned IL-only KSA may be adapted for this data-only test')
            break
    else:
        raise RuntimeError('No CLI header found')
    struct.pack_into('<H', data, coff, 0x14c)
    struct.pack_into('<H', data, coff + 18, struct.unpack_from('<H', data, coff + 18)[0] | 0x100)
    adapted = folder / 'host-il' / dll.name
    adapted.parent.mkdir(exist_ok=True)
    adapted.write_bytes(data)
    return adapted

project = ET.Element('Project', Sdk='Microsoft.NET.Sdk')
props = ET.SubElement(project, 'PropertyGroup')
for key, value in {'OutputType': 'Exe', 'TargetFramework': 'net10.0', 'ImplicitUsings': 'enable',
                   'Nullable': 'enable', 'EnableDefaultCompileItems': 'false'}.items():
    ET.SubElement(props, key).text = value
items = ET.SubElement(project, 'ItemGroup')
ET.SubElement(items, 'PackageReference', Include='Samboy063.Tomlet', Version='6.2.0')
ET.SubElement(items, 'PackageReference', Include='Lib.Harmony', Version='2.4.2')
# Native module callers mention KSA's logging constructor in their unchanged IL.
ET.SubElement(items, 'PackageReference', Include='Microsoft.Extensions.Logging.Abstractions', Version='10.0.0')
for source in ['src/Rendering/FragmentTemplateLoader.cs', 'src/Rendering/FragmentAssetWriter.cs',
               'src/Rendering/FragmentGlbWriter.cs', 'src/Rendering/FragmentTopology.cs',
               'src/Cutting/CutGeometry.cs', 'src/Cutting/PlaneSlicer.cs', 'src/Cutting/SliceShellBuilder.cs',
               'src/Cutting/AssistedSliceGeometry.cs', 'src/Slicing/SliceGeometryWork.cs', 'src/Slicing/QuietSubpartBreakup.cs',
               'src/Slicing/QuietSliceQuiescence.cs', 'tests/QuietSliceHooksChecks.cs',
               'src/Slicing/SliceColliderGeometry.cs', 'src/Slicing/SlicePrincipalFrame.cs',
               'src/Slicing/SliceMeshPrecision.cs', 'src/Core/SaberLimits.cs',
               'src/Slicing/SliceDiagnosticCounters.cs', 'src/Slicing/SliceFrameDiagnostics.cs',
               'src/Slicing/SliceWorkerDiagnostics.cs', 'src/Runtime/SliceWorkerHooks.cs',
               'src/Slicing/SliceCommitTimings.cs', 'src/Runtime/GameHooks.cs',
               'tests/SliceDiagnosticsChecks.cs', 'tests/FrameHooksChecks.cs', 'tests/FrameHooksHostStubs.cs',
               'tests/SliceWorkerDiagnosticsChecks.cs', 'tests/SliceWorkerHooksChecks.cs',
               'tests/FragmentAssetChecks.cs', 'tests/FragmentTemplateChecks.cs']:
    ET.SubElement(items, 'Compile', Include=str(root / source))
for dll in host.glob('*.dll'):
    if dll.stem.startswith(('System.', 'Microsoft.')) or dll.stem in ('mscorlib', 'netstandard'):
        continue
    dll = managed_reference(dll)
    if dll is None:
        continue
    reference = ET.SubElement(items, 'Reference', Include=dll.stem)
    ET.SubElement(reference, 'HintPath').text = str(dll)
    ET.SubElement(reference, 'Private').text = 'true'
path = folder / 'FragmentTemplateChecks.csproj'
ET.ElementTree(project).write(path, encoding='unicode')
raise SystemExit(subprocess.run(['dotnet', 'run', '--project', str(path), '-c', 'Release'], cwd=root).returncode)
