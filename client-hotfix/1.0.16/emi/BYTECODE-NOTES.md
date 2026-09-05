# Installed EMI behavior verified for this patch

Inspected read-only with `javap -p -c`:

- DEV `mods/emi-1.1.22-SNAPSHOT+1.21.1+fabric.jar`, SHA-256
  `bb953b8eddebe5b3007bf8ac511432d6afda0d09dac49290f4130cc9e534e309`.
- DEV `mods/cobblemon_emi_compat-fabric-1.1.0.jar`.
- DEV `mods/mega_showdown-fabric-1.9.9+1.7.3+1.21.1.jar`.

## JSON path and stack codec

`EmiData.init` installs the `index/stacks` loader. `EmiDataLoader` filters to the
`emi` namespace and reads all resources at each matching identifier. Thus the
client resource path is `assets/emi/index/stacks/megastorm.json`, not `data/`,
`assets/megastorm/`, or a server config.

`EmiData.lambda$init$16` reads `added` as an array of objects whose `stack` is an
EMI ingredient (and whose optional `after` is another ingredient). `removed` is
an array of ingredients. The pack deliberately does not use `filters` or
`disable`.

`ItemEmiStackSerializer.getType()` returns `item`. `EmiStackSerializer.deserialize`
reads `id`, optional string `nbt`, and optional `amount` (default 1). At offsets
179-211 it parses `nbt` as SNBT and decodes `DataComponentChanges.CODEC` with
client registry access. Therefore the compound is a component patch directly,
not legacy `{tag:...}` or an outer `{components:...}` item save object.

The six recipes only use strings, string lists and integer model IDs. The
checked-in SNBT deliberately uses the ASCII JSON-compatible subset, so the
validator can independently recover the complete component map with `json.loads`.
Text JSON remains nested *strings*, including its original spaces and fields.
No claim is made that a JSON parser is a general-purpose SNBT validator.

## Default index and duplicate handling

`EmiStackList.reload` gathers registry item stacks and creative-group variants;
it is not an enumeration of all component-bearing recipe results. Its final
collection uses `ObjectOpenCustomHashSet(StrictHashStrategy)` (offsets 674-685).
`StrictHashStrategy` compares the item and complete component patch.

`EmiStackList.bake` processes data files later. For each file it removes the
`removed` ingredients (offsets 52-172), then appends `added` (200-354). Added
entries do not receive another deduplication pass. `lambda$bake$6` converts each
removal to `EmiPort.compareStrict()`, which is `Comparison.compareComponents()`.
`EmiStack.isEqual` requires BOTH comparisons when they differ; `hashCode` uses
the item key. Consequently these six complete-component removals do not match
ordinary paper or another paper variant with different name/lore/components.

This pack removes then adds only the same six complete stacks. It is idempotent
if an identical pack layer is encountered again, and also replaces matching
base/plugin entries already present when its data file runs. A separate later
producer can still introduce new/duplicate variants; do not knowingly ship
multiple independent registrations. Read-only inventory of top-level installed
DEV mod/resource-pack archives found no existing `assets/emi/index/stacks/*.json`
containing MegaStorm, registry-location components or paper.

This changes INDEX VISIBILITY only. Default paper recipe/usage comparisons are
not globally changed. `CobblemonEmiPlugin.register` only enables brewing stand,
cooking pot and berry-mutation integrations; it does not enumerate MegaStorm.

## Existing data and synchronization

Both approved datapack copies have SHA-256
`b41f5fdff2a5146ad871921d69d00853b6572b597a70683db76cccefb167f744`.
They contain the six complete recipe outputs and matching mega registry entries.
The resource pack already has the corresponding paper model overrides and assets.

MegaShowdown's `DatapackRegistry.register` registers `MEGA_REGISTRY_KEY` through
Fabric `DynamicRegistries.registerSynced`. The two stack component codecs are
string (`registry_type_component`) and identifier (`registry_location_component`);
Minecraft supplies codec-backed network serialization when no explicit packet
codec is provided. A client EMI index entry does not register a new server item,
modify a Pokemon's held item, or create new recipes.

No game, dedicated server, or integration transaction was run. Bytecode inspection
plus offline source/ZIP checks are the validation boundary.
