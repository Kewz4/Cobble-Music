using CobbleMusicUpdater;

// Fresh-install sourcing (Kewz 2026-09-23: "why is it 9GB? shouldnt it be like 5gb"). 1.0.57 rolled
// InventoryParticles back to 3.0.0, whose bytes were last in a baseline in 1.0.6; the baseline plan took that jar from
// 1.0.6 and every fresh install downloaded 1.0.55 + all of 1.0.6 (9.40 GiB) instead of 1.0.55 + 1.0.56 + 1.0.57.
internal static partial class Program
{
    private static void TestSourcingPlanUsesDeltasAfterNewestBaseline()
    {
        static ManifestFile F(string path, string bytes) => new() { Path = path, Size = bytes.Length, Sha256 = HashText(bytes) };
        static RemoteRelease Release(string version, int schema, long payloadSize, List<ManifestFile> contents,
            List<ManifestFile>? files = null)
        {
            RemoteRelease release = Remote(version, schema, HashText("sourcing-" + version), baseVersion: "0", baseHash: HashText("0"));
            release.Manifest.Payload!.Size = payloadSize;
            if (schema == 1) release.Manifest.Files = contents;
            else
            {
                release.Manifest.PayloadFiles = contents;
                release.Manifest.Files = files ?? contents;
            }
            return release;
        }
        static string Versions(Dictionary<RemoteRelease, List<ManifestFile>> plan) =>
            string.Join(",", plan.Keys.Select(r => r.Manifest.Version).OrderBy(Version.Parse));

        ManifestFile rolledBack = F("mods/particles.jar", "particles-3.0.0");
        ManifestFile newer = F("mods/particles.jar", "particles-3.2.0-");
        ManifestFile core = F("mods/core.jar", "core-v2");
        ManifestFile coreOld = F("mods/core.jar", "core-v1");
        ManifestFile shared = F("config/shared.txt", "shared");
        ManifestFile x = F("mods/x.jar", "x-1");
        ManifestFile y = F("mods/y.jar", "y-1");
        RemoteRelease oldBaseline = Release("1.0.6", 1, 4470, [rolledBack, coreOld, shared]);
        RemoteRelease oldDelta = Release("1.0.36", 2, 80, [rolledBack]);
        RemoteRelease baseline = Release("1.0.55", 1, 4810, [newer, core, shared]);
        RemoteRelease delta56 = Release("1.0.56", 2, 30, [x]);
        RemoteRelease latest = Release("1.0.57", 2, 100, [rolledBack, y], [rolledBack, core, shared, x, y]);
        // Catalog order is deliberately scrambled: selection must not depend on it.
        IReadOnlyList<RemoteRelease> catalog = [latest, oldBaseline, baseline, oldDelta, delta56];

        var fresh = UpdateEngine.ChooseSourcingPlan(catalog, latest.Manifest.Files, NoCancellation);
        Equal("1.0.55,1.0.56,1.0.57", Versions(fresh), "fresh install = newest baseline + the deltas after it, no older baseline");
        Equal("1.0.57", fresh.Single(g => g.Value.Contains(rolledBack)).Key.Manifest.Version,
            "a rolled-back file comes from the newest delta that re-shipped it, not the old baseline that last held it");
        Equal(4940L, fresh.Keys.Sum(r => r.Manifest.Payload!.Size), "fresh install downloads baseline + deltas only");

        // Small repairs still take the cheapest payload per file.
        var repair = UpdateEngine.ChooseSourcingPlan(catalog, [rolledBack], NoCancellation);
        Equal("1.0.36", Versions(repair), "single-file repair still uses the cheapest payload holding the exact bytes");

        // Without any baseline in the catalog, plan B takes each file from its newest source (130 here, reusing 1.0.57)
        // and still competes with per-file cheapest (1.0.36 + 1.0.56 + 1.0.57 = 210).
        var noBaseline = UpdateEngine.ChooseSourcingPlan([latest, oldDelta, delta56], [rolledBack, x, y], NoCancellation);
        Equal("1.0.56,1.0.57", Versions(noBaseline), "the cheaper of the two plans wins");
        Throws<InvalidDataException>(() => UpdateEngine.ChooseSourcingPlan(catalog, [F("mods/missing.jar", "nowhere")], NoCancellation));
        Console.WriteLine("Sourcing plan checks passed: fresh install = newest baseline + later deltas, rolled-back file from the newest delta, small repairs cheapest, missing source rejected.");
    }

    // Replays a fresh install of the newest release against every real signed manifest in <directory>/*/cobble-music-update.json
    // (+ .sig). The plan must contain the newest baseline and nothing older than it.
    private static void TestPublishedFreshInstallSourcing(string directory)
    {
        var catalog = new List<RemoteRelease>();
        foreach (string path in Directory.EnumerateFiles(directory, "cobble-music-update.json", SearchOption.AllDirectories))
        {
            byte[] raw = File.ReadAllBytes(path);
            UpdateManifest manifest = ManifestParser.VerifyAndParse(raw, File.ReadAllBytes(Path.ChangeExtension(path, ".sig")));
            catalog.Add(new RemoteRelease(new GitHubRelease { TagName = manifest.ReleaseTag }, [], [],
                new Dictionary<string, Uri>(), manifest, HashBytes(raw)));
        }
        RemoteRelease latest = catalog.OrderByDescending(r => Version.Parse(r.Manifest.Version)).First();
        RemoteRelease newestBaseline = catalog.Where(r => r.Manifest.SchemaVersion == 1)
            .OrderByDescending(r => Version.Parse(r.Manifest.Version)).First();
        UpdateManifest target = latest.Manifest;
        List<ManifestFile> needed = target.Files
            .Where(f => !HistoricalManifestPolicy.IsPlayerOwned(target, f) && !PathSafety.IsOptionalPlayerMod(f.Path))
            .Concat(target.SeedFiles.Where(f => PathSafety.IsSeedAllowed(f.Path))).ToList();
        var plan = UpdateEngine.ChooseSourcingPlan(catalog, needed, NoCancellation);
        long bytes = plan.Keys.Sum(r => r.Manifest.Payload!.Size);
        foreach ((RemoteRelease release, List<ManifestFile> files) in plan.OrderBy(g => Version.Parse(g.Key.Manifest.Version)))
            Console.WriteLine($"  {release.Manifest.Version}: {release.Manifest.Payload!.Size / (1024 * 1024)} MiB payload, {files.Count} files");
        Equal(true, plan.ContainsKey(newestBaseline), "fresh install uses the newest baseline");
        Equal(false, plan.Keys.Any(r => Version.Parse(r.Manifest.Version) < Version.Parse(newestBaseline.Manifest.Version)),
            "fresh install downloads nothing older than the newest baseline");
        Console.WriteLine($"Published fresh-install sourcing passed: {catalog.Count} signed releases, {target.Version} from {plan.Count} payloads, {bytes / (1024 * 1024)} MiB.");
    }
}
