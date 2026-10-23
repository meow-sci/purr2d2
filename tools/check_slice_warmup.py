#!/usr/bin/env python3
"""Probe the production JIT-only warmup against a copied current game snapshot.

No game behavior is invoked. On ARM64, x64-marked pure-IL copies get their PE
machine changed so .NET can compile their IL locally; originals are never edited.
This checks compilation and method selection, not live game frame performance.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import struct
import subprocess
import sys
from xml.sax.saxutils import escape


def adapt_pure_il_copy(path: Path) -> bool:
    data = bytearray(path.read_bytes())
    if len(data) < 64 or data[:2] != b"MZ":
        return False
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", data, pe + 4)[0] != 0x8664:
        return False
    optional = pe + 24
    magic = struct.unpack_from("<H", data, optional)[0]
    directory = optional + (112 if magic == 0x20B else 96)
    cli_rva = struct.unpack_from("<I", data, directory + 14 * 8)[0]
    if not cli_rva:
        return False
    sections = optional + struct.unpack_from("<H", data, pe + 20)[0]
    for index in range(struct.unpack_from("<H", data, pe + 6)[0]):
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from("<IIII", data, sections + index * 40 + 8)
        if virtual_address <= cli_rva < virtual_address + max(virtual_size, raw_size):
            cli = raw_offset + cli_rva - virtual_address
            flags = struct.unpack_from("<I", data, cli + 16)[0]
            native_header = struct.unpack_from("<II", data, cli + 64)
            if not flags & 1 or flags & 2 or native_header != (0, 0):
                raise RuntimeError(f"Refusing to adapt a mixed/native/32-bit managed image: {path.name}")
            struct.pack_into("<H", data, pe + 4, 0xAA64)
            path.write_bytes(data)
            return True
    raise RuntimeError(f"Could not locate CLI header in {path.name}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    root = Path(__file__).resolve().parents[1]
    parser.add_argument("--host-dll-dir", type=Path, default=Path(os.environ.get("KSA_DLL_DIR", str(root.parent / "ksa-game-assemblies/current/dll"))))
    parser.add_argument("--dependency-dir", type=Path, action="append", default=[],
                        help="Additional managed host dependency directory; repeat for dependencies omitted from the reference snapshot.")
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    args = parser.parse_args()
    host = args.host_dll_dir.resolve()
    mod = root / "bin" / args.configuration / "net10.0/Purr2D2.dll"
    if not mod.is_file() or not (host / "KSA.dll").is_file():
        parser.error("Build Purr2D2 first and supply the current game DLL directory.")

    output = root / "artifacts/checks/SliceWarmupProbe"
    copies = output / "host-dlls"
    if copies.is_symlink() or copies.resolve() == host:
        raise RuntimeError("The probe copy directory must be separate from the source game DLL directory.")
    copies.mkdir(parents=True, exist_ok=True)
    originals = {path: hashlib.sha256(path.read_bytes()).digest() for path in host.glob("*.dll")}
    adapted = 0
    for path in originals:
        destination = copies / path.name
        if destination.is_symlink():
            raise RuntimeError(f"Refusing to overwrite a linked test DLL: {destination}")
        shutil.copy2(path, destination)
        if platform.machine().lower() in ("arm64", "aarch64"):
            adapted += adapt_pure_il_copy(destination)
    print(f"Copied {len(originals)} host DLLs; adapted {adapted} pure-IL PE machine fields in test copies only.", flush=True)

    # Resolve compile-only dependencies from this build's restored package paths.
    dependencies = [str(path.resolve()) for path in args.dependency_dir]
    if any(not path.is_dir() for path in args.dependency_dir):
        parser.error("Every --dependency-dir must name an existing managed dependency directory.")
    assets_path = root / "obj/project.assets.json"
    if assets_path.is_file():
        assets = json.loads(assets_path.read_text())
        for library in assets.get("libraries", {}).values():
            if library.get("type") != "package":
                continue
            for folder in assets.get("packageFolders", {}):
                package = Path(folder) / library["path"]
                dependencies.extend(str(path.parent) for path in package.glob("lib/net10.0/*.dll"))
    output.joinpath("SliceWarmupProbe.csproj").write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
        '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
        f'<Compile Include="{escape(str(root / "tools/slice_warmup_probe.cs"))}" />'
        '</ItemGroup></Project>\n'
    )
    try:
        warmed_stdout = ""
        for mode in ("--cold-baseline", "--warm", "--cancel-before", "--cancel-running"):
            command = ["dotnet", "run", "--project", str(output / "SliceWarmupProbe.csproj"), "-c", "Release"]
            if mode != "--cold-baseline":
                command.append("--no-build")
            command.append("--")
            if mode != "--warm":
                command.append(mode)
            process = subprocess.run([*command, str(mod), str(copies), *sorted(set(dependencies))],
                                     cwd=root, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
            print(process.stdout, end="")
            if process.returncode:
                return process.returncode
            if mode == "--warm":
                warmed_stdout = process.stdout
        summaries = re.findall(r"Slice JIT warmup: prepared (\d+) methods; skipped \d+ open/abstract/bodyless methods; failed 0; targeted graph (\d+) bodies/\d+ IL bytes, depth \d+, boundaries \d+; within budget", warmed_stdout)
        if len(summaries) != 1 or summaries[0][0] != summaries[0][1]:
            print("FAIL: production warmup did not publish a complete successful summary.", file=sys.stderr)
            return 1
        return 0
    finally:
        if any(hashlib.sha256(path.read_bytes()).digest() != digest for path, digest in originals.items()):
            raise RuntimeError("A source game DLL changed during the probe.")
        print("PASS: original host DLL hashes are unchanged.")


if __name__ == "__main__":
    sys.exit(main())
