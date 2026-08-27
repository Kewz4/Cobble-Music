#!/usr/bin/env python3
"""Build a self-contained Modrinth client pack from the canonical Prism instance.

Modrinth-hosted JARs remain external downloads. CurseForge-only and custom JARs
are carried under overrides. Player/runtime data is excluded, while reviewed
client defaults (including options.txt) are shipped only as import-time files.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import sys
import tomllib
import unicodedata
import zipfile
from typing import Any, Iterable


INDEX_ROOTS = (
    "config",
    "resourcepacks",
    "shaderpacks",
    "fancymenu_data",
    "ESM",
    "ModTranslations",
)
TOP_LEVEL_DEFAULTS = (
    "options.txt",
    "servers.dat",
    "emi.json",
    "patchouli_data.json",
    "TrashSlotSaveState.json",
)
TEMPLATE_OVERRIDES = {
    "config/ReactiveMusic.json5": "config/ReactiveMusic.json5",
    "config/iris.properties": "config/iris.properties",
}
ARCHIVE_EXTENSIONS = {
    ".7z", ".bz2", ".gif", ".gz", ".jar", ".jpeg", ".jpg", ".lz4",
    ".m4a", ".mp3", ".mp4", ".ogg", ".png", ".rar", ".webm", ".webp",
    ".wav", ".xz", ".zip",
}
BACKUP_NAME = re.compile(r"(?:\.bak(?:[-._].*)?|\.old(?:[-._].*)?|~)$", re.I)
VERSION = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
MODRINTH_PROJECT = re.compile(r"/data/([^/]+)/versions/", re.I)
FIXED_ZIP_TIME = (1980, 1, 1, 0, 0, 0)


def fail(message: str) -> None:
    raise RuntimeError(message)


def digest(path: Path, algorithm: str) -> str:
    hasher = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def canonical_relative(value: str) -> str:
    if "\\" in value or "\x00" in value:
        fail(f"Unsafe archive path: {value!r}")
    path = PurePosixPath(value)
    if path.is_absolute() or not path.parts or any(part in ("", ".", "..") for part in path.parts):
        fail(f"Unsafe archive path: {value!r}")
    normalized = unicodedata.normalize("NFC", path.as_posix())
    if normalized != value:
        fail(f"Archive path is not canonical NFC: {value!r}")
    return normalized


def collision_key(value: str) -> str:
    return unicodedata.normalize("NFC", value).casefold()


def is_axiom(filename: str) -> bool:
    lowered = filename.casefold()
    return lowered == "axiom.jar" or lowered.startswith("axiom-")


def should_exclude_override(relative: str) -> bool:
    lowered = relative.casefold()
    parts = PurePosixPath(lowered).parts
    name = parts[-1]
    if lowered == "config/mcbrowser/tabs.json":
        return True
    if any(part in (".git", ".svn", "__pycache__") for part in parts):
        return True
    if parts[0] == "config" and "cache" in parts:
        return True
    if BACKUP_NAME.search(name) or name in ("thumbs.db", ".ds_store"):
        return True
    return False


def read_fabric_environment(jar: Path) -> str | None:
    try:
        with zipfile.ZipFile(jar) as archive:
            raw = archive.read("fabric.mod.json")
        value = json.loads(raw.decode("utf-8-sig"))
        if isinstance(value, list):
            environments = {str(item.get("environment", "*")) for item in value if isinstance(item, dict)}
            return "client" if environments == {"client"} else "server" if environments == {"server"} else "*"
        if isinstance(value, dict):
            return str(value.get("environment", "*"))
    except (KeyError, OSError, UnicodeError, json.JSONDecodeError, zipfile.BadZipFile):
        return None
    return None


def old_environment_map(instance: Path) -> tuple[dict[str, dict[str, str]], dict[str, dict[str, str]]]:
    index_path = instance / "mrpack" / "modrinth.index.json"
    if not index_path.is_file():
        return {}, {}
    old = json.loads(index_path.read_text(encoding="utf-8"))
    by_project: dict[str, dict[str, str]] = {}
    by_path: dict[str, dict[str, str]] = {}
    for entry in old.get("files", []):
        if not isinstance(entry, dict):
            continue
        environment = entry.get("env")
        if not isinstance(environment, dict):
            continue
        normalized = {
            "client": str(environment.get("client", "required")),
            "server": str(environment.get("server", "required")),
        }
        path = str(entry.get("path", ""))
        by_path[path] = normalized
        downloads = entry.get("downloads", [])
        if isinstance(downloads, list) and downloads:
            match = MODRINTH_PROJECT.search(str(downloads[0]))
            if match:
                by_project[match.group(1)] = normalized
    return by_project, by_path


def environment_for(
    jar: Path,
    project_id: str | None,
    old_by_project: dict[str, dict[str, str]],
    old_by_path: dict[str, dict[str, str]],
) -> dict[str, str]:
    if is_axiom(jar.name):
        return {"client": "optional", "server": "unsupported"}
    old = old_by_project.get(project_id or "") or old_by_path.get(f"mods/{jar.name}")
    if old:
        return dict(old)
    fabric_environment = (read_fabric_environment(jar) or "*").casefold()
    server = "unsupported" if fabric_environment == "client" else "required"
    return {"client": "required", "server": server}


def load_components(instance: Path) -> tuple[str, str]:
    metadata = json.loads((instance / "mmc-pack.json").read_text(encoding="utf-8"))
    versions = {str(item.get("uid")): str(item.get("version")) for item in metadata.get("components", [])}
    minecraft = versions.get("net.minecraft")
    fabric = versions.get("net.fabricmc.fabric-loader")
    if not minecraft or not fabric:
        fail("Prism mmc-pack.json is missing Minecraft or Fabric Loader metadata")
    return minecraft, fabric


def load_packwiz_mods(instance: Path, minecraft: Path) -> tuple[list[dict[str, Any]], set[str], list[Path], list[str]]:
    index_root = minecraft / "mods" / ".index"
    mods_root = minecraft / "mods"
    old_by_project, old_by_path = old_environment_map(instance)
    downloadable: list[dict[str, Any]] = []
    indexed_names: set[str] = set()
    bundled: list[Path] = []
    disabled: list[str] = []

    for metadata_path in sorted(index_root.glob("*.pw.toml"), key=lambda path: path.name.casefold()):
        metadata = tomllib.loads(metadata_path.read_text(encoding="utf-8"))
        filename = str(metadata.get("filename", ""))
        if not filename or Path(filename).name != filename:
            fail(f"Unsafe Packwiz filename in {metadata_path.name}: {filename!r}")
        jar = mods_root / filename
        if not jar.is_file():
            disabled_path = mods_root / f"{filename}.disabled"
            if disabled_path.is_file():
                disabled.append(disabled_path.name)
                continue
            fail(f"Packwiz metadata points to a missing active JAR: {filename}")
        indexed_names.add(filename)
        download = metadata.get("download", {})
        mode = str(download.get("mode", ""))
        expected_hash = str(download.get("hash", "")).lower()
        hash_format = str(download.get("hash-format", "")).lower()
        if hash_format not in ("sha1", "sha512") or digest(jar, hash_format) != expected_hash:
            fail(f"Packwiz hash does not match active JAR: {filename}")

        if mode != "url":
            bundled.append(jar)
            continue
        url = str(download.get("url", ""))
        if not url.startswith("https://"):
            fail(f"Packwiz download URL is not HTTPS: {filename}")
        project_id = None
        update = metadata.get("update", {})
        if isinstance(update, dict) and isinstance(update.get("modrinth"), dict):
            project_id = str(update["modrinth"].get("mod-id") or "") or None
        downloadable.append(
            {
                "path": f"mods/{filename}",
                "hashes": {"sha1": digest(jar, "sha1"), "sha512": digest(jar, "sha512")},
                "env": environment_for(jar, project_id, old_by_project, old_by_path),
                "downloads": [url],
                "fileSize": jar.stat().st_size,
            }
        )

    active_jars = sorted(
        (path for path in mods_root.iterdir() if path.is_file() and path.suffix.casefold() == ".jar"),
        key=lambda path: path.name.casefold(),
    )
    skipped_axiom: list[str] = []
    for jar in active_jars:
        if jar.name in indexed_names:
            continue
        if is_axiom(jar.name):
            skipped_axiom.append(jar.name)
            continue
        bundled.append(jar)

    return downloadable, indexed_names, sorted(set(bundled), key=lambda path: path.name.casefold()), disabled + skipped_axiom


def iter_tree_files(root: Path) -> Iterable[Path]:
    if not root.exists():
        return
    for path in sorted(root.rglob("*"), key=lambda item: item.as_posix().casefold()):
        if path.is_symlink():
            fail(f"Symlink is forbidden in client-pack source: {path}")
        if path.is_file():
            yield path


def collect_overrides(
    minecraft: Path,
    template_root: Path,
    bundled_mods: list[Path],
) -> dict[str, Path]:
    overrides: dict[str, Path] = {}
    seen: dict[str, str] = {}

    def add(relative: str, source: Path) -> None:
        relative = canonical_relative(relative)
        if should_exclude_override(relative):
            return
        key = collision_key(relative)
        previous = seen.get(key)
        if previous is not None:
            fail(f"Case/Unicode-colliding override paths: {previous!r} and {relative!r}")
        if not source.is_file() or source.is_symlink():
            fail(f"Override source is missing or unsafe: {source}")
        seen[key] = relative
        overrides[relative] = source

    for root_name in INDEX_ROOTS:
        root = minecraft / root_name
        for source in iter_tree_files(root):
            relative = source.relative_to(minecraft).as_posix()
            template_relative = TEMPLATE_OVERRIDES.get(relative)
            if template_relative:
                template = template_root / PurePosixPath(template_relative)
                add(relative, template)
            else:
                add(relative, source)
    for filename in TOP_LEVEL_DEFAULTS:
        source = minecraft / filename
        if source.is_file():
            add(filename, source)
    for jar in bundled_mods:
        add(f"mods/{jar.name}", jar)
    return overrides


def zip_info(name: str, compress_type: int) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, FIXED_ZIP_TIME)
    info.compress_type = compress_type
    info.external_attr = 0o100644 << 16
    info.create_system = 3
    return info


def add_bytes(archive: zipfile.ZipFile, name: str, payload: bytes) -> None:
    archive.writestr(zip_info(name, zipfile.ZIP_DEFLATED), payload, compresslevel=9)


def add_file(archive: zipfile.ZipFile, name: str, source: Path) -> None:
    compression = zipfile.ZIP_STORED if source.suffix.casefold() in ARCHIVE_EXTENSIONS else zipfile.ZIP_DEFLATED
    info = zip_info(name, compression)
    with source.open("rb") as input_stream, archive.open(info, "w", force_zip64=True) as output_stream:
        shutil.copyfileobj(input_stream, output_stream, length=8 * 1024 * 1024)


def validate_inventory(index: dict[str, Any], overrides: dict[str, Path]) -> None:
    seen: dict[str, str] = {}
    for entry in index["files"]:
        path = canonical_relative(str(entry["path"]))
        key = collision_key(path)
        if key in seen:
            fail(f"Duplicate downloadable path: {path}")
        seen[key] = path
    for relative in overrides:
        archive_path = canonical_relative(f"overrides/{relative}")
        key = collision_key(archive_path)
        if key in seen:
            fail(f"Download/override collision: {archive_path}")
        seen[key] = archive_path


def build(args: argparse.Namespace) -> dict[str, Any]:
    instance = args.instance.resolve(strict=True)
    minecraft = (instance / "minecraft").resolve(strict=True)
    template_root = args.template_root.resolve(strict=True)
    output = args.output.resolve()
    if output.exists():
        fail(f"Output already exists; refusing to overwrite: {output}")
    if not VERSION.fullmatch(args.version):
        fail(f"Version is not canonical major.minor.patch: {args.version}")

    version_config = minecraft / "config" / "cobble-music-pack-version.json"
    configured_version = str(json.loads(version_config.read_text(encoding="utf-8"))["version"])
    if configured_version != args.version:
        fail(f"DEV pack version is {configured_version}, expected {args.version}")

    minecraft_version, fabric_version = load_components(instance)
    downloads, indexed_names, bundled_mods, skipped_optional = load_packwiz_mods(instance, minecraft)
    overrides = collect_overrides(minecraft, template_root, bundled_mods)
    downloads.sort(key=lambda entry: str(entry["path"]).casefold())
    index = {
        "formatVersion": 1,
        "game": "minecraft",
        "versionId": args.version,
        "name": "Kewz's Cobblemon - Client",
        "summary": (
            "A COBBLEVERSE-based Cobblemon client pack with curated content, "
            "performance settings, English shader profiles, and reactive music."
        ),
        "files": downloads,
        "dependencies": {"minecraft": minecraft_version, "fabric-loader": fabric_version},
    }
    validate_inventory(index, overrides)
    index_bytes = (json.dumps(index, ensure_ascii=False, indent=2) + "\n").encode("utf-8")

    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_name(f".{output.name}.new-{os.getpid()}")
    try:
        with zipfile.ZipFile(temporary, "w", allowZip64=True) as archive:
            add_bytes(archive, "modrinth.index.json", index_bytes)
            for relative, source in sorted(overrides.items(), key=lambda item: item[0].casefold()):
                add_file(archive, f"overrides/{relative}", source)
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)

    expected_names = {"modrinth.index.json", *(f"overrides/{relative}" for relative in overrides)}
    with zipfile.ZipFile(output) as archive:
        actual_names = set(archive.namelist())
        if actual_names != expected_names or len(actual_names) != len(archive.infolist()):
            fail("Built MRPACK archive inventory does not exactly match its planned inventory")
        parsed_index = json.loads(archive.read("modrinth.index.json"))
        if parsed_index != index:
            fail("Built MRPACK index changed during serialization")

    return {
        "schemaVersion": 1,
        "version": args.version,
        "output": str(output),
        "size": output.stat().st_size,
        "sha256": digest(output, "sha256"),
        "minecraft": minecraft_version,
        "fabricLoader": fabric_version,
        "downloadableMods": len(downloads),
        "bundledMods": len(bundled_mods),
        "indexedMetadataEntries": len(indexed_names),
        "overrideFiles": len(overrides),
        "optionalOrDisabledModsNotBundled": sorted(skipped_optional, key=str.casefold),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--instance", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--template-root", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        report = build(args)
        rendered = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(rendered, encoding="utf-8", newline="\n")
        print(rendered, end="")
        return 0
    except Exception as exception:
        print(f"ERROR: {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
