#!/usr/bin/env python3
"""Build a deterministic, validated Purr2D2 ZIP. This tool never publishes it."""

from __future__ import annotations

import argparse
import hashlib
import sys
import zipfile
from pathlib import Path

from validate import DEFAULT_SOURCE, validate_source, validate_stage


ZIP_EPOCH = (1980, 1, 1, 0, 0, 0)


def _zip_info(name: str, *, is_directory: bool = False) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=ZIP_EPOCH)
    info.create_system = 3
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = ((0o40755 if is_directory else 0o100644) << 16)
    if is_directory:
        info.external_attr |= 0x10
    return info


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _verify_archive(path: Path, stage: Path) -> None:
    expected = ["purr2d2/"] + sorted(
        f"purr2d2/{item.relative_to(stage).as_posix()}"
        for item in stage.rglob("*") if item.is_file()
    )
    with zipfile.ZipFile(path, "r") as archive:
        names = archive.namelist()
        if names != expected:
            raise ValueError("generated ZIP does not contain exactly one purr2d/ root and all staged files")
        if any(not name.startswith("purr2d2/") for name in names):
            raise ValueError("generated ZIP has a file outside the purr2d/ root")
        corrupt = archive.testzip()
        if corrupt is not None:
            raise ValueError(f"generated ZIP has a corrupt member: {corrupt}")


def create_package(source: Path, stage: Path, output_dir: Path) -> tuple[Path, str]:
    version, issues = validate_source(source)
    issues.extend(validate_stage(stage, version))
    if issues:
        raise ValueError("validation failed:\n" + "\n".join(f"- {issue}" for issue in issues))
    assert version is not None

    source = source.resolve()
    stage = stage.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    archive_path = output_dir / f"purr2d2-{version}.zip"
    temporary_path = archive_path.with_suffix(archive_path.suffix + ".tmp")
    members = sorted(path for path in stage.rglob("*") if path.is_file())
    try:
        with zipfile.ZipFile(temporary_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
            archive.writestr(_zip_info("purr2d2/", is_directory=True), b"")
            for path in members:
                relative = path.relative_to(stage).as_posix()
                archive_name = f"purr2d2/{relative}"
                with path.open("rb") as stream, archive.open(_zip_info(archive_name), "w") as destination:
                    while block := stream.read(1024 * 1024):
                        destination.write(block)
        _verify_archive(temporary_path, stage)
        temporary_path.replace(archive_path)
    finally:
        if temporary_path.exists():
            temporary_path.unlink()

    digest = _sha256(archive_path)
    checksum_path = archive_path.with_suffix(archive_path.suffix + ".sha256")
    checksum_path.write_text(f"{digest}  {archive_path.name}\n", encoding="ascii")
    return archive_path, digest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE, help="mod source root (default: repository root)")
    parser.add_argument("--stage", type=Path, help="staging directory (default: artifacts/stage/purr2d2)")
    parser.add_argument("--output", type=Path, help="output directory (default: artifacts/dist)")
    args = parser.parse_args(argv)
    source = args.source.resolve()
    stage = (args.stage or (source / "artifacts/stage/purr2d2")).resolve()
    output_dir = (args.output or (source / "artifacts/dist")).resolve()
    try:
        archive, digest = create_package(source, stage, output_dir)
    except (OSError, ValueError) as exc:
        print(f"Package failed: {exc}", file=sys.stderr)
        return 1
    print(f"Created {archive}")
    print(f"SHA256 {digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
