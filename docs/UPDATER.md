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
- `shaderpacks/`
- `config/`
- `defaultconfigs/`
- `kubejs/`
- `scripts/`

The compiled allowlist cannot be widened by `updater.json`. Saves, logs,
screenshots, servers, accounts, and arbitrary remote paths are never managed.
The updater refuses junctions/symlinks in target paths.

Updater 1.2.6 adds a separate signed `seedFiles` inventory for reviewed
first-install defaults. A seed may be `options.txt`, a reviewed `config/`
file, or a top-level Axiom JAR. Seeds are copied only into a pristine install;
they are never written into installed updater state, size-checked, repaired,
deleted, or reapplied. Existing settings and existing absence both win. This
lets a new client start with the canonical keybinds, video settings, music UI,
Atmospherics selection, and particle/performance configuration while every
player remains free to change or remove them afterward. Axiom is likewise an
optional player-owned mod: removing, disabling, or replacing any Axiom JAR
does not trigger pack validation or a payload download.

Updater 1.2.7 adds a signed stable updater channel. The friend-facing Prism
command remains checksum-pinned to one immutable bootstrap generation. That
bootstrap retains a separately named, checksum-pinned 1.2.7 verifier and uses
the compiled Ed25519 release key to authenticate the current updater version,
release tag, byte length, and SHA-256 before atomically replacing the runnable
EXE. The unsigned branch pointer and GitHub asset URL provide availability,
not trust. A cached signed descriptor supports offline launch, and a replayed
older descriptor cannot downgrade an already trusted updater.

Updater 1.2.8 adds managed shaderpack delivery and a persistent one-time seed
ledger. New reviewed defaults can now be offered to an existing installation
once: an existing file always wins, a newly missing default is initialized once,
and a later edit or deletion remains player-owned. When upgrading state written
by 1.2.6/1.2.7, the signed base manifest marks its older seeds as already
offered, so the migration cannot reinstall a removed Axiom JAR or resurrect an
intentionally deleted old setting. The updater also recognizes the exact full
state of a current `.mrpack`, including a schema-v2 release, and can adopt it
without downloading an older multi-gigabyte baseline.

Updater 1.2.9 adds narrowly scoped corrective seed offers. A signed schema-v2
manifest may list selected declared `seedFiles` again in `reofferSeedPaths`.
During that release only, a listed file is initialized if and only if it is
missing; an existing player file is never compared or overwritten. This repairs
a packaging mistake that previously recorded a default as offered without
turning mutable settings into managed content or resurrecting unrelated removed
defaults. Exact-baseline adoption is disabled while any corrective target is
missing so adoption cannot silently skip the repair.

Updater 1.2.10 adds exact corrective reconciliation for installs that an older
updater already marked current while leaving known legacy files behind. Signed
cleanup may accept multiple reviewed size/SHA-256 identities for one path, so
official old Packed Packs profiles and unpacked shader generations can be
replaced while any modified player copy is preserved. It also supports a
signed managed-to-seed transition in a delta when the old signed base identity
is listed for exact cleanup and the target seed is re-offered. Top-level Iris
shader option sidecars are therefore player-owned after migration. A separate
text migration is compile-time restricted to the exact `shaderPack=` line in
`config/iris.properties`; it cannot rewrite `options.txt`, keybinds, video,
sound, Sodium, Voxy, or arbitrary mod settings. Corrective reconciliation runs
even when local state already names the target release, and committed seed
refreshes remain recoverable across an interrupted launch.

Updater 1.2.11 adds one deliberately narrow, ledgered exception for the legacy
Contest Tracker `K` collision. A signed manifest may contain only the complete
reviewed pair that unbinds Iris shader toggle and Fancy Toasts while Contest
Tracker is still exactly bound to `K`. The updater inspects that migration once
and commits its stable ID with the surrounding file transaction even when the
player's layout is already custom, missing, duplicated, or otherwise ineligible.
Afterward, `options.txt` is never reconsidered: later keybinds, video, sound,
Sodium, and Voxy choices cannot trigger a repair or be undone. `options.txt`
may not be re-offered in the same release, so an existing player-owned absence
also remains respected.

Updater 1.2.19 fixes fresh-install sourcing. Convergence builds two plans and
downloads the cheaper: per-file cheapest payload, or newest schema-v1 baseline
first. The baseline plan used to take a file the newest baseline lacks from the
newest *older baseline* holding its exact bytes; when 1.0.57 rolled
InventoryParticles back to 3.0.0 (bytes last in the 1.0.6 baseline), every
fresh install downloaded 1.0.55 plus all 4.47 GiB of 1.0.6 (9.40 GiB) for one
1.7 MiB jar. Such a file was re-shipped after the baseline, so it now comes
from the newest release holding its bytes: a fresh 1.0.57 install is 1.0.55 +
1.0.56 + 1.0.57 (4.93 GiB). The chosen plan is written to `updater.log`
(`Sourcing plan: ...`). `CobbleMusicUpdater.Tests --sourcing-regression <dir>`
replays a fresh install against every signed manifest in `<dir>`.

Updater 1.2.20 fixes a launch block introduced with the 1.2.17 runtime-config tolerance: convergence
tolerated a game-rewritten `config/gravels-extended-battles.toml` / `.rpo`, but the pre-commit check still
demanded their signed bytes, so every update after the game had rewritten them rolled back with "Adopted
managed file changed before commit" and exited 3. The commit check now tolerates them exactly as it tolerates
the official Packed Packs profiles.

Updater 1.2.21 closes a gap in convergence: it replayed only the target release's `deletedFiles`, so a player
who skipped a release (for example because that update rolled back) kept whatever that release removed, even
after their recorded version caught up (BetterF3 stayed installed after 1.0.58 removed it). Convergence now
removes any file that some published release deleted or listed in `legacyCleanup` and that the target no
longer ships, but only while it is still byte-identical to the official copy, and it does so even when the
recorded version already matches. 1.2.21 also accepts one more reviewed one-time `options.txt` migration,
`options-ftb-force-complete-c-v1`: FTB Quests 2101.1.36 put "Force-complete Hovered" on C, the key Cobblemon's
Summary uses; exactly that default line becomes unbound, and a manifest carrying it requires updater 1.2.21.

Updater 1.2.22 adds **lite mode** for weak PCs (Kewz, 2026-09-23: a Ryzen 7 2700 + RTX 3050 should run the pack
well with shaders). Everything a lite PC changes is listed in the signed manifest's `performanceProfiles.lite`, so
the lists change per release without an updater release. Lite never changes shaderpack options, Iris settings or the
shader profiles; the updater refuses such targets. The invariants:

- **Decision.** `minecraft/cobble-music-updater/performance-mode.txt` holds `mode=auto|lite|full` and is honoured
  every launch (the updater creates it with `mode=auto` and only ever refreshes its `# This computer:` status line).
  With `auto`, the PC is lite when the processor score is below `detection.cpuScoreThreshold` **or** every real
  display adapter matches `detection.liteGpuPatterns` (a card on `fullGpuPatterns`, or one on neither list, keeps
  graphics neutral). Patterns are whole words, `#` = one digit. The processor score comes from a ~1 s single-thread
  probe (score 1000 = Kewz's i7-10750H) run once per PC on its own thread with a 5 s timeout; the raw numbers are
  kept machine-wide in `%LOCALAPPDATA%\CobbleMusicUpdater\performance-detection.json` and re-measured only for a new
  processor. A failed, timed-out or disturbed run (typical work step more than 90% slower than the fastest, e.g.
  Minecraft already running) never votes lite and is retried on the next launch, at most three times. Adapter names
  come from the display-class registry key (no WMI). Nothing readable = full. One log line states processor, score,
  adapters, verdict and reason.
- **Mods** listed in `disabledMods` (exact managed top-level jars) are renamed to `<jar>.disabled`, the Prism
  convention Fabric Loader ignores. Convergence counts an exact `.disabled` copy as installed (lite-listed mod while
  lite, or one this instance's ledger says lite switched off), so it is never downloaded again, and full renames it
  back byte-exact. A player's own `.disabled` copies are never deleted; a mod the player turns back on by hand stays
  on. A release that updates a lite mod delivers the new jar and lite switches it off again; a release that retires
  one also removes lite's disabled copy.
- **Resource packs** in `removedPackIds` are taken out of the two official Packed Packs profiles once per signed
  profile revision (the profile's own formatting is kept). Full writes back the saved original bytes, or re-inserts
  the ids at their old positions when Packed Packs re-serialized the file.
- **Settings** in `settings` are edited once, one value each, only in a compiled allow-list of player-owned files
  (`options.txt` key:value; Sodium, Sodium Extra, Voxy, voxy-server-side, AS-Outline, AsyncParticles and Gnetum JSON
  by dotted key path to a scalar; Xaero world map / minimap `cobbleverse.cfg` key = value). The key must occur exactly
  once; the previous value is recorded; every other byte (order, spacing, comments, CRLF, BOM) is kept. A value the
  player changes afterwards is never overwritten, and full puts back the recorded value only while the lite value is
  still there. A missing file or key is skipped (retried on later launches), never created.
- **Transactions.** Each switch is one journaled transaction after normal convergence, recorded in
  `minecraft/cobble-music-updater/performance-state.json` (journal field `performanceOutcomes`). A failure rolls it
  back and is logged; Minecraft still starts with the pack as it was. A release without a lite list, or `mode=full`,
  undoes everything in the ledger. FULL PCs with an empty ledger are not touched at all.
- **Compatibility.** A manifest with `performanceProfiles` must require updater 1.2.22 (parser and publisher).
  Proven with the unchanged 1.2.21 source: it ignores the unknown section when the floor is 1.2.21 (full pack) and
  rejects the release ("requires updater 1.2.22") when the floor is 1.2.22, so the stable channel must serve 1.2.22
  before such a release is published (the publisher refuses otherwise).

Network trouble, GitHub rate limiting, a missing release, or invalid remote
content leaves the last known-good local pack unchanged and lets Prism launch.
A run that ends **Blocked** (local recovery needs attention, an integrity or
apply failure, an unfinished install another process still holds, or
`allowOfflineLaunch: false` without GitHub) fails the launch. The pinned
friend bootstrap ignores the updater's exit code, so since 1.2.18 the updater
itself ends its verified pre-launch `powershell.exe` (parent chain
powershell -> prismlauncher, checked with creation-time guards) with exit code
3; Prism then fails the pre-launch step and does not start Minecraft. Under the
legacy direct command the updater's own exit code 1 does the same.

### One updater per instance (lock v2, 1.2.18)

`update.lock` keeps the 1.2.17 file and `FileShare.None`, so old and new
builds still exclude each other. Only a sharing violation counts as busy
(10 attempts, 250 ms apart); other I/O errors are real errors. The holder
writes `update.lock.owner.json` (pid, start time, exe, Prism chain, phase,
2 s heartbeat, progress) and listens on
`Local\CobbleMusicUpdater.Stop.<identity hash>`. A later launch classifies the
holder from OS facts only: an orphan (its Prism launch is gone) is taken over
after a 3 s notice, or after up to 5 minutes when it is installing files; a
live holder is waited for with a countdown and only a person can choose
**Stop it and continue** (after 10 minutes, or when it stops responding); an
unidentified holder is never touched and the launch continues after 30 s only
when no install journal exists. A takeover signals the stop event, waits 15 s,
re-validates pid, start time and image (this instance's
`cobble-music-updater\CobbleMusicUpdater.exe` only), terminates, reacquires,
and recovers any journal before doing anything else.

## What players see at launch

When Prism starts the updater normally, a small centered **Kewz's Cobblemon**
card appears immediately with **Checking for updates…**. It has no Windows
title bar and uses a custom animated progress indicator while GitHub is being
checked. If a signed pack update is available, the same card shows aggregate
download percentage, downloaded/total size, smoothed live speed, and ETA
across every payload part and release step, followed by the file-install count.
Already cached resume bytes advance completion without inflating the speed or
ETA estimate.

On an ordinary no-update, offline-fallback, or successful-update launch, it
briefly shows the result and closes automatically before Minecraft starts. A
Blocked result stops the Prism launch (see above), stays visible with a
**Close (20)** countdown, and closes itself after 20 seconds.

The **X** is safe at any time: while checking or downloading it cancels the
run and closes within 5 seconds (downloads resume by Range next time); while
files are being installed or recovered it rolls the install back first
("Finishing safely"), and a second X asks before a hard exit, which also stops
the Prism launch.

The GUI build also writes its own rotating diagnostic log at
`minecraft/cobble-music-updater/updater.log`; it never writes to Minecraft's
normal `logs/` directory. The blocked state tells the player to check that
file when more detail is needed.

The `--no-ui` switch is only for diagnostics and automated tests; do not put
it in Prism's normal pre-launch command.

Normal source or updater-binary releases can live in this repository without
interfering with clients. They are ignored unless they use the reserved,
signed `modpack-v<version>` release format.

### Network limits and the release-metadata cache (updater 1.2.18)

- **Idle timeouts.** Every response body read is bounded by an inactivity
  limit that resets after each successful read: 30 s for release metadata,
  60 s for payload parts. A download that keeps moving is never cut off.
- **Retries.** Transport errors, stalls, HTTP 5xx and 429 get three attempts
  in total with jittered 2 s / 5 s backoff. `Retry-After` is honoured up to
  60 s; a longer wait fails at once. Payload parts resume from the kept
  partial file with a validated `Range` request. Each retry is logged and
  the card shows "Connection stalled — retrying…" (or similar).
- **Check budget.** The release check (release list, asset lists, manifests)
  has 90 s in total. On expiry the updater takes the normal offline fallback.
- **What counts as a network failure once retries are spent** (1.2.18
  integration): `HttpRequestException` (including rate limits), timeouts
  (idle, budget, header), `HttpIOException` (body ended before its
  Content-Length), an `IOException` caused by a socket reset, and a body that
  ended cleanly below its signed size. During the check that is the offline
  fallback; after the check it launches the current pack only when offline
  launch is allowed and no install journal exists. Any other I/O error (disk
  full, a locked file) stays Blocked. The X and a later launch's stop event
  always end as a cancellation, never as an offline fallback.
- **Verified metadata cache.** Each release's signed manifest and signature
  are kept in `%LOCALAPPDATA%\CobbleMusicUpdater\<instance>\cache\releases\<release id>.json`,
  keyed by release id, tag and the manifest/signature asset id, name, size and
  `updated_at` (plus GitHub's sha256 `digest` when listed). Cached bytes are
  re-verified with the compiled Ed25519 key, re-parsed and re-validated on
  every launch exactly like downloaded bytes; an entry that fails is deleted
  and downloaded again, and entries for releases GitHub no longer lists are
  removed. The nested `assets` of the release list are used instead of one
  `/releases/{id}/assets` call per release; the paginated endpoint is used
  only when the nested list lacks the manifest, signature or a signed part.
  Steady state: **one api.github.com call and no manifest downloads** per
  launch.
- **Skipping unreachable old releases.** A release that cannot be reached
  (network only, never a verification failure) is skipped when it is older
  than the installed version and the installed state has its offered-defaults
  ledger. The newest release, the installed release, anything newer, every
  release on a fresh install, and every release for a legacy state without
  the ledger still fail the check.
- **Diagnostics.** `updater.log` records each API response's HTTP status and
  `X-RateLimit-Remaining`/`X-RateLimit-Reset`. On a GitHub rate limit (403
  with no quota left, 403 with `Retry-After`, or 429) the card says "GitHub is
  limiting update checks from this network until HH:MM — starting your
  current pack."



## Recommended Java memory settings (1.2.18)

After a successful (or offline-fallback) pre-launch run, the updater checks the
instance's Java settings against policy version 1, which is compiled into the
exe (`updater/CobbleMusicUpdater/JvmSettings/JvmPolicy.cs`): a heap floor by
physical RAM (below 11 GiB nothing changes; 7168 / 8192 / 10000 / 12288 MB for
the 16 / 24 / 32 / 48+ GB classes, Xms `min(4096, Xmx/2)`) and five JVM
arguments (`-XX:+UseG1GC -XX:MaxGCPauseMillis=50 -XX:G1ReservePercent=15
-XX:+UseStringDeduplication` and a rotated `logs/gc.log`). It acts only when
something is missing; the player's own value always wins for every key, any
player-chosen collector suppresses the whole GC group, and a higher player heap
is never lowered.

Prism rewrites `instance.cfg` from memory whenever a launch starts or a game
starts or stops, so the file can only be edited while `prismlauncher.exe` is not
running. When something is missing and every guard passes (verified
`prismlauncher -> [powershell ->] updater` chain, Prism 10 or 11, nothing else
running under Prism, the INST_JAVA_ARGS cross-check, at most 2 attempts per
policy version and none in the last 10 minutes), the updater starts a helper
copy of itself (`--apply-jvm-settings <plan.json>`), stops the current launch
with exit code 3 ("Pre-Launch command failed with code 3." in Prism), and the
helper closes Prism (WM_CLOSE first; TerminateProcess only with no children and
no `.cfg.lock`), edits `instance.cfg` atomically in Prism's own INI format (only
`OverrideMemory`, `MinMemAlloc`, `MaxMemAlloc`, `OverrideJavaArgs`, `JvmArgs`;
backup `instance.cfg.cobble-music-jvm-<stamp>.bak`, last 3 kept) and reopens
Prism with `--launch <instance id>` through Explorer. If a game from another
instance is running, nothing is closed: the edit waits until Prism exits.
State lives in `%LOCALAPPDATA%\CobbleMusicUpdater\<instance-hash>\jvm-settings.json`.
A player opts out by creating
`minecraft/cobble-music-updater/jvm-settings.optout`. The game folder's `logs/`
directory is created on every pre-launch run because the gc log flag is fatal
without it. Design and proofs: `workspace/shared/updater0923/jvm-args/FINDINGS.md`
in the handoff hub. The fake-Prism integration harness is
`updater/JvmSettings.FakePrism/Run-FakePrismJvmIntegration.ps1` (never point it
at a real Prism folder).

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
three-part versions such as `1.2.3` (no leading zeroes or fourth component).
A new stable updater release must be strictly newer than every existing stable
`updater-v*` release; exact reruns of their own draft or published release stay
idempotent, while downgrades and alternate spellings of the same numeric
version are rejected.

The publisher resolves Git only through its own repository root, captures one
clean commit, exports the complete tree without reading live working-tree
bytes, and uses the builder, source, tests, bootstrap, SDK pin, package lock,
and NuGet configuration from that export. It builds self-contained `win-x64`,
computes the EXE's SHA-256, pins the bootstrap only when staging its immutable
verifier generation, and proves that the staged EXE is byte-identical to
independent builds of the same commit. It also creates the canonical
`updater/channel/stable.json`, signs those exact bytes with the offline release
key, and verifies the descriptor through the staged EXE. The eventual Git tag
and release target that exact commit.

First stage locally. This runs every `tests/Test-*.ps1` plus every updater
`*.Tests.csproj`, atomically refreshes the tracked bootstrap (when applicable)
and signed stable channel only after they pass, and makes no GitHub change.
The private key is read only from the maintainer-selected offline path and is
never copied into staging. Console-style test projects are executed
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
GitHub authentication comes only from the GitHub CLI credential store; no
access token is accepted or emitted. GitHub mutation modes validate the
already committed public channel/signature and do not need the private seed.

### Pre-staging a new updater (1.2.18 and later)

Without pre-staging, every updater release makes each friend's pinned
bootstrap download the new executable with Windows PowerShell 5.1 before any
window appears (a hidden, unbounded download of tens of megabytes, which is
also what floods Prism's log view). From 1.2.18 on, a running updater can
fetch the next updater itself, so friends' bootstraps never have to:

1. Stage and commit as usual, but push the release commit to a **branch**,
   not `main` (`-UploadDraft` only needs the commit to exist on GitHub).
   `main`'s `updater/channel/stable.json` must keep naming the current
   updater for now.
2. `-UploadDraft`, then `-Publish -ConfirmPublish` as above. Publishing the
   GitHub release changes nothing for friends; `stable.json` on `main` does.
3. On `main`, stage the next channel and commit only those two files:

   ```powershell
   .\tools\Publish-CobbleMusicUpdater.ps1 -StageNextChannel -NextVersion <version>
   git add updater/channel/next.json updater/channel/next.sig
   ```

   This standalone, read-only mode needs no private key. It takes the exact
   signed `stable.json`/`stable.sig` from the commit that the
   `updater-v<version>` tag names, requires them to be byte-identical to what
   this publisher writes for that version, verifies them with the bootstrap's
   pinned 1.2.7 verifier, confirms the published release carries exactly the
   signed `CobbleMusicUpdater.exe` (GitHub digest and an anonymous download),
   and refuses a version that is not newer than `main`'s stable channel or
   older than an already staged next channel.
4. Push `main`. Over the next launches, running 1.2.18+ updaters download
   the new updater in the background: HttpClient, 30-second inactivity
   timeout per read, three retries with backoff, resume by `Range`, and at
   most 15 seconds added after a successful update run, with the rest
   resuming next launch. Each accepts the file only when it is exactly the
   signed size and SHA-256 and starts with `MZ`, which is the bootstrap's
   `Test-ExactExecutable`. The card's X (or a later launch's stop event)
   ends that 15-second wait at once and starts no swap helper: a partial
   download resumes and a finished one stays staged for the next launch.
5. Later, advance stable by merging the release commit into `main`.
   `stable.json` then carries the same bytes as `next.json`. A bootstrap that
   already has the pre-staged updater sees an equal version with equal size and
   hash, so it downloads nothing. Everyone else gets the normal bootstrap
   download.

Never let `stable.json` name a pre-staged version with different executable
bytes. The pinned bootstrap refuses a reused version with different metadata
and keeps running the pre-staged exe, so those friends would stay on it.

How the swap is proven to satisfy the pinned bootstrap (`tests/Test-UpdaterPrestageBootstrap.ps1`):

- **Why a helper process.** A running .NET single-file executable cannot be
  renamed. After a rename, every later assembly load in that process fails
  (measured for plain and compressed bundles). So the updater only downloads.
  The swap is done by `prestage\swap-helper-*.exe`, a hard link to the
  current updater that runs after the updater exits.
- **Swap order.** The helper first keeps rollback copies. It then writes
  `installed-updater-channel.json` and then `.sig` with the exact signed
  `next` bytes, and finally renames the verified staged exe over
  `CobbleMusicUpdater.exe`. Each of those three is one atomic rename, and the
  helper re-verifies everything first.
- **What the bootstrap does next.** Its `Get-CachedChannel` accepts the pair
  (verified by the pinned verifier, exe matching size and SHA-256). A
  `stable.json` still naming the older updater is then "Ignoring replayed
  updater channel", and offline it keeps the cached pair. In none of these
  cases does it download or fall back.
- **Only when the bootstrap started it.** The updater pre-stages only in a
  Prism pre-launch started by Prism's PowerShell, running from the
  bootstrap's `<instance>\minecraft\cobble-music-updater\CobbleMusicUpdater.exe`,
  with the pinned verifier beside it, and when the cached channel verifies
  and names the running exe.
- **Crash between writes, online.** If the helper dies between two renames
  and the next launch is online, the bootstrap re-caches `stable.json` for the
  old exe, runs it with no download, and the swap is retried.
- **Crash between writes, offline.** If the helper dies after the cache
  rename but before the exe rename, and the next launch is offline, that one
  launch reaches the bootstrap's pinned-verifier fallback. That window can't be
  closed because the bootstrap reads two files that can't be replaced together.
  The next 1.2.18+ updater run repairs the pair from the signed descriptors
  it kept, and removes leftover helper and rollback files.

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

A player who has neither the updater EXE nor a pre-launch hook needs only one
setup action: paste the release-generated command into that instance's
**Settings → Custom Commands → Pre-launch Command** field and enable
**Override Global Settings**. They can immediately press Play. The permanent
command is a single `powershell.exe -EncodedCommand ...` invocation so Prism's
direct process launcher preserves every argument even when the instance path
contains spaces or apostrophes.

On the first Play, that command creates `minecraft/cobble-music-updater/`,
downloads the exact release bootstrap, verifies its pinned SHA-256, installs
the checksum-pinned verifier, resolves the signed stable updater channel, and
runs the verified updater before Minecraft starts. Later Plays reuse the
immutable bootstrap and verifier, check the small signed channel documents,
and download an updater EXE only when its authenticated version advances or
the installed copy needs repair. Offline checks retain the last verified EXE.

The permanent command deliberately leaves `instance.cfg` alone while Prism is
running. The bootstrap's `-PrismPreLaunch` mode writes only its own
`minecraft/cobble-music-updater/updater.json`, then waits for the updater to
finish. This avoids racing Prism's in-memory settings while still making the
very first Play perform the pack update.

### Existing 1.0.5 players

Release 1.0.5 installed updater 1.2.3 and a short pre-launch command that runs
the local EXE directly. That updater predates the signed stable updater channel
and cannot replace itself. A 1.0.5 player must therefore replace the entire old
`"$INST_MC_DIR/cobble-music-updater/CobbleMusicUpdater.exe" ...` entry with the
current release-generated `powershell.exe ... -EncodedCommand ...` line once.

On the next Play, the permanent command verifies and installs updater 1.2.7,
then applies the signed 1.0.6 baseline. The player does not need to delete the
old updater, reinstall the Prism instance, or change the command again for
ordinary future updater releases.

Generate the exact friend-facing command only after the updater release
bootstrap has its final checksum:

```powershell
.\tools\New-CobbleMusicPrismBootstrapCommand.ps1 `
  -BootstrapVersion 1.2.7 `
  -ExpectedBootstrapSha256 '<SHA-256 of the published Bootstrap-CobbleMusicUpdater.ps1>'
```

Copy the one output line verbatim. Friends do not run this generator and do
not need a terminal, script download, instance-path substitution, or any
second setup step; they paste its output directly into Prism's Pre-launch
Command field.

The separately published bootstrap also retains a manual installer mode for
an owner who wants to close Prism and replace the field with the short direct
EXE command. In manual mode it:

1. verifies the selected Prism instance has `instance.cfg` and `minecraft/`;
2. downloads the exact `updater-v1.2.7` verifier and checks its SHA-256;
3. installs it at `minecraft/cobble-music-updater/CobbleMusicUpdater.exe`;
4. writes only the updater's own `updater.json` (backing up an existing one);
   and
5. backs up `instance.cfg` and adds the instance-specific Prism pre-launch
   command above.

The bootstrap refuses to replace a different existing Prism pre-launch command
unless the player deliberately reruns it with `-Force`. It does not touch
saves, `options.txt`, `servers.dat`, logs, or resource-pack selections.

For that optional manual path, after the bootstrap asset has been downloaded from
[updater-v1.2.7](https://github.com/Kewz4/Cobble-Music/releases/tag/updater-v1.2.7),
the player can paste this into PowerShell, replacing the example instance path:

```powershell
$uri = 'https://github.com/Kewz4/Cobble-Music/releases/download/updater-v1.2.7/Bootstrap-CobbleMusicUpdater.ps1'
$path = Join-Path $env:TEMP 'Bootstrap-CobbleMusicUpdater.ps1'
$expected = '56E39784A1470E5BF2923820AB9D96D6E88474FD679540B85C2B65387CD4301A'
Invoke-WebRequest -Uri $uri -OutFile $path
if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw 'Bootstrap checksum mismatch.' }
Unblock-File -LiteralPath $path
& $path -InstanceDirectory 'C:\Program Files\Prism Launcher\instances\<your instance name>'
```

This optional manual form intentionally is **not** an `irm | iex` command. The
script is downloaded, verified by a pinned checksum, and only then run.

### Lite mode lists (updater 1.2.22)

Pass the lite profile with `-PerformanceProfileManifest <file.json>`; the file holds the `performanceProfiles`
object:

```json
{ "lite": {
    "revisionId": "lite-1.0.61-v1",
    "detection": { "cpuScoreThreshold": 1050, "fullGpuPatterns": ["RTX 3060"], "liteGpuPatterns": ["RTX 3050"] },
    "disabledMods": ["mods/<exact managed jar>.jar"],
    "removedPackIds": ["file/<pack>.zip"],
    "settings": [ { "path": "config/sodium-options.json", "format": "json", "key": "quality.leaves_quality", "value": "\"FAST\"" } ] } }
```

The Core module validates it (`ConvertTo-CobblePerformanceProfiles`, the same rules as the updater's
`PerformanceProfilePolicy`), embeds it, and raises `minimumUpdaterVersion` to 1.2.22. The publisher refuses to stage
when the signed stable channel serves an older updater than the release requires, and refuses a source instance that
is itself in lite mode (a `performance-state.json` with content or any `mods/*.jar.disabled`), so a lite PC's
disabled jars can never become the pack. Before publishing, `CobbleMusicUpdater.Tests --check-performance-profile
<signed manifest> <profile.json> <seed dir> <profile dir> <preview dir>` runs the file through the updater's own
validation, prints every setting's current value, and writes the filtered Packed Packs profiles a lite PC will get.

## Publish a pack update

The canonical source is the current live client, not Claude’s stale `1.0.3`
`.mrpack` or its retained `mrpack\` folder. `-SourceMinecraftDir` is mandatory
when staging and has no default, so the operator must name the intended
instance explicitly. The publisher also requires an explicit mode so
accidentally omitting a base version cannot upload the entire pack.
Release and base versions use exactly `major.minor.patch` with no leading
zeroes (for example, `1.0.5`); four-component variants are rejected.

The modpack publisher never builds or loads the updater from the current C#
working tree. Signing and verification use only
`updater\dist\win-x64\CobbleMusicUpdater.exe`. A separate immutable local
verifier must match the committed 1.2.7 bootstrap pin; it authenticates the
public stable channel, whose signed version, byte length, and SHA-256 must then
match the distributed EXE. The EXE is held read-locked from checksum
verification through each signer or verifier process. Keep the private signing
key outside the Minecraft source, every managed source root, and
`release-output`; the publisher rejects those locations before it inventories
a single source file.

Release `1.0.5` is a sanitized recovery baseline: it replaces the unsafe 1.0.4
inventory, excludes generated browser/cache/index data, and performs the
reviewed exact-hash legacy cleanup. Build it explicitly from the canonical
client:

```powershell
$source = 'C:\Program Files\Prism Launcher\instances\<canonical instance>\minecraft'
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.5 -FullBaseline `
  -SourceMinecraftDir $source `
  -LegacyCleanupManifest .\release-manifests\legacy-through-1.0.4-cleanup.json
```

Normal later releases are signed schema-v2 deltas. They download the prior release's
manifest and detached signature, verify the signature with the updater's
compiled-in public key, require the requested base/version binding, and compare
that complete signed file set with the canonical live client. The source
updater state must itself match the exact signed base version/hash/inventory:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.6 -BaseVersion 1.0.5 `
  -SourceMinecraftDir $source
```

Updater 1.2.10 can move a path from managed `files` into player-owned
`seedFiles` in a schema-v2 delta. The publisher requires the exact signed base
identity in `legacyCleanup` and requires the path in `reofferSeedPaths`. An
unchanged official file is refreshed as the new seed, while a locally modified
copy survives and simply becomes player-owned. Older updater generations used
a full schema-v1 baseline for this transition. For example, the 1.0.6
settings/Axiom ownership migration was staged with:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.6 -FullBaseline `
  -SourceMinecraftDir $source
```

After that baseline established the original ownership model, later releases
returned to schema-v2 deltas.

A corrective delta may safely restore particular missing defaults without
overwriting existing player copies:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.10 -BaseVersion 1.0.9 `
  -SourceMinecraftDir $source `
  -ReofferSeedFiles @(
    'config/packed_packs/profiles/resourcepacks/Default.profile.json',
    'config/packed_packs/profiles/resourcepacks/Realistic.profile.json'
  )
```

Every re-offered path must also exist in the signed `seedFiles` inventory, must
pass the ordinary seed allowlist, and requires updater 1.2.9 or newer. The
feature is deliberately unavailable to schema-v1 baselines.

To use reviewed local copies of the base assets, supply both paths:

```powershell
.\tools\Publish-CobbleMusicRelease.ps1 -Version 1.0.6 -BaseVersion 1.0.5 `
  -SourceMinecraftDir $source `
  -BaseManifestPath .\reviewed-base\cobble-music-update.json `
  -BaseSignaturePath .\reviewed-base\cobble-music-update.sig
```

Local copies do not bypass GitHub identity checks. The publisher still locates
the currently published, non-draft `modpack-v1.0.5` release through GitHub's
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
Only approved top-level mod/resource-pack artifacts are inventoried. Nested
`mods`/`resourcepacks` runtime directories, MCEF cache/libraries, generated
`.index` data, browser tab state, backup files, and VCS/workspace metadata are
never distributable. Reviewed `.rpo` sidecars appear only as explicit source
file entries, and the final authoritative manifest is checked again before
signing.
The staging/resume validators mirror updater 1.2's schema rules: a v1 baseline
must declare a canonical supported `minimumUpdaterVersion` and cannot carry
delta-only fields, while v2 cannot use path-only `deletePaths`. Truly absent
legacy collection properties receive the runtime model's empty-list defaults;
an explicit JSON `null` for any required collection is different and is
rejected exactly as the distributed updater rejects it.

The publisher inventories immutable `files` separately from create-only
`seedFiles`. `options.txt` and the reviewed safe `config/` tree are seeds, so
vanilla keybind/video/sound settings plus FancyMenu, Iris, Sodium, Voxy,
particle/fog, camera, zoom, Atmospherics, and mod defaults can reach a player
without becoming integrity-enforced afterward. Reviewed files under
`release-defaults/` sanitize mutable state, such as removing per-player Reactive
Music home coordinates and comments/timestamps from the initial Iris choice.
PackedPacks configuration and the `Default`/`Realistic` profiles also come from
reviewed templates; its generated `config/packed_packs/__version.json` is never
seeded into a release. Generated caches, numbered backups, ETF warning state,
Sodium's machine fingerprint, Spark runtime files, Jade's username cache,
Cobbreeding's generated encryption key, Zoomify residue, MCBrowser tab state,
and both known DreamDisplays service-config names are excluded from automatic
and explicit source collection. Only the music bridge, pack-version marker,
Log Begone policy, and resource-pack policy remain managed configs. Staging
also requires that the canonical DEV pack-version marker exactly match the
requested target version.
Shaderpack ZIPs and folders are managed pack content and therefore receive
normal signed updates and repairs. Top-level Iris `.txt` option sidecars are
create-only player settings, so later shader-option edits do not trigger a
repair or download. Every current top-level Axiom JAR is automatically removed
from the immutable inventory and added to the create-only inventory. Historical
seed releases require updater 1.2.6 or newer; releases using the one-time
migration ledger require updater 1.2.8, corrective re-offers require updater
1.2.9, and exact seed refreshes, managed-to-seed transitions, or Iris selector
migrations require updater 1.2.10. The one-time conditional `options.txt`
migration requires updater 1.2.11. The payload ZIP is checked against the
exact union of its managed payload files and seed files before signing.

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
on the currently published, non-prerelease `updater-v1.2.7` release. It checks
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

Give friends only the release-generated permanent command and approved setup
instructions—never the private signing seed. Their Prism instance must be
writable; a user-writable Prism location is easiest.

The updater cannot replace its own running EXE, so the immutable bootstrap runs
first and owns updater-binary upgrades. For an ordinary updater release,
publish the newer immutable updater asset and atomically advance the signed
stable channel descriptor and signature. Existing permanent commands fetch
that channel on Play, authenticate its version, release tag, byte length, and
SHA-256 with the pinned verifier, then atomically replace the runnable EXE
before starting it. Players do not change their Prism command.

A signing-key or verifier trust-root rotation, or an incompatible stable-channel
protocol change, is a deliberate migration and may require a new permanent
command. If channel retrieval or verification fails, clients retain and run
their last verified compatible updater instead of adopting untrusted bytes.

## Claude workflow

Claude may prepare a sanitized `HANDOFF.md` or a PR with source changes, but
it never directly controls player machines or GitHub releases. After review,
ask Codex to audit, build, stage, and publish a version. Only a signed
`modpack-v*` GitHub Release becomes an update that friends can receive.
