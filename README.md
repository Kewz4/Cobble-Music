# Cobble Music

Curated music, compatibility tooling, and a Windows updater for the Cobblemon
client pack.

The repository intentionally contains source code, manifests, and publishing
tools—not third-party music, mod JARs, Prism instance exports, player saves,
Claude session exports, or credentials.

## Updater

`updater/CobbleMusicUpdater` builds a self-contained Windows executable. Prism
Launcher runs it as an instance-specific pre-launch command. It finds the
highest stable signed `modpack-v*` GitHub Release, downloads only declared update chunks,
verifies every SHA-256 hash, and applies only approved paths under the Minecraft
folder. It never edits saves, screenshots, logs, `options.txt`, or arbitrary
paths named by a remote release.

Normal source or updater-binary releases may coexist in this repository: they
are ignored unless they use the reserved signed `modpack-v*` release format.

See [the updater guide](docs/UPDATER.md) for setup and publishing. See
[the Claude handoff prompt](docs/CLAUDE_HANDOFF_PROMPT.md) before asking Claude
to contribute a build.
