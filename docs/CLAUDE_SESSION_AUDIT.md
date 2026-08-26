# Sanitized Claude-session audit

The supplied Claude export was read as reference material only. It is **not**
part of this repository and must not be committed or uploaded.

## What existed before this project

- No updater, GitHub project, release manifest, signing system, transaction
  journal, rollback mechanism, or Prism launch hook existed.
- Claude used temporary, ad-hoc scripts to build a Modrinth `.mrpack`; those
  scripts hard-code paths and are not a reproducible release system.
- The canonical live client is currently:
  `C:\Program Files\Prism Launcher\instances\Kewz's Cobblemon - Client 1.0.1\minecraft`
- Useful historical references include the development, backup, and upstream
  Prism instances. They are references, not updater input.

## Release baseline warning

The delivered `1.0.3` `.mrpack` is stale: later resource-pack and option
changes exist only in the live client. Future update manifests must be built
from a newly audited live canonical client, not that `.mrpack` or the stale
`mrpack\` folder retained inside the instance.

## Security warning

The export and Claude temporary scratchpad contain a plaintext hosting
credential. They must never be shared, committed, or uploaded. Rotate that
credential before continuing any external handoff or release work.

## Distribution constraint

The current music archive and full `.mrpack` exceed GitHub Releases' per-asset
limit. The release pipeline uses signed 256 MiB payload chunks by default instead. Public
distribution additionally requires the maintainer to confirm rights for every
third-party asset.
