## Updater 1.2.16

- SHA-256 validation of the complete latest signed inventory, including same-size corruption.
- Direct repair from exact signed payload origins instead of relying on every intermediate installation.
- Conflicting older copies of managed Fabric mods moved to retained recovery backups.
- Axiom and ordinary player settings remain optional/player-owned.
- Working minimize/taskbar restore; no always-on-top window.
- Datapack root support and once-per-revision delivery of official Default/Realistic profiles; Packed Packs' own runtime rewrites do not trigger repairs.
- Removes the 30-minute total-operation timeout that could interrupt large downloads.

The current friend command fetches this EXE automatically from the signed stable channel. Its immutable legacy wrapper still allows offline launch and ignores updater failure exit codes; a failed run is not proof of being up to date. This release does not silently change that launch-availability policy.

SHA-256 (`CobbleMusicUpdater.exe`):
`23d84cff8137d5e457011e301036fd4ebdcb6d717faee0e8a28188af8f36a0f7`

Build: .NET SDK 10.0.103, locked dependencies, self-contained win-x64.
Verified: executable regression harness; delta publisher tests; release metadata/signature/bootstrap checks.
