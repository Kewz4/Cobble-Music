# Client hotfix 1.0.16

Canonical authoring source: `Kewz's Cobblemon - Client DEV/minecraft`.
This release does not change the Minecraft server. Only Outline and the render
guard are new mods; both declare the Fabric client environment.

## Reproducible inputs and checks

- Python 3.12 standard library: `tools/Build-ClientHotfix-1.0.16.py` repairs the
  audited IterationRP ZIP's filename case and builds the small compatibility RP.
  Use the backed-up 1.0.15 shader as input; every contained file is byte-identical
  after the `shaders/lib` / `Settings.glsl` filename repair.
- `emi/build.py --datapack <local-server-payload-copy> --dev-root <minecraft>`
  validates the exact six recipe component stacks against the pinned datapack,
  resource pack and installed EMI build. Its index is merged into the compatibility
  RP; do not ship the standalone EMI ZIP too.
- `render-guard/build.ps1` uses Java 21 and explicit cached dependency paths.
  The source is intentionally in Minecraft 1.21.1 intermediary names; no remapper
  or dependencies are bundled. `verify.ps1` records the 40 headless migration
  assertions, target checks and repeat-build check from the sidecar handoff.
- Before packaging, run `python tools/Build-ClientHotfix-1.0.16.py
  --verify-shader "<minecraft>/shaderpacks/Max Quality (Iteration RP - Path Traced).zip"`.
- Generate historical migrations with the builder's `--release-migrations`
  pointing at the exact published 1.0.15 manifest. Include prior deletions for
  skipped-release users. Current managed paths are repaired by updater 1.2.16,
  not by legacy cleanup; unchanged current shader names must not be cleaned up.

The authoring instance has a genuine 1.0.13 updater receipt, despite later
maintainer edits. Stage with `-BaseVersion 1.0.15 -SourceReceiptVersion 1.0.13`:
the publisher verifies BOTH signed manifests, the signed parent relationship,
and the exact older receipt inventory. It never fabricates an installed receipt.
Without this explicit option, the original exact-delta-base receipt check stays
mandatory. Review the full changed/deleted list before `-ResumePublish`.

## Delivery and settings

Eight changed/new managed files, no deletions relative to 1.0.15, 677 create-only
defaults. Both official resource profiles put Kewz Client Compatibility first;
Resource Pack Overrides requires it. The original MegaStorm resource ZIP is not
modified. Immersive Weathering LabPBR remains Realistic-only.

Outline settings are the maintainer's DEV settings captured on September 5,
2026. They are seeds, not integrity-managed config. Existing players keep their
own Outline preferences. Shader options, vanilla/Sodium/Voxy video settings,
audio, saves and user keybinds stay player-owned, apart from the already-reviewed
one-time K-key collision migration retained for users skipping older releases.

The render guard changes only absent-world/player rendering. Its NEC migration
backs up the original config, changes only `catchGameloop` once, synchronizes the
already-loaded NEC config, and records a marker. Later intentional settings edits
are respected. The root mod that originally clears the client player is unknown;
this guards the observed crash path, not every possible rendering fault.

## Permissions and limitations

The maintainer explicitly confirmed redistribution permission for Actions &
Stuff - Outline and the modified MegaStorm compatibility data on September 5,
2026, and previously confirmed permission for the modified IterationRP shader.
That permission is not a new blanket license for third parties. Retain the
original authors' notices and licenses; the render guard itself is MIT.

No local Minecraft/server launch or in-game smoke test was performed. Static
verification is not proof of runtime behavior. Ask a player to verify shader
loading, Mega Noivern rendering and the six EMI entries after updating.

Staged manifest SHA-256:
`56119a153b3b8aff0ab936d1b8fbb97db04c5e4cc1520394ffeab439827936df`.
Payload: 97,686,650 bytes; SHA-256
`aade960730926633a7c3004b2b21aa2796e01ba1c1c4dcf42d2daaca0797419d`.
