#!/usr/bin/env python3
"""Exercise production mesh growth planning/ownership without Vulkan or game loading."""
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
folder = root / "artifacts/checks/SliceMeshGrowthChecks"
folder.mkdir(parents=True, exist_ok=True)
project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
props = ET.SubElement(project, "PropertyGroup")
for key, value in {"OutputType": "Exe", "TargetFramework": "net10.0", "ImplicitUsings": "enable",
                   "Nullable": "enable", "EnableDefaultCompileItems": "false", "TreatWarningsAsErrors": "true"}.items():
    ET.SubElement(props, key).text = value
items = ET.SubElement(project, "ItemGroup")
for source in ["src/Core/SliceMeshArena.cs", "src/Core/SliceMeshGrowth.cs", "tests/SliceMeshGrowthChecks.cs"]:
    ET.SubElement(items, "Compile", Include=str(root / source))
path = folder / "SliceMeshGrowthChecks.csproj"
ET.ElementTree(project).write(path, encoding="unicode")
raise SystemExit(subprocess.run(["dotnet", "run", "--project", str(path), "-c", "Release"], cwd=root).returncode)
