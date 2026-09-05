# Kewz Client Render Guard + one-time NEC migration

Final artifact: **8616 bytes**, SHA256 **07ab5272f6f39126a3c6d0bd1ba241f0dcf7294e4e26f94a01a6d5bc7522f5c7**. This supersedes the earlier render-only draft. Final verification: **40 headless assertions PASS**, target/mixin static checks PASS, two final repeated builds byte-identical.

Staged only. Install `dist/kewz-render-guard-1.0.0+mc1.21.1.jar` on Fabric 1.21.1 clients, never the server. No DEV, existing config, repository, or server file was changed, and no game/server was launched. Parent owns client installation and NEC seed config.

## Bounded render changes

Exactly two client-only cancellable HEAD injections, each `require=1`, `expect=1`, `allow=1`. Both cancel only if the owning client's player OR world is null; valid world/player frames run unchanged. No catch-all, GL calls, player/world changes, general feature switch, or recurring config rewrite.

Verified against cached Minecraft 1.21.1 bytecode and Yarn 1.21.1+build.3:

| Named target | Exact runtime intermediary target | Owner client field |
|---|---|---|
| GameRenderer.renderWorld(RenderTickCounter) | class_757.method_3188(Lnet/minecraft/class_9779;)V | field_4015 |
| LightmapTextureManager.update(float) | class_765.method_3313(F)V | field_4137 |

MinecraftClient player/world are field_1724/field_1687. Source deliberately uses intermediary names with remap=false; this is a pinned production Fabric 1.21.1 artifact, not a named-development-environment build.

Frame boundaries: the world HEAD guard runs before the lightmap call, camera work and internal profiler push. It does not cancel the enclosing GameRenderer.render or its caller-owned cleanup/UI flow. The lightmap HEAD guard runs before clearing its dirty flag or pushing lightTex profiler state, so it leaves a skipped update pending. A getDarknessFactor-only fallback would allow later player dereferences to continue; no such patch is added. This is a defensive guard for the observed invalid state, not attribution of the original mod/root cause. Other rendering hooks or state becoming invalid after HEAD are outside this bounded guard; runtime integration remains untested.

## One-time NEC migration

Fabric client entrypoint runs after main mod initialization. If NEC is absent, no-op. With NEC present, reads only `config/notenoughcrashes.json`; requires strict valid UTF-8 JSON object with an existing boolean `catchGameloop`. Missing/malformed/unexpected config warns and is not repaired. On first success:

1. Save exact original bytes to `config/kewz-render-guard/notenoughcrashes.pre-catch-gameloop-v1.json` using CREATE_NEW.
2. Recheck original bytes against the current file to refuse concurrent edits.
3. If true, change only `catchGameloop` to false, preserving all other JSON values, and atomically replace through a same-directory temporary file. Already-false configs keep byte-exact formatting.
4. Reflectively set public static boolean `fudge.notenoughcrashes.config.NecMidnightConfig.catchGameloop=false`. Verified in actual installed NEC 4.4.9; NecConfig.getCurrent() reads this field. There is no compile-time NEC dependency.
5. Write `config/kewz-render-guard/nec-catch-gameloop-v1.done`, containing original/result SHA256 values.

If the marker exists, neither disk nor in-memory settings are changed on future launches. Later intentional edits therefore win. A backup without a completion marker is an ambiguous interrupted attempt: warn and fail closed without overwriting config/backup. Atomic replacement or reflection failure warns, preserves backup, and never throws through the client initializer. No updater binary change is needed. Only successful first migration synchronizes policy; this is not a permanent recovery gate.

## Build and verification

Run `./build.ps1` or `./verify.ps1` with Java 21 on PATH. Uses read-only existing Gradle-cache Minecraft intermediary, Fabric Loader, Mixin 0.8.7 and Gson 2.10.1 inputs. Paths are parameters in build.ps1. No network, Gradle daemon/cache mutation, bundled dependencies, or game launch. Output paths are confined to this staging directory. MIT license and all source included.

Headless tests cover nested unknown-key preservation, exact backup, marker creation, real reflection adapter against a matching NEC field fixture, later intentional edits (disk and memory), NEC absent, missing config, invalid JSON/type/UTF-8, already-false formatting, interrupted-backup fail-closed behavior. Tests load no Minecraft classes and the NEC fixture is not included in the artifact. Static javap checks verify exact target signatures/shadows, CLASS-retained @Mixin, HEAD/cancellable settings, two null-branch conditions, and no field writes or exception catches in render callbacks. Repeated build hashes must match.

Limitations: no live Mixin application, gameplay, shader/Iris, transition-frame or real NEC-loaded-client test was run. Those require the separately authorized client acceptance pass. This does not repair Noivern resources, identify the producer of null state, or make NEC recursive recovery safe if a user later deliberately re-enables it.
