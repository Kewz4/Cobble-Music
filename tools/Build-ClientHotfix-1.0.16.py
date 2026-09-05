"""Build scoped client fixes; never write to the live instance or a server.

Inputs are supplied explicitly. Shader code/settings remain byte-identical;
only two case-sensitive archive paths change. The compatibility pack contains
configuration only, not MegaStorm art, models, or animation assets.
"""
import argparse
import hashlib
import json
import posixpath
import re
from pathlib import Path, PurePosixPath
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED

SHADER = "Max Quality (Iteration RP - Path Traced).zip"
RP = "MegaStorm Gen1 Fixes (ResourcePack).zip"
POSE = "assets/cobblemon/bedrock/pokemon/posers/0715_noivern/noivern_mega.json"
BROKEN_QUIRK = "q.bedrock_quirk('noivern_mega', 'blink')"
SOURCE_SHADER_SHA = "f20d3e84b181f81816585104ed4ee1bc2b910fc54740558d53fab87cc057b1ca"
BASE_MANIFEST_SHA = "8e7ad9a2739e0d4e72d9383859bbfa83c3b6bfef2ec71ddb5aa05523a87d134f"
FIXED_TIME = (2026, 9, 5, 0, 0, 0)
INCLUDE = re.compile(r'^\s*#include\s+["<]([^">]+)', re.M)
SOURCE_EXTENSIONS = (".glsl", ".vsh", ".fsh", ".gsh", ".csh", ".tcs", ".tes", ".properties")


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def encode(value):
    return (json.dumps(value, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def safe_entry(name):
    p = PurePosixPath(name)
    if not name or p.is_absolute() or ".." in p.parts or "\\" in name or ":" in name:
        raise ValueError(f"Unsafe archive entry: {name!r}")


def renamed(name):
    name = name.replace("shaders/lib/", "shaders/Lib/", 1)
    if name == "shaders/Lib/settings.glsl":
        name = "shaders/Lib/Settings.glsl"
    return name


def write_entry(archive, name, data):
    safe_entry(name)
    info = ZipInfo(name, FIXED_TIME)
    info.create_system = 3
    info.external_attr = 0o100644 << 16
    info.compress_type = ZIP_DEFLATED
    archive.writestr(info, data, compresslevel=6)


def include_errors(path):
    errors = []
    with ZipFile(path) as archive:
        names = set(archive.namelist())
        for name in sorted(names):
            if not name.endswith(SOURCE_EXTENSIONS):
                continue
            source = archive.read(name).decode("utf-8", errors="strict")
            for include in INCLUDE.findall(source):
                target = posixpath.normpath(
                    "shaders/" + include.lstrip("/") if include.startswith("/")
                    else posixpath.dirname(name) + "/" + include)
                if target not in names:
                    errors.append({"source": name, "include": include, "target": target})
        # Also verify non-Minecraft image files consumed by shaders.properties.
        for line in archive.read("shaders/shaders.properties").decode("utf-8").splitlines():
            match = re.match(r"\s*(?:texture\.[^=]+|customTexture\.[^=]+)\s*=\s*(\S+)", line)
            if match and ":" not in match[1] and "shaders/" + match[1] not in names:
                errors.append({"source": "shaders/shaders.properties", "texture": match[1]})
    return errors


def build_shader(client, output):
    source = client / "shaderpacks" / SHADER
    if sha(source) != SOURCE_SHADER_SHA:
        raise ValueError("IterationRP source differs from the audited release; inspect it before rebuilding.")
    destination = output / "shaderpacks" / SHADER
    if destination.exists():
        raise FileExistsError(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    before = include_errors(source)
    renames = []
    with ZipFile(source) as src, ZipFile(destination, "x") as dst:
        mapped = [renamed(n) for n in src.namelist()]
        if len(mapped) != len(set(mapped)):
            raise ValueError("Case repair would produce duplicate ZIP entries.")
        for name in sorted(src.namelist()):
            target = renamed(name)
            if target != name:
                renames.append({"old": name, "new": target})
            write_entry(dst, target, src.read(name))
    after = include_errors(destination)
    if after:
        raise ValueError(f"Unresolved shader includes after repair: {after[:4]}")
    with ZipFile(source) as src, ZipFile(destination) as dst:
        if dst.testzip() is not None:
            raise ValueError("Shader ZIP CRC validation failed.")
        for name in src.namelist():
            if src.read(name) != dst.read(renamed(name)):
                raise ValueError(f"Unexpected shader content change: {name}")
    return {"source_sha256": sha(source), "output_sha256": sha(destination),
            "output_size": destination.stat().st_size, "unresolved_before": len(before),
            "unresolved_after": len(after), "entry_renames": renames,
            "all_entry_contents_byte_identical": True, "shader_settings_modified": False}


def build_resources(client, output, emi_pack):
    source = client / "resourcepacks" / RP
    with ZipFile(source) as archive:
        original = json.loads(archive.read(POSE))
        animations = json.loads(archive.read(
            "assets/cobblemon/bedrock/pokemon/animations/0715_noivern/noivern_mega.animation.json"))
    if "animation.noivern_mega.blink" in animations["animations"]:
        raise ValueError("MegaStorm now supplies blink; this compatibility removal needs review.")
    patched = json.loads(json.dumps(original))
    removed = []
    for name, pose in patched["poses"].items():
        quirks = pose.get("quirks", [])
        count = quirks.count(BROKEN_QUIRK)
        if count:
            pose["quirks"] = [q for q in quirks if q != BROKEN_QUIRK]
            removed.extend([name] * count)
    if len(removed) != 6:
        raise ValueError(f"Expected six invalid blink references, found {len(removed)}.")
    restored = json.loads(json.dumps(patched))
    for name in removed:
        restored["poses"][name]["quirks"] = original["poses"][name]["quirks"]
    assert restored == original, "Unexpected poser edits"
    destination = output / "resourcepacks" / "Kewz Client Compatibility.zip"
    destination.parent.mkdir(parents=True, exist_ok=True)
    entries = {"pack.mcmeta": encode({"pack": {"pack_format": 34,
                "description": "Kewz Client Compatibility - MegaStorm animation safety and EMI stones"}}),
               POSE: encode(patched),
               "CREDITS.txt": (
                   "MegaStorm compatibility configuration for Cobblemon 1.7.3.\n"
                   "MegaStorm: Necronyx; Mega Noivern model: User_JJ.\n"
                   "https://modrinth.com/datapack/the-megastorm\n"
                   "Requires the original MegaStorm Resource Pack. No models, textures, or animations included.\n"
                   "Only the six calls to a missing Noivern blink animation are removed.\n"
               ).encode()}
    with ZipFile(emi_pack) as emi:
        for name in emi.namelist():
            if name.startswith("assets/emi/") and name.endswith(".json"):
                safe_entry(name)
                json.loads(emi.read(name))
                entries[name] = emi.read(name)
    if not any(n.startswith("assets/emi/index/stacks/") for n in entries):
        raise ValueError("The validated EMI stone index is missing.")
    with ZipFile(destination, "x") as archive:
        for name, data in sorted(entries.items()):
            write_entry(archive, name, data)
    with ZipFile(destination) as archive:
        assert archive.testzip() is None
    return {"source_pack_sha256": sha(source), "removed_blink_poses": removed,
            "other_poser_fields_preserved": True, "output_sha256": sha(destination),
            "output_size": destination.stat().st_size, "entries": sorted(entries)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--client", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--emi-pack", type=Path)
    parser.add_argument("--shader-only", action="store_true")
    parser.add_argument("--verify-shader", type=Path, help="Read-only case-sensitive include audit")
    parser.add_argument("--release-migrations", type=Path, help="Verified 1.0.15 manifest to carry forward")
    args = parser.parse_args()
    if args.verify_shader:
        errors = include_errors(args.verify_shader)
        if errors:
            raise ValueError(f"{len(errors)} broken shader references: {errors[:4]}")
        print("PASS: all shader include and texture references resolve with exact ZIP case.")
        return
    if args.release_migrations:
        if args.output is None:
            parser.error("--output is required for generated migrations")
        if sha(args.release_migrations) != BASE_MANIFEST_SHA:
            raise ValueError("Migration input differs from the verified published 1.0.15 manifest.")
        base = json.loads(args.release_migrations.read_bytes())
        managed = {f["path"].casefold() for f in base["files"]}
        # 1.2.16 repairs every current managed file itself. Legacy cleanup is
        # for removed paths and exact historical seeds, not unchanged managed
        # destinations. Carry base deletions too: clients may skip that release.
        cleanup = {}
        for entry in base["legacyCleanup"] + base["deletedFiles"]:
            if entry["path"].casefold() not in managed:
                cleanup[(entry["path"].casefold(), entry["size"], entry["sha256"])] = entry
        outputs = {
            "1.0.16-legacy-cleanup.json": [cleanup[k] for k in sorted(cleanup)],
            "1.0.16-reoffer-seeds.json": base["reofferSeedPaths"],
            "1.0.16-seed-text-replacements.json": base["seedTextReplacements"],
        }
        args.output.mkdir(parents=True, exist_ok=True)
        for name, data in outputs.items():
            target = args.output / name
            if target.exists():
                raise FileExistsError(target)
            target.write_bytes(encode(data))
        print(json.dumps({name: len(data) for name, data in outputs.items()}, indent=2))
        return
    if args.client is None or args.output is None:
        parser.error("--client and --output are required for a build")
    args.output.mkdir(parents=True, exist_ok=True)
    report = {"shader": build_shader(args.client, args.output)}
    if not args.shader_only:
        if args.emi_pack is None:
            parser.error("--emi-pack is required unless --shader-only is selected")
        report["resources"] = build_resources(args.client, args.output, args.emi_pack)
    (args.output / "verification.json").write_bytes(encode(report))
    print(json.dumps({key: {k: v for k, v in value.items() if k != "entry_renames"}
                      for key, value in report.items()}, indent=2))


if __name__ == "__main__":
    main()
