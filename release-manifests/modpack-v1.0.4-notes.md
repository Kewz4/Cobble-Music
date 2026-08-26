# Kewz's Cobblemon 1.0.4

First signed updater baseline for the approved Kewz's Cobblemon client pack.

## Updated mods

- AsyncParticles 21.1.4.0 → 21.1.4.1
- Catch Rate Display 2.8.24 → 2.9.2
- Only Bottle Caps 1.3.0 → 1.4.0
- Packed Packs 2.2.3 → 2.2.4
- Particle Rain beta.10 → beta.11
- VCC 0.4.0-beta.2 → 0.4.0

## Cleanup and reliability

- Removes the exact, hash-matched superseded 1.0.3 jars during first-run migration.
- Resolves duplicate ExtraSounds and Journey Mounts mod IDs.
- Adds narrowly scoped Log Begone guards for the observed Axiom/OpenGL flood without hiding general warnings or errors.
- Seeds MCBrowser's tabs file so clean shutdown no longer fails when its config directory is absent.
- Includes the current music, notification metadata, Reactive Music bridge, and resource-pack configuration from the boot-tested canonical client.

The updater verifies the signed manifest, every chunk, the reconstructed payload, and every managed file before committing the update.
