# MegaStorm EMI-only client hotfix 1.0.16

Staged for parent integration; nothing is installed/enabled in DEV or on a server.

## Artifacts

- `src/pack.mcmeta`: Minecraft 1.21.1 resource-pack format 34.
- `src/assets/emi/index/stacks/megastorm.json`: six exact replacements/additions.
- `dist/Kewz-MegaStorm-EMI-1.0.16.zip`: only those two client resource entries.
- `dist/validation.json`: pinned source hashes, exact output preservation, asset
  coverage, duplicate-handling model, ZIP checks and validation limitations.
- `dist/SHA256SUMS.txt`: artifact hash.
- `BYTECODE-NOTES.md`: verified installed schema and strict-removal semantics.

The stacks are **minecraft:paper**, not invented `megastorm:*` item IDs. All five
recipe components are retained, including exact item-name/lore JSON strings:

| Mega registry entry | Custom model data |
| --- | ---: |
| megastorm:golduckite | 770006 |
| megastorm:laprasite | 770007 |
| megastorm:noivernite | 770002 |
| megastorm:rapidashite | 770008 |
| megastorm:typhlosionite | 770004 |
| megastorm:walreinite | 770003 |

## Reproduce

Run from this folder with Python 3.10+ (standard library only):

```text
python -B build.py
python -B -m unittest discover -s tests -v
python -B build.py --check-only
```

Default read-only inputs are DEV's installed EMI JAR, MegaStorm datapack/resource
pack, and `step-next-server/payload/world/datapacks/MegaStorm Gen1 Fixes.zip` under
the task workspace. `--dev-root` and `--datapack` can select relocated copies;
the approved datapack/EMI hashes must still match. The builder writes ONLY its
own `dist/` folder. ZIP entries are sorted with fixed 1980 timestamps, Unix mode
0644 and STORE compression to avoid compressor-version-dependent output.

## Parent integration

Either include/enable the standalone ZIP with the existing MegaStorm resource
pack, or merge just `assets/emi/index/stacks/megastorm.json` into one parent-owned
client pack. Do not overwrite the parent's `pack.mcmeta` when merging. Do not
ship both the standalone pack and another independent generated copy. Exact
remove-then-add behavior prevents duplicate canonical variants and preserves
ordinary/unrelated paper; it does not broadly filter the paper item ID.

Existing MegaStorm models/translations and server recipe/registry data remain
dependencies. This ZIP adds no recipes, textures, Pokemon assets, classes or
gameplay changes. It leaves paper recipe/usage comparison behavior unchanged.
No server action is required for adding client index entries. Any later client
activation/verification is owned by the parent; this sidecar does not launch it.

Do NOT modify/reset `emi.json` player history as part of this patch. The unrelated
saved-history warning's offending record has not been identified.
