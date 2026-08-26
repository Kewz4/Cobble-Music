# Kewz's Cobblemon updater

`CobbleMusicUpdater.exe` is a self-contained Windows executable that Prism
Launcher runs before Minecraft. It keeps a friend’s approved Kewz's Cobblemon
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

## What players see at launch

When Prism starts the updater normally, a small centered **Kewz's Cobblemon**
card appears immediately with **Checking for updates…**. It has no Windows
title bar and uses a custom animated progress indicator while GitHub is being
checked. If a signed pack update is available, the same card shows download
percentage and then the file-install count.

On an ordinary no-update, offline-fallback, or successful-update launch, it
briefly shows the result and closes automatically before Minecraft starts. A
local recovery or concurrent-update error stays visible and blocks launch,
because starting a partly updated pack would be unsafe.

The GUI build also writes its own rotating diagnostic log at
`minecraft/cobble-music-updater/updater.log`; it never writes to Minecraft's
normal `logs/` directory. The blocked state tells the player to check that
file when more detail is needed.

The `--no-ui` switch is only for diagnostics and automated tests; do not put
it in Prism's normal pre-launch command.

Normal source or updater-binary releases can live in this repository without
interfering with clients. They are ignored unless they use the reserved,
signed `modpack-v<version>` release format.

## Why releases are chunked

GitHub limits individual Release assets to under 2 GiB. The current Reactive
mega pack alone is about 2.42 GiB, while the older full `.mrpack` is about
3.06 GB. The publisher therefore creates 256 MiB by default
`cobble-music-payload.part###` assets. The updater reconstructs the ZIP only
after every downloaded part passes its hash.

Do not upload the monolithic mega pack or a full `.mrpack` as one GitHub
Release asset.

## Maintainer prerequisites

- PowerShell 7 on Windows
- .NET SDK 10.0.103 exactly (`global.json` disables SDK roll-forward)
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
.\tests\Test-UpdaterBuildReproducibility.ps1
.\tools\Build-CobbleMusicUpdater.ps1
```

The signature test uses a committed public fixture and does not require the
private key. A maintainer can add `-PrivateKey <offline-key-path>` for an
optional live signing round trip.

The reproducibility test exports one exact commit into two clean directories
with different absolute paths, performs a cold build in each and a repeated
warm build in one, then compares all three EXEs byte-for-byte and by SHA-256.
When the publisher runs it, the independently built EXEs must also be
byte-identical to the actual staged release artifact. Release builds map their
physical source root to a fixed virtual path, omit debug/PDB and Source Link
data, ignore Git revision metadata, and use the exact SDK selected by
`global.json`. The builder supplies the repository's explicit `NuGet.Config`,
uses locked versions and content hashes, disables the shared NuGet HTTP cache,
and extracts packages only under that exact source root's `updater\packages`
directory. A repeated build may reuse only its own root's cache; a distinct
source root gets a distinct cache regardless of `NUGET_PACKAGES`. The builder
also disables inherited Directory.Build/Central Package imports and rejects
implicit MSBuild inputs found anywhere above the project.
`.gitattributes` keeps text build inputs at LF in every clean checkout while
explicitly treating binaries as binary.

The builder emits:

```text
updater\dist\win-x64\CobbleMusicUpdater.exe
```

Use `-Runtime win-arm64` on an ARM Windows machine, and pass the same runtime
to the installer.

## Publish an updater-binary release

Updater binaries use their own `updater-v*` releases and are completely
separate from signed `modpack-v*` payload releases. The updater publisher
derives the release version from the single `BuildInfo.Version` constant,
requires the project version to match it, and accepts only canonical
three-part versions such as `1.2.1` (no leading zeroes or fourth component).
A new stable updater release must be strictly newer than every existing stable
`updater-v*` release; exact reruns of their own draft or published release stay
idempotent, while downgrades and alternate spellings of the same numeric
version are rejected.

The publisher resolves Git only through its own repository root, captures one
clean commit, exports the complete tree without reading live working-tree
bytes, and uses the builder, source, tests, bootstrap, SDK pin, package lock,
and NuGet configuration from that export. It builds self-contained `win-x64`,
computes the EXE's SHA-256, tests a proposed bootstrap containing that exact
version/hash, and proves that the staged EXE is byte-identical to independent
builds of the same commit. The eventual Git tag and release target that exact
commit.

First stage locally. This runs every `tests/Test-*.ps1` plus every updater
`*.Tests.csproj`, atomically refreshes the tracked bootstrap only after they
pass, and makes no GitHub change. Console-style test projects are executed
with `dotnet run` and must print their exact success marker; test-SDK projects
use `dotnet test`. A project that only builds without proving execution blocks
the release.

```powershell
.\tools\Publish-CobbleMusicUpdater.ps1
```

Use `-DryRun` to build and test the proposed release without changing either
the tracked bootstrap or GitHub. Release inputs must already be committed even
for a dry run because the build is intentionally sourced from an exact commit,
not the working tree. After a normal staging run, review and commit the
refreshed bootstrap and local dist artifact. GitHub modes re-check that the
same source commit and bootstrap remain bound before any remote mutation.
In particular, dry-run never creates or changes a Git ref.

Create or resume the release as a persistent draft:

```powershell
.\tools\Publish-CobbleMusicUpdater.ps1 -UploadDraft
```

Before creating or uploading that draft, the publisher reserves the exact
lightweight `refs/tags/updater-v<version>` ref at the captured source commit.
If another process races to create it, the run continues only when a re-fetch
shows the same lightweight ref and commit; an annotated tag or foreign target
blocks the release. A failed upload may therefore leave this safe tag reserved
alongside the persistent draft. Every upload boundary and the publication
PATCH revalidate it, including once immediately after publication.

Rerunning that command retains already verified assets, uploads only missing
assets, and leaves the validated release as a draft. An incomplete `starter`
asset, uploaded size/digest mismatch, unexpected or case-colliding name,
duplicate, or unknown state blocks the run; ordinary resume never deletes a
remote asset. The exact remote inventory is:

- `CobbleMusicUpdater.exe`
- `Bootstrap-CobbleMusicUpdater.ps1`

For each asset, GitHub must report `uploaded`, the exact local byte length,
and the exact SHA-256 digest. If an interrupted GitHub CLI process has
definitely stopped but left an expected-name asset in GitHub's `starter`
state, resume that existing draft with the additional recovery gate:

```powershell
.\tools\Publish-CobbleMusicUpdater.ps1 -UploadDraft -RepairStaleUploads
```

That mode validates the complete draft, exact tag target, and every asset
before each deletion. It deletes only an expected-name asset that is still
`starter`; it never deletes an uploaded mismatch or unknown state. Never use
the repair switch while another uploader may still be active.

Only after reviewing the exact draft, publish it with both explicit gates:

```powershell
.\tools\Publish-CobbleMusicUpdater.ps1 -Publish -ConfirmPublish
```

The publisher re-reads and validates the draft immediately before changing
`draft` to `false`. That fresh exact-ID GET must still report the original tag
and source commit with `draft=true` and `prerelease=false`, plus the complete
two-asset inventory. The PATCH response is not trusted: another exact-ID GET
must then report the same ID/tag/commit with `draft=false`,
`prerelease=false`, and the same complete inventory. A failed upload or
validation deliberately retains the draft so the next run can resume safely.
After the final draft/assets GET, the publisher rechecks the bound local source
commit and reserved lightweight tag, with the tag check directly adjacent to
the PATCH.
Authentication comes only from the GitHub CLI credential store; the publisher
accepts and emits no secrets.

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

Close Prism Launcher before running either installer. Both scripts fail before
editing `instance.cfg` while `prismlauncher.exe` is running, then check again
directly beside the final configuration write to minimize the remaining
start-between-check-and-save window. They preserve unrelated settings and
canonicalize `OverrideCommands`, `PreLaunchCommand`, and
`LogPrePostOutput` to exactly one entry each. A different nonempty pre-launch
command is still preserved unless `-Force` is deliberate.

The installers write the physical instance.cfg value with escaped quotes,
which QSettings requires to retain both spaces and quote boundaries when Prism
later saves the instance:

    PreLaunchCommand=\"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe\" --instance-dir \"$INST_DIR\" --minecraft-dir \"$INST_MC_DIR\" --prism-prelaunch

Prism waits for the pre-launch command, so Minecraft does not begin while an
update is being applied.

## First-time player setup

A player who has neither the updater EXE nor the Prism command needs one
one-time setup action—nothing exists on their PC yet that could check GitHub
when they press Play. The published bootstrap performs that setup safely:

1. verifies the selected Prism instance has `instance.cfg` and `minecraft/`;
2. downloads the exact `updater-v1.2.1` EXE and checks its SHA-256;
3. installs it at `minecraft/cobble-music-updater/CobbleMusicUpdater.exe`;
4. writes only the updater's own `updater.json` (backing up an existing one);
   and
5. backs up `instance.cfg` and adds the instance-specific Prism pre-launch
   command above.

The bootstrap refuses to replace a different existing Prism pre-launch command
unless the player deliberately reruns it with `-Force`. It does not touch
saves, `options.txt`, `servers.dat`, logs, or resource-pack selections.

After the bootstrap asset has been downloaded from
[updater-v1.2.1](https://github.com/Kewz4/Cobble-Music/releases/tag/updater-v1.2.1),
the player can paste this into PowerShell, replacing the example instance path:

```powershell
$uri = 'https://github.com/Kewz4/Cobble-Music/releases/download/updater-v1.2.1/Bootstrap-CobbleMusicUpdater.ps1'
$path = Join-Path $env:TEMP 'Bootstrap-CobbleMusicUpdater.ps1'
$expected = 'AB6A36DBDBAA2275590634303EAA8D5926D14D0ABE42D966841D15F06368B98F'
Invoke-WebRequest -Uri $uri -OutFile $path
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw 'Bootstrap checksum mismatch.' }
Unblock-File -LiteralPath $path
& $path -InstanceDirectory 'C:\Program Files\Prism Launcher\instances\<your instance name>'
```

This intentionally is **not** an `irm | iex` command. The script is downloaded,
verified by a pinned checksum, and only then run. Once it finishes, future
updates happen automatically when that player presses Prism's Play button.

## Publish a pack update

The canonical source is the current live client, not Claude’s stale `1.0.3`
`.mrpack` or its retained `mrpack\` folder. The publisher requires an explicit
mode so accidentally omitting a base version cannot upload the entire pack.
Release and base versions use exactly `major.minor.patch` with no leading
zeroes (for example, `1.0.5`); four-component variants are rejected.

The modpack publisher never builds or loads the updater from the current C#
working tree. Signing and verification use only
`updater\dist\win-x64\CobbleMusicUpdater.exe`, whose version, ProductVersion,
and SHA-256 must exactly match the committed `updater-v1.2.1` bootstrap pin.
The EXE is held read-locked from checksum verification through each signer or
verifier process. Keep the private signing key outside the Minecraft source,
every managed source root, and `release-output`; the publisher rejects those
locations before it inventories a single source file.

The already-published `1.0.4` release is the full schema-v1 baseline. A new
baseline is exceptional and must be requested explicitly:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 2.0.0 -FullBaseline
```

Normal releases are signed schema-v2 deltas. They download the prior release's
manifest and detached signature, verify the signature with the updater's
compiled-in public key, require the requested base/version binding, and compare
that complete signed file set with the canonical live client:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.5 -BaseVersion 1.0.4
```

To use reviewed local copies of the base assets, supply both paths:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.5 -BaseVersion 1.0.4 `
  -BaseManifestPath .\reviewed-base\cobble-music-update.json `
  -BaseSignaturePath .\reviewed-base\cobble-music-update.sig
```

Local copies do not bypass GitHub identity checks. The publisher still locates
the currently published, non-draft `modpack-v1.0.4` release through GitHub's
API and requires the local manifest and signature raw sizes/SHA-256 hashes to
match their exact `uploaded` release assets. A locally signed but unpublished
or replaced base is rejected so the resulting delta cannot reference an
unreachable chain. Both base files are read-locked before this comparison; the
same captured manifest bytes are signature-verified, parsed, and hashed into
the delta's `base.manifestSha256`, and that exact manifest/signature hash pair
is the pair revalidated at the final publication boundary.

A v2 manifest still carries the complete authoritative `files` state. Its
`payloadFiles` contains only changed/new files, while `deletedFiles` contains
the exact old path, size, and SHA-256 from the signed base. A deletion-only
release is valid and has no ZIP or part assets; raw v2 JSON may omit the
optional `payload` property entirely. Case/Unicode-colliding paths, unsafe
updater paths, stale or equal versions, incomplete differences, and case-only
renames are rejected before signing.
The staging/resume validators mirror updater 1.2's schema rules: a v1 baseline
must declare a canonical supported `minimumUpdaterVersion` and cannot carry
delta-only fields, while v2 cannot use path-only `deletePaths`. Truly absent
legacy collection properties receive the runtime model's empty-list defaults;
an explicit JSON `null` for any required collection is different and is
rejected exactly as the distributed updater rejects it.

Review `release-output\1.0.5\cobble-music-update.json`, its signature, and all
generated part hashes. Staging makes no GitHub change. Payload parts default to
256 MiB; the temporary combined ZIP is removed immediately after splitting so
staging does not retain a second full copy. One release may contain at most 997
payload parts: the manifest and detached signature consume the other two slots
of the publisher's 999-asset safety ceiling. If splitting would produce 998
parts, staging stops before signing; use a larger reviewed chunk size or reduce
the payload.
Before hashing or splitting that ZIP, the publisher streams every archive entry
and requires its canonical path, uncompressed size, and SHA-256 to exactly
match `payloadFiles`, with no duplicate, extra, or missing entries. This catches
a source file that changes after the initial inventory but before archiving.
Resume also streams the retained part files in their signed order, validating
each part and the concatenated payload's exact signed size/SHA-256 without
reconstructing or retaining another full ZIP.
The manifest and detached signature are opened as read-locked byte snapshots
before signature verification. Those same exact bytes are parsed, hashed for
the expected asset inventory, and held against replacement through upload;
their signature and hashes are checked again at every remote mutation boundary.
The manifest limit is exactly 8 MiB and the detached-signature limit is exactly
64 KiB, inclusive, matching updater 1.2; one additional byte is rejected before
verification, signing-resume, or upload.

After review, publish that **exact existing staging**:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.5 `
  -ResumePublish -ConfirmDistributionRights
```

Publishing creates a persistent draft before uploading. A retry reuses every
already-finalized asset whose name, size, and GitHub SHA-256 digest matches the
signed staging and uploads only missing assets. Unexpected, incomplete, or
mismatched assets stop the run; the release becomes public only after the
remote inventory matches exactly. `-Publish -ConfirmDistributionRights` may
instead stage and start that same draft workflow in one run.
Immediately before `draft=false`, the tool re-fetches the target release by
its original API ID and revalidates that same ID, reserved tag, draft and
prerelease state, and complete exact asset inventory. It then postvalidates the
same ID/tag in public state with the same inventory; a tag that was deleted,
recreated, retargeted, or otherwise changed is never patched through stale
state.

Updater 1.2 reads at most five 100-item GitHub release-index pages and treats a
full fifth page as unsafe truncation. Before publication, the publisher fully
scans the authenticated release index and permits the new public release only
when it would leave at most 499 non-draft releases in total; public prereleases
consume slots too. That prospective scan occurs before a final exact-ID
draft/assets re-fetch, leaving that identity snapshot directly adjacent to the
PATCH. The publisher repeats the count after the exact-ID publication check.
Drafts do not consume player-visible slots, but are still traversed so tag
uniqueness checks cannot miss a reserved draft beyond the updater's public scan
window.

Before any GitHub mutation, the publisher additionally requires the local
pinned updater EXE to match the exact `uploaded` size and GitHub SHA-256 digest
on the currently published, non-prerelease `updater-v1.2.1` release. It checks
that dependency again immediately before making the modpack draft public. For
a v2 delta it also re-fetches the stable base release at that final boundary
and requires both base manifest and signature to match the identity captured
during staging/resume. A replaced, unpublished, or incomplete dependency
leaves the delta draft private.

If an interrupted GitHub CLI process has definitely stopped but left an asset
in GitHub's incomplete `starter` state, recovery requires an additional,
explicit switch:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.5 `
  -ResumePublish -RepairStaleUploads -ConfirmDistributionRights
```

Before deleting anything, that mode validates the complete draft. Immediately
before every deletion it re-fetches the exact release by ID and revalidates its
tag, draft/prerelease state, and complete asset inventory. It deletes only the
same exact expected-name asset while its API state is still `starter`; if that
asset finished uploading or was replaced meanwhile, the old candidate is
skipped. Any uploaded size/digest mismatch, unexpected or case-colliding name,
duplicate, unknown state, or missing asset ID blocks the entire repair. Never
use the switch while another `gh` upload process is active.

Only publish after confirming permission to redistribute **every** third-party
mod, music track, sound asset, and configuration in the payload. The tool
publishes only the signed manifest, detached signature, and chunks; it never
reads Claude scratchpads or old `.mrpack` output.

> **Updater prerequisite:** schema-v2 deltas declare
> `minimumUpdaterVersion: 1.2.0`. Do not publish the first v2 delta until the
> updater release that understands base chains, `payloadFiles`, and exact
> `deletedFiles` has been installed for players. Updater 1.1.x supports the
> full schema-v1 baseline only.

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

## Friend installation and updater-binary upgrades

Give friends only the compiled EXE and approved setup/configuration—never the
private signing seed. Their Prism instance must be writable; a user-writable
Prism location is easiest.

This updater updates the signed **modpack payload**, not its own running EXE.
If a future release requires a newer updater binary, publish a new bootstrap
asset with a new pinned EXE checksum and have players run that one-time
bootstrap again. Older clients safely keep launching their last known-good
pack rather than applying a format they cannot verify.

## Claude workflow

Claude may prepare a sanitized `HANDOFF.md` or a PR with source changes, but
it never directly controls player machines or GitHub releases. After review,
ask Codex to audit, build, stage, and publish a version. Only a signed
`modpack-v*` GitHub Release becomes an update that friends can receive.
