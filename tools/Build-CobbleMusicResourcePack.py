#!/usr/bin/env python3
"""Build and verify a deterministic ZIP from a reviewed resource-pack tree."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
import tempfile
import unicodedata
import zipfile
from typing import Any, NoReturn


ARCHIVE_EXTENSIONS = {
    ".7z", ".bz2", ".gif", ".gz", ".jar", ".jpeg", ".jpg", ".lz4",
    ".m4a", ".mp3", ".mp4", ".ogg", ".png", ".rar", ".webm", ".webp",
    ".wav", ".xz", ".zip",
}
FIXED_ZIP_TIME = (1980, 1, 1, 0, 0, 0)


def fail(message: str) -> NoReturn:
    raise RuntimeError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_relative(path: Path, root: Path) -> str:
    value = path.relative_to(root).as_posix()
    pure = PurePosixPath(value)
    normalized = unicodedata.normalize("NFC", pure.as_posix())
    if (
        pure.is_absolute()
        or not pure.parts
        or any(part in ("", ".", "..") for part in pure.parts)
        or normalized != value
    ):
        fail(f"Unsafe or non-canonical resource-pack path: {value!r}")
    return value


def ensure_plain_tree(root: Path) -> None:
    is_junction = getattr(root, "is_junction", lambda: False)
    if root.is_symlink() or is_junction():
        fail(f"Symbolic links and junctions are not accepted: {root}")
    for directory, directory_names, file_names in os.walk(root):
        directory_path = Path(directory)
        for name in [*directory_names, *file_names]:
            candidate = directory_path / name
            candidate_is_junction = getattr(candidate, "is_junction", lambda: False)
            if candidate.is_symlink() or candidate_is_junction():
                fail(f"Symbolic links and junctions are not accepted: {candidate}")


def zip_info(name: str, compression: int) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, FIXED_ZIP_TIME)
    info.compress_type = compression
    info.external_attr = 0o100644 << 16
    info.create_system = 3
    return info


def add_file(archive: zipfile.ZipFile, name: str, source: Path) -> None:
    compression = (
        zipfile.ZIP_STORED
        if source.suffix.casefold() in ARCHIVE_EXTENSIONS
        else zipfile.ZIP_DEFLATED
    )
    with source.open("rb") as input_stream, archive.open(
        zip_info(name, compression), "w", force_zip64=True
    ) as output_stream:
        shutil.copyfileobj(input_stream, output_stream, length=8 * 1024 * 1024)


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".new")
    temporary.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    os.replace(temporary, path)


def build(source_value: str, output_value: str, report_value: str) -> dict[str, Any]:
    source = Path(source_value).expanduser().resolve()
    output = Path(output_value).expanduser().resolve()
    report = Path(report_value).expanduser().resolve()
    if not source.is_dir():
        fail(f"Source resource-pack tree does not exist: {source}")
    if output.exists():
        fail(f"Output already exists; refusing to overwrite: {output}")
    if output == source or source in output.parents:
        fail("Output ZIP must be outside the source resource-pack tree")
    if report == source or source in report.parents:
        fail("Build report must be outside the source resource-pack tree")
    ensure_plain_tree(source)

    files = [path for path in source.rglob("*") if path.is_file()]
    files.sort(key=lambda path: canonical_relative(path, source).casefold())
    entries: list[dict[str, Any]] = []
    collisions: dict[str, str] = {}
    for path in files:
        relative = canonical_relative(path, source)
        key = unicodedata.normalize("NFC", relative).casefold()
        if key in collisions:
            fail(f"Case-colliding resource-pack paths: {collisions[key]} and {relative}")
        collisions[key] = relative
        entries.append(
            {
                "path": relative,
                "size": path.stat().st_size,
                "sha256": sha256(path),
            }
        )

    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=output.stem + ".building.", suffix=output.suffix, dir=output.parent
    )
    os.close(descriptor)
    temporary = Path(temporary_name)
    try:
        with zipfile.ZipFile(
            temporary,
            "w",
            compression=zipfile.ZIP_DEFLATED,
            compresslevel=9,
            allowZip64=True,
        ) as archive:
            for entry, source_file in zip(entries, files, strict=True):
                add_file(archive, str(entry["path"]), source_file)
        with zipfile.ZipFile(temporary, "r", allowZip64=True) as archive:
            names = archive.namelist()
            if names != [str(entry["path"]) for entry in entries]:
                fail("Resource-pack ZIP inventory or ordering changed during build")
            bad_entry = archive.testzip()
            if bad_entry is not None:
                fail(f"Resource-pack ZIP failed CRC verification: {bad_entry}")
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)

    result = {
        "schemaVersion": 1,
        "source": str(source),
        "output": str(output),
        "archiveBytes": output.stat().st_size,
        "archiveSha256": sha256(output),
        "entryCount": len(entries),
        "sourceBytes": sum(int(entry["size"]) for entry in entries),
        "entries": entries,
    }
    write_json(report, result)
    return result


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--report", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    result = build(args.source, args.output, args.report)
    print(json.dumps({key: value for key, value in result.items() if key != "entries"}, indent=2))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        raise SystemExit(1)
