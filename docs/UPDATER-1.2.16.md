# Updater 1.2.16

This release converges directly to the complete latest signed inventory. It
validates SHA-256 and size instead of accepting a version marker or size alone.
Only missing/changed managed files are installed. Their contents can come from
any verified published payload containing the exact signed file identity; an
unusable intermediate delta no longer prevents the latest inventory applying.

Older alternate filenames for a currently managed Fabric mod are identified
by fabric.mod.json mod ID and moved into retained transaction recovery backups.
Unrelated extra mods and optional Axiom remain player-owned. Displaced managed
files are also retained in the instance's LocalAppData updater rollback area.

The minimize button and taskbar minimize/restore work during updates. The
window is not always-on-top. The former 30-minute whole-operation timeout is
removed; individual network requests retain their configured timeout.

The two official Packed Packs profiles (Default and Realistic) are managed
release content; custom profiles and selections remain player-owned. Data
packs in the dedicated datapacks directory are now supported. options.txt,
keybinds, video/audio settings, mutable shader option sidecars and ordinary
config defaults retain their existing ownership rules.

## Launcher limitation

The existing checksum-pinned 1.2.7 bootstrap still fetches the newest signed
EXE without a command change. It explicitly ignores nonzero updater exit
codes, however, and allows offline fallback. Therefore a failed run must not
be described as verified/up-to-date merely because Minecraft opens. The EXE
reports failure and does not commit successful state after a failed repair.
Changing that immutable wrapper's launch-availability policy requires a
separate approved bootstrap/command migration; it is not silently enabled here.

## Checks

The executable regression harness covers direct catalog repair, same-size
corruption, mixed Xaero versions with retained backups, missing intermediate
releases, prior payload sources, optional Axiom, player settings, transaction
rollback, download resumption, release signatures, and window layout. No game
or dedicated Minecraft server is launched by that harness.
