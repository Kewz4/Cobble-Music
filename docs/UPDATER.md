# Cobble Music updater

`CobbleMusicUpdater.exe` is a self-contained Windows executable that Prism
Launcher runs before Minecraft. It keeps a friend’s approved Cobble Music
files current without touching player data.

## Runtime behavior

On each Prism launch, the updater:

1. selects the highest-version stable GitHub Release tagged `modpack-v*` that
   contains the required manifest assets;
2. downloads the small manifest and detached Ed25519 signature with strict
   response-size limits;
3. verifies the raw manifest with the public key compiled into the EXE before
   reading any remote paths, hashes, or URLs;
4. downloads the signed payload in resumable chunks, verifies every chunk and
   the reconstructed ZIP with SHA-256, then validates every extracted file;
5. applies a journaled, rollback-capable transaction under an instance lock;
   and
6. repairs missing managed files on later launches by checking their recorded
   size before declaring the installed version healthy.

Only these areas of the instance’s `minecraft` directory are ever eligible:

- `mods/`
- `resourcepacks/`
- `config/`
- `defaultconfigs/`
- `kubejs/`
- `scripts/`

The compiled allowlist cannot be widened by `updater.json`. The updater never
manages saves, logs, screenshots, `options.txt`, servers, accounts, or
arbitrary remote paths. It refuses junctions/symlinks in target paths.

Network trouble, GitHub rate limiting, a missing release, or invalid remote
content leaves the last known-good local pack unchanged and lets Prism launch.
An **unrecoverable local transaction** is the sole fail-closed case: Prism is
blocked so it cannot start with a half-applied pack.

Normal source or updater-binary releases can live in this repository without
interfering with clients. They are ignored unless they use the reserved,
signed `modpack-v<version>` release format.

## Why releases are chunked

GitHub limits individual Release assets to under 2 GiB. The current Reactive
mega pack alone is about 2.42 GiB, while the older full `.mrpack` is about
3.06 GB. The publisher therefore creates 512 MiB
`cobble-music-payload.part###` assets. The updater reconstructs the ZIP only
after every downloaded part passes its hash.

Do not upload the monolithic mega pack or a full `.mrpack` as one GitHub
Release asset.

## Maintainer prerequisites

- PowerShell 7 on Windows
- .NET 10 SDK
- 7-Zip or NanaZip providing `7z` on `PATH`
- GitHub CLI authenticated for `Kewz4/Cobble-Music` (`gh auth login`)
- substantial local free space: staging needs the chunk files, reconstructed
  ZIP, extraction tree, and temporary rollback copies. Plan for several times
  the payload size across the repository drive and `%LOCALAPPDATA%`.

## Signing key policy

The released EXE trusts one public key identified as
`cobble-music-release-1`. Its matching private seed lives only in the
maintainer’s offline local key folder, outside this repository. Normal
publishing uses that existing seed.

Do **not** run `--generate-keypair` as a normal setup step. A newly generated
seed does not match the public key in an already distributed EXE, so clients
will correctly reject releases signed by it.

Key generation is only for an intentional initial bootstrap or key rotation:

1. generate and securely back up the new offline seed;
2. replace the compiled public key in source and rebuild the updater;
3. distribute and install the rebuilt EXE to every client; then
4. begin signing new pack releases with that new seed.

Never put a private seed in Git, Claude transcripts, cloud drives, friends’
instances, or release assets.

## Build and verify the EXE

```powershell
.\tests\Test-ManifestSignature.ps1
.\tests\Test-TransactionRecovery.ps1
.\tools\Build-CobbleMusicUpdater.ps1
```

The signature test uses a committed public fixture and does not require the
private key. A maintainer can add `-PrivateKey <offline-key-path>` for an
optional live signing round trip.

The builder emits:

```text
updater\dist\win-x64\CobbleMusicUpdater.exe
```

Use `-Runtime win-arm64` on an ARM Windows machine, and pass the same runtime
to the installer.

## Install into one Prism instance

```powershell
.\tools\Install-CobbleMusicUpdater.ps1
```

The installer copies the EXE into `minecraft\cobble-music-updater\`, writes
its local configuration, backs up `instance.cfg`, and enables this
instance-specific Prism pre-launch command:

```text
"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe" --instance-dir "$INST_DIR" --minecraft-dir "$INST_MC_DIR" --prism-prelaunch
```

It refuses to overwrite a different existing `PreLaunchCommand`; inspect it
first and use `-Force` only when replacement is deliberate. Prism waits for
the pre-launch command, so Minecraft does not begin while an update is being
applied.

## Publish a pack update

The canonical source is the current live client, not Claude’s stale `1.0.3`
`.mrpack` or its retained `mrpack\` folder. Stage a release first:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.4
```

Review `release-output\1.0.4\cobble-music-update.json`, its signature, and
all generated chunk hashes. That command makes no GitHub change.

Publishing is deliberately gated by an explicit rights confirmation:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.4 `
  -Publish -ConfirmDistributionRights
```

Only use it after confirming permission to redistribute **every** third-party
mod, music track, sound asset, and configuration in the payload. The tool
publishes only the signed manifest and chunks; it never reads Claude
scratchpads or old `.mrpack` output.

### Clean first-run migration

Later releases automatically remove files that an earlier signed updater
release managed but no longer contains. For a friend migrating from a manual
or legacy pack, the updater will not broadly delete unknown mods or resource
packs. That would be unsafe.

Instead, create a reviewed JSON array containing only known legacy files and
their exact old size/hash, then pass it as `-LegacyCleanupManifest`:

```json
[
  {
    "path": "resourcepacks/old-music-pack.zip",
    "size": 123456,
    "sha256": "64-lowercase-hex-characters"
  }
]
```

The signed release deletes one of those files only if the friend’s local copy
still matches exactly. Changed or unknown files remain untouched. Build this
mapping from a reviewed known baseline, not guesses about a friend’s disk.

## Friend installation and updater upgrades

Give friends only the compiled EXE and approved setup/configuration—never the
private signing seed. Their Prism instance must be writable; a user-writable
Prism location is easiest.

Version 1 of this tool updates the modpack payload, not its own running EXE.
If a future release requires a newer updater, distribute the rebuilt EXE and
rerun the installer before publishing that pack release. Older clients safely
keep launching their last known-good pack rather than applying a format they
cannot verify.

## Claude workflow

Claude may prepare a sanitized `HANDOFF.md` or a PR with source changes, but
it never directly controls player machines or GitHub releases. After review,
ask Codex to audit, build, stage, and publish a version. Only a signed
`modpack-v*` GitHub Release becomes an update that friends can receive.
