using CobbleMusicUpdater;

// Updater 1.2.21 (2026-09-23). Two fixes found from a friend's 1.0.59 crash report:
// 1. BetterF3 was still installed: convergence replayed only the TARGET's deletions, so a player who skipped 1.0.58 (their
//    1.0.58 update had rolled back) kept everything 1.0.58 removed, even after their recorded version reached 1.0.59.
// 2. FTB Quests 2101.1.36 added "Force-complete Hovered" on C, colliding with Cobblemon's Summary key.
internal static partial class Program
{
    private static async Task TestCatalogRetiredOfficialFilesAsync(string root, ConvergenceTestSigner signer)
    {
        UpdaterPaths paths = Paths(root);
        var configuration = new UpdaterConfiguration { AllowedRoots = ["mods", "config", "resourcepacks"] };
        const string keepPath = "mods/kept.jar";
        const string retiredPath = "mods/BetterF3-11.0.3-Fabric-1.21.1.jar";
        const string legacyPath = "mods/legacy-tool.jar";
        const string playerPath = "mods/player-edited.jar";
        byte[] keep = ConvergenceFabricJar("kept_mod", "1.0.0");
        byte[] retired = ConvergenceFabricJar("betterf3", "11.0.3");
        byte[] legacy = ConvergenceFabricJar("legacy_tool", "1.0.0");
        byte[] playerOfficial = ConvergenceFabricJar("player_edited", "1.0.0");
        byte[] playerOwnCopy = ConvergenceFabricJar("player_edited", "1.0.0-mine");
        const string addedPath = "mods/added-in-108.jar";
        byte[] added = ConvergenceFabricJar("added_mod", "1.0.0");

        RemoteRelease first = await CreateSignedConvergenceReleaseAsync(paths, new UpdateManifest
        {
            SchemaVersion = 1, Version = "1.0.7",
            Files = [ConvergenceFile(keepPath, keep), ConvergenceFile(retiredPath, retired), ConvergenceFile(legacyPath, legacy),
                ConvergenceFile(playerPath, playerOfficial)]
        }, new() { [keepPath] = keep, [retiredPath] = retired, [legacyPath] = legacy, [playerPath] = playerOfficial }, signer, configuration);
        // The release a player skipped: it deletes BetterF3 (deletedFiles) and the player-edited jar.
        RemoteRelease skipped = await CreateSignedConvergenceReleaseAsync(paths, new UpdateManifest
        {
            SchemaVersion = 2, Version = "1.0.8",
            Base = new ManifestBase { Version = first.Manifest.Version, ManifestSha256 = first.ManifestSha256 },
            Files = [ConvergenceFile(keepPath, keep), ConvergenceFile(legacyPath, legacy), ConvergenceFile(addedPath, added)],
            PayloadFiles = [ConvergenceFile(addedPath, added)],
            DeletedFiles = [ConvergenceFile(retiredPath, retired), ConvergenceFile(playerPath, playerOfficial)]
        }, new() { [addedPath] = added }, signer, configuration);
        // A later release retires another file through legacyCleanup only.
        RemoteRelease latest = await CreateSignedConvergenceReleaseAsync(paths, new UpdateManifest
        {
            SchemaVersion = 1, Version = "1.0.9",
            Files = [ConvergenceFile(keepPath, keep), ConvergenceFile(addedPath, added)],
            LegacyCleanup = [new LegacyCleanupFile { Path = legacyPath, Size = legacy.LongLength, Sha256 = HashBytes(legacy) }]
        }, new() { [keepPath] = keep, [addedPath] = added }, signer, configuration);

        // The friend's shape: recorded version already = latest, yet the skipped release's removals are still on disk.
        await WriteConvergenceFileAsync(paths, keepPath, keep);
        await WriteConvergenceFileAsync(paths, addedPath, added);
        await WriteConvergenceFileAsync(paths, retiredPath, retired);
        await WriteConvergenceFileAsync(paths, playerPath, playerOwnCopy);
        await LocalStateStore.SaveStateAsync(paths, StateFrom(latest.Manifest, latest.ManifestSha256), NoCancellation);

        var logs = new List<string>();
        await new UpdateEngine(paths, configuration, logs.Add, verifiedCatalog: [first, skipped, latest])
            .CheckAndUpdateAsync([], checkOnly: false, NoCancellation);

        string Local(string path) => PathSafety.CombineUnder(paths.MinecraftDirectory, path);
        Equal(false, File.Exists(Local(retiredPath)), "a file deleted by a skipped release is removed although the version already matches");
        Equal(true, logs.Any(line => line.Contains("removed by 1.0.8", StringComparison.Ordinal)), "the removal names the release that retired it");
        Equal(HashBytes(playerOwnCopy), await PathSafety.Sha256Async(Local(playerPath), NoCancellation),
            "a player's own file at a retired path is never touched");
        Equal(HashBytes(keep), await PathSafety.Sha256Async(Local(keepPath), NoCancellation), "shipped files stay");

        // legacyCleanup of an older release counts too; a second run with nothing retired stays on the fast path.
        await WriteConvergenceFileAsync(paths, legacyPath, legacy);
        await new UpdateEngine(paths, configuration, logs.Add, verifiedCatalog: [first, skipped, latest])
            .CheckAndUpdateAsync([], checkOnly: false, NoCancellation);
        Equal(false, File.Exists(Local(legacyPath)), "a legacyCleanup identity from the catalog is removed");
        logs.Clear();
        await new UpdateEngine(paths, configuration, logs.Add, verifiedCatalog: [first, skipped, latest])
            .CheckAndUpdateAsync([], checkOnly: false, NoCancellation);
        Equal(true, logs.Any(line => line.StartsWith("Verified release 1.0.9 inventory", StringComparison.Ordinal)),
            "nothing retired left means no transaction");
        Console.WriteLine("Retired official files check passed: skipped-release deletions and legacyCleanup applied, player file kept.");
    }

    private static async Task TestFtbForceCompleteKeyMigrationAsync(string root)
    {
        const string managedPath = "mods/current.jar";
        const string optionsPath = "options.txt";
        const string summaryC = "key_key.cobblemon.summary:key.keyboard.c";
        string ftbC = ManifestParser.FtbForceCompleteC;
        string ftbUnbound = ManifestParser.FtbForceCompleteUnbound;
        string migrationId = ManifestParser.FtbForceCompleteMigrationId;
        string officialNew = "renderDistance:8\n" + summaryC + "\n" + ftbUnbound + "\n";

        ManifestFile managed = FileEntry(managedPath, "managed");
        var signedBase = new UpdateManifest
        {
            SchemaVersion = 2, Version = "1.0.59", Files = [managed],
            SeedFiles = [FileEntry(optionsPath, "renderDistance:8\n" + summaryC + "\n")]
        };
        string baseHash = HashText("ftb-key-base");
        var delta = new UpdateManifest
        {
            SchemaVersion = 2, Version = "1.0.60", Files = [managed],
            SeedFiles = [FileEntry(optionsPath, officialNew)],
            SeedTextReplacements = [new SeedTextReplacement { Path = optionsPath, OldText = ftbC, NewText = ftbUnbound, MigrationId = migrationId }]
        };
        string extract = Path.Combine(root, "extract");
        await WriteRelativeTextAsync(extract, optionsPath, officialNew);

        // Player who kept FTB's default C: exactly that line is unbound, CRLF and every other setting stay byte-stable.
        UpdaterPaths affected = Paths(Path.Combine(root, "affected"));
        await WriteRelativeTextAsync(affected.MinecraftDirectory, managedPath, "managed");
        string custom = "renderDistance:23\r\n" + summaryC + "\r\n" + ftbC + "\r\nkey_key.jump:key.keyboard.space\r\n";
        await WriteRelativeTextAsync(affected.MinecraftDirectory, optionsPath, custom);
        InstalledState state = StateFrom(signedBase, baseHash);
        state.OfferedSeedPaths = [optionsPath];
        var engine = new UpdateEngine(affected, new UpdaterConfiguration { AllowedRoots = ["mods"] }, _ => { });
        Equal(true, await engine.HasPendingCorrectiveWorkAsync(delta, state, NoCancellation), "FTB C binding is pending corrective work");
        await engine.ApplyTransactionAsync(delta, HashText("ftb-key-delta"), extract, state, signedBase, NoCancellation);
        Equal(custom.Replace(ftbC, ftbUnbound, StringComparison.Ordinal),
            await ReadRelativeTextAsync(affected.MinecraftDirectory, optionsPath), "only the FTB force-complete line changed");
        InstalledState migrated = LocalStateStore.LoadState(affected);
        Equal(migrationId, migrated.AppliedPlayerSettingMigrationIds.Single(), "the migration is recorded once");

        // A later deliberate C binding is never undone.
        await WriteRelativeTextAsync(affected.MinecraftDirectory, optionsPath, custom);
        Equal(false, await engine.HasPendingCorrectiveWorkAsync(delta, migrated, NoCancellation), "completed migration never re-runs");

        // A player who moved it to another key keeps that key.
        UpdaterPaths chose = Paths(Path.Combine(root, "chose-x"));
        await WriteRelativeTextAsync(chose.MinecraftDirectory, managedPath, "managed");
        string own = "renderDistance:12\n" + summaryC + "\nkey_key.ftbquests.gui_editor.complete_object:key.keyboard.x\n";
        await WriteRelativeTextAsync(chose.MinecraftDirectory, optionsPath, own);
        InstalledState choseState = StateFrom(signedBase, baseHash);
        choseState.OfferedSeedPaths = [optionsPath];
        await new UpdateEngine(chose, new UpdaterConfiguration { AllowedRoots = ["mods"] }, _ => { })
            .ApplyTransactionAsync(delta, HashText("ftb-key-delta-x"), extract, choseState, signedBase, NoCancellation);
        Equal(own, await ReadRelativeTextAsync(chose.MinecraftDirectory, optionsPath), "a player's own key choice is preserved");

        // The parser accepts exactly this migration at updater 1.2.21+, and nothing looser.
        ManifestParser.ValidateSeedTextReplacementsForTest(delta.SeedTextReplacements, delta.SeedFiles);
        var loose = new SeedTextReplacement { Path = optionsPath, OldText = "key_key.ftbquests.quests:key.keyboard.u",
            NewText = "key_key.ftbquests.quests:key.keyboard.unknown", MigrationId = migrationId };
        bool rejected = false;
        try { ManifestParser.ValidateSeedTextReplacementsForTest([loose], delta.SeedFiles); }
        catch (InvalidDataException) { rejected = true; }
        Equal(true, rejected, "the FTB migration id cannot carry any other line");
        Console.WriteLine("FTB force-complete key migration passed: default C unbound once, player choices kept.");
    }
}
