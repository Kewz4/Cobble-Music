#!/usr/bin/env python3
"""Validate pinned read-only inputs and build this folder's EMI-only resource ZIP.

Python 3.10+, standard library only. No Minecraft/Java execution or live writes.
All outputs are restricted to ./dist beside this script.
"""

import argparse
import hashlib
import io
import json
from pathlib import Path
import zipfile


ROOT = Path(__file__).resolve().parent
WORKSPACE = ROOT.parents[1]
DEV = Path(r"C:\Program Files\Prism Launcher\instances\Kewz's Cobblemon - Client DEV\minecraft")
DP_SHA256 = "b41f5fdff2a5146ad871921d69d00853b6572b597a70683db76cccefb167f744"
EMI_SHA256 = "bb953b8eddebe5b3007bf8ac511432d6afda0d09dac49290f4130cc9e534e309"
INDEX_PATH = "assets/emi/index/stacks/megastorm.json"
ZIP_NAME = "Kewz-MegaStorm-EMI-1.0.16.zip"
MODELS = {
    "golduckite": 770006,
    "laprasite": 770007,
    "noivernite": 770002,
    "rapidashite": 770008,
    "typhlosionite": 770004,
    "walreinite": 770003,
}
COMPONENT_KEYS = {
    "minecraft:custom_model_data",
    "minecraft:item_name",
    "minecraft:lore",
    "mega_showdown:registry_type_component",
    "mega_showdown:registry_location_component",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def json_bytes(value):
    return (json.dumps(value, indent=2, ensure_ascii=False) + "\n").encode("utf-8")


def load_index():
    return json.loads((ROOT / "src" / INDEX_PATH).read_text(encoding="utf-8"))


def decode_stack(stack):
    require(set(stack) == {"type", "id", "nbt"}, "Unexpected EMI stack fields")
    require(stack["type"] == "item", "Must use EMI's item serializer")
    require(stack["id"] == "minecraft:paper", "Do not invent MegaStorm item IDs")
    require(isinstance(stack["nbt"], str), "EMI nbt must be an SNBT string")
    # These SNBT strings deliberately use JSON's common subset: quoted ASCII
    # keys/strings, integer model IDs and arrays. Inner text JSON stays a string.
    components = json.loads(stack["nbt"])
    require(isinstance(components, dict), "Component patch must be a compound")
    require(stack["nbt"].isascii(), "Review SNBT escaping for new non-ASCII input")
    return {"id": stack["id"], "components": components}


def identity(stack):
    # Mirrors the scope of EMI compareStrict: item + complete component patch,
    # not just the item ID. This is an offline model, not a game runtime test.
    return json.dumps(decode_stack(stack), sort_keys=True, separators=(",", ":"))


def apply_index(existing, index):
    removed = {identity(stack) for stack in index["removed"]}
    return [stack for stack in existing if identity(stack) not in removed] + [
        entry["stack"] for entry in index["added"]
    ]


def validate_index(index, recipes):
    require(set(index) == {"removed", "added"}, "Do not add broad filters/disable")
    require(len(index["removed"]) == len(index["added"]) == 6, "Expected six stacks")
    require(all(set(entry) == {"stack"} for entry in index["added"]), "Unexpected addition fields")
    added = [entry["stack"] for entry in index["added"]]
    require(index["removed"] == added, "Removal must match only the six exact additions")
    require(len({identity(stack) for stack in added}) == 6, "Duplicate custom stack")
    results = []
    for name, stack in zip(sorted(MODELS), added):
        recipe = recipes[name]
        require(recipe["count"] == 1, "EMI's implicit amount=1 must match recipe")
        require(set(recipe) == {"count", "id", "components"}, "Review new result fields")
        decoded = decode_stack(stack)
        require(decoded == {k: recipe[k] for k in ("id", "components")}, f"Recipe mismatch: {name}")
        components = decoded["components"]
        require(set(components) == COMPONENT_KEYS, f"Unexpected component set: {name}")
        require(type(components["minecraft:custom_model_data"]) is int, "Model ID must be an integer")
        require(components["minecraft:custom_model_data"] == MODELS[name], f"Model mismatch: {name}")
        require(components["mega_showdown:registry_type_component"] == "mega", "Wrong registry type")
        require(components["mega_showdown:registry_location_component"] == f"megastorm:{name}", "Wrong binding")
        title = json.loads(components["minecraft:item_name"])
        lore = [json.loads(line) for line in components["minecraft:lore"]]
        require(title == {"translate": f"item.name.{name}", "italic": False}, "Changed name")
        require(lore == [{"translate": f"item.desc.{name}", "color": "gray", "italic": False}], "Changed lore")
        results.append({"registry": f"megastorm:{name}", "model": MODELS[name], "component_count": 5})
    plain = {"type": "item", "id": "minecraft:paper", "nbt": "{}"}
    unrelated = {"type": "item", "id": "minecraft:paper", "nbt": '{"minecraft:custom_model_data":42}'}
    seeded = [plain, unrelated] + added + added
    once = apply_index(seeded, index)
    require(once == [plain, unrelated] + added, "Deduplication changed unrelated paper")
    require(apply_index(once, index) == once, "Repeated pack application is not idempotent")
    return results


def build_zip(payload):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_STORED) as archive:
        for name in sorted(payload):
            info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, payload[name])
    return buffer.getvalue()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dev-root", type=Path, default=DEV)
    parser.add_argument("--datapack", type=Path, default=WORKSPACE / "step-next-server/payload/world/datapacks/MegaStorm Gen1 Fixes.zip")
    parser.add_argument("--check-only", action="store_true", help="Validate without writing outputs")
    args = parser.parse_args()
    require((ROOT / "dist").resolve().parent == ROOT, "Output folder escaped sidecar scope")
    dev_dp = args.dev_root / "datapacks/MegaStorm Gen1 Fixes.zip"
    emi_jar = args.dev_root / "mods/emi-1.1.22-SNAPSHOT+1.21.1+fabric.jar"
    rp_path = args.dev_root / "resourcepacks/MegaStorm Gen1 Fixes (ResourcePack).zip"
    inputs = {}
    for label, path, expected in (
        ("server_payload_datapack", args.datapack, DP_SHA256),
        ("dev_datapack", dev_dp, DP_SHA256),
        ("installed_emi", emi_jar, EMI_SHA256),
    ):
        digest = sha256(path.read_bytes())
        require(digest == expected, f"Input hash changed; review before rebuilding: {path}")
        inputs[label] = {"path": str(path.resolve()), "sha256": digest}
    inputs["dev_resourcepack"] = {"path": str(rp_path.resolve()), "sha256": sha256(rp_path.read_bytes())}
    recipes = {}
    recipe_hashes = {}
    with zipfile.ZipFile(args.datapack) as archive:
        expected_paths = {f"data/megastorm/recipe/{name}.json" for name in MODELS}
        actual_paths = {name for name in archive.namelist() if name.startswith("data/megastorm/recipe/") and name.endswith(".json")}
        require(actual_paths == expected_paths, "Review changed MegaStorm recipe set")
        for name in sorted(MODELS):
            path = f"data/megastorm/recipe/{name}.json"
            content = archive.read(path)
            recipes[name] = json.loads(content)["result"]
            recipe_hashes[path] = sha256(content)
            require(f"data/megastorm/mega_showdown/mega/{name}.json" in archive.namelist(), f"Missing mega binding: {name}")
    index = load_index()
    stones = validate_index(index, recipes)
    with zipfile.ZipFile(rp_path) as archive:
        names = set(archive.namelist())
        paper = json.loads(archive.read("assets/minecraft/models/item/paper.json"))
        overrides = {entry["predicate"].get("custom_model_data"): entry["model"] for entry in paper["overrides"]}
        translations = {}
        for path in sorted(names):
            if path.startswith("assets/") and path.endswith("/lang/en_us.json"):
                translations.update(json.loads(archive.read(path)))
        for name, model in MODELS.items():
            require(overrides.get(model) == f"megastorm:item/megas/{name}", f"Missing model predicate: {name}")
            require(f"assets/megastorm/models/item/megas/{name}.json" in names, f"Missing model: {name}")
            require(f"assets/megastorm/textures/item/megas/{name}.png" in names, f"Missing texture: {name}")
            require(f"item.name.{name}" in translations and f"item.desc.{name}" in translations, f"Missing name/lore translations: {name}")
    src = ROOT / "src"
    payload = {path.relative_to(src).as_posix(): path.read_bytes() for path in src.rglob("*") if path.is_file()}
    require(set(payload) == {"pack.mcmeta", INDEX_PATH}, "Resource pack must contain only metadata and EMI index")
    require(json.loads(payload["pack.mcmeta"])["pack"]["pack_format"] == 34, "Wrong 1.21.1 resource-pack format")
    blob = build_zip(payload)
    require(blob == build_zip(payload), "Nondeterministic ZIP output")
    with zipfile.ZipFile(io.BytesIO(blob)) as archive:
        require(archive.testzip() is None, "ZIP CRC failure")
        require({name: archive.read(name) for name in archive.namelist()} == payload, "ZIP payload mismatch")
    report = {
        "status": "PASS",
        "zip": {"name": ZIP_NAME, "bytes": len(blob), "sha256": sha256(blob)},
        "inputs": inputs,
        "source_recipe_sha256": recipe_hashes,
        "entries": {name: {"bytes": len(data), "sha256": sha256(data)} for name, data in sorted(payload.items())},
        "stones": stones,
        "checks": {
            "recipe_components_names_lore_exact": True,
            "six_unique_complete_stacks": True,
            "exact_remove_then_add_idempotence_model": True,
            "ordinary_and_unrelated_custom_paper_preserved_model": True,
            "matching_existing_models_textures_translations": True,
            "deterministic_two_builds_equal": True,
            "only_two_client_resource_entries": True,
        },
        "limitations": [
            "Schema and strict removal semantics verified from pinned installed EMI bytecode; no Minecraft runtime test.",
            "Recipe/usage comparison remains EMI's existing behavior; no plugin or global paper comparison change.",
            "A later independent pack/plugin could add further variants; this pack only replaces its six exact stacks.",
            "Not enabled or copied into DEV. No EMI player history, server files, or gameplay data modified.",
        ],
    }
    if not args.check_only:
        dist = ROOT / "dist"
        dist.mkdir(exist_ok=True)
        (dist / ZIP_NAME).write_bytes(blob)
        (dist / "validation.json").write_bytes(json_bytes(report))
        (dist / "SHA256SUMS.txt").write_bytes(f"{sha256(blob)}  {ZIP_NAME}\n".encode("ascii"))
    print(json.dumps({"status": "PASS", "zip": report["zip"], "stack_count": 6, "check_only": args.check_only}, indent=2))


if __name__ == "__main__":
    main()
