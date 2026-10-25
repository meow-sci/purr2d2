#!/usr/bin/env python3
"""Run the cape cloth checks and CPU benchmark against the installed KSA managed DLLs."""

from pathlib import Path
import os
import platform
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET


root = Path(__file__).resolve().parents[1]
host = Path(os.environ.get("KSA_DLL_DIR", root.parent / "ksa-game-assemblies/current/dll")).resolve()
folder = root / "artifacts/checks/CapeClothChecks"
folder.mkdir(parents=True, exist_ok=True)
if not (host / "KSA.dll").is_file():
    raise SystemExit("Set KSA_DLL_DIR to the matching game DLL snapshot.")


def managed_reference(dll):
    data = bytearray(dll.read_bytes())
    if data[:2] != b"MZ":
        return None
    coff = struct.unpack_from("<I", data, 0x3C)[0] + 4
    optional = coff + 20
    magic = struct.unpack_from("<H", data, optional)[0]
    directories = optional + (112 if magic == 0x20B else 96)
    cli_rva = struct.unpack_from("<I", data, directories + 14 * 8)[0]
    if not cli_rva:
        return None
    if dll.name != "KSA.dll" or platform.machine().lower() in ("x86_64", "amd64"):
        return dll
    # A managed-only copy lets metadata checks run on ARM without starting native KSA.
    sections = optional + struct.unpack_from("<H", data, coff + 16)[0]
    for i in range(struct.unpack_from("<H", data, coff + 2)[0]):
        section = sections + i * 40
        size, rva, raw_size, raw = struct.unpack_from("<IIII", data, section + 8)
        if rva <= cli_rva < rva + max(size, raw_size):
            flags = struct.unpack_from("<I", data, raw + cli_rva - rva + 16)[0]
            if flags != 1:
                raise RuntimeError("Only unsigned IL-only KSA may be adapted for this metadata check")
            break
    else:
        raise RuntimeError("No CLI header found")
    struct.pack_into("<H", data, coff, 0x14C)
    struct.pack_into("<H", data, coff + 18, struct.unpack_from("<H", data, coff + 18)[0] | 0x100)
    adapted = folder / "host-il" / dll.name
    adapted.parent.mkdir(exist_ok=True)
    adapted.write_bytes(data)
    return adapted


project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
properties = ET.SubElement(project, "PropertyGroup")
for key, value in {"OutputType": "Exe", "TargetFramework": "net10.0", "ImplicitUsings": "enable",
                   "Nullable": "enable", "EnableDefaultCompileItems": "false"}.items():
    ET.SubElement(properties, key).text = value
items = ET.SubElement(project, "ItemGroup")
for source in [
    "src/Capes/CapeGeometry.cs",
    "src/Capes/CapePoseCache.cs",
    "src/Capes/CapeCloth.cs",
    "tests/capes/ClothChecks.cs",
    "tests/capes/AssetHostChecks.cs",
]:
    ET.SubElement(items, "Compile", Include=str(root / source))
for dll in host.glob("*.dll"):
    if dll.stem.startswith(("System.", "Microsoft.")) or dll.stem in ("mscorlib", "netstandard"):
        continue
    dll = managed_reference(dll)
    if dll is None:
        continue
    reference = ET.SubElement(items, "Reference", Include=dll.stem)
    ET.SubElement(reference, "HintPath").text = str(dll)
    ET.SubElement(reference, "Private").text = "true"
path = folder / "CapeClothChecks.csproj"
ET.ElementTree(project).write(path, encoding="unicode")
raise SystemExit(subprocess.run(
    ["dotnet", "run", "--project", str(path), "-c", "Release", *sys.argv[1:]], cwd=root).returncode)
