using System.Diagnostics;
using System.Text;
using CobbleMusicUpdater;

// Updater 1.2.22 lite mode (Kewz, 2026-09-23): weak PCs (Jim: Ryzen 7 2700 + RTX 3050) get a signed, data-driven lite
// profile - mods switched off the Prism way (.jar.disabled), resource packs taken out of the official Packed Packs
// profiles, and allow-listed one-time setting edits - and switching back to full restores exactly what was there.
internal static partial class Program
{
    private const string LiteDefaultProfile = "config/packed_packs/profiles/resourcepacks/Default.profile.json";
    private const string LiteRealisticProfile = "config/packed_packs/profiles/resourcepacks/Realistic.profile.json";
    private const string LiteSodium = "config/sodium-options.json";
    private const string LiteOptions = "options.txt";
    private const string LiteXaero = "config/xaero/world-map/profiles/cobbleverse.cfg";
    private const string LiteModA = "mods/atmospherics-2.6.6.jar";
    private const string LiteModB = "mods/particular-1.1.2.jar";
    private const string KeptMod = "mods/InventoryParticles-3.0.0.jar";

    private static async Task TestPerformanceModeAsync(string root)
    {
        TestPerformancePathPolicy();
        TestGpuTierList();
        TestSettingTextEdits();
        TestPackProfileText();
        TestPerformanceManifestValidation();
        await TestPerformanceJournalPolicyAsync(Path.Combine(root, "journal-policy"));
        await TestPerformanceDetectionAsync(Path.Combine(root, "detection"));
        await TestPerformanceModeFileAsync(Path.Combine(root, "mode-file"));
        using var signer = new ConvergenceTestSigner();
        await TestLiteFullPcUntouchedAsync(Path.Combine(root, "full-untouched"), signer);
        await TestLiteRoundTripAsync(Path.Combine(root, "round-trip"), signer);
        await TestLitePlayerDisabledModsAsync(Path.Combine(root, "player-disabled"), signer);
        await TestLiteReleaseChangesAsync(Path.Combine(root, "release-changes"), signer);
        await TestLiteCrashRecoveryAsync(Path.Combine(root, "crash"), signer);
        await TestLiteMissingSettingFileAsync(Path.Combine(root, "missing-setting"), signer);
        Console.WriteLine("Lite mode checks passed: allow-lists and shader rejection, 1.2.22 floor, GPU tiers, byte-exact setting and pack edits, "
            + "detection fallback/timeout/once-per-PC, override file both ways, FULL PCs untouched, no re-download of switched-off mods, "
            + "player-disabled mods kept, exact restore on full, release changes while lite, crash recovery.");
    }

    // ---- pure policy ----------------------------------------------------------------------------------------

    private static void TestPerformancePathPolicy()
    {
        foreach (string shader in new[] { "config/iris.properties", "config/iris-excluded.json", "shaderpacks/ComplementaryReimagined_r5.5.1.zip.txt",
            "config/kewz-shader-profiles.json", "config/oculus.properties" })
        {
            Equal(true, PathSafety.IsShaderSettingPath(shader), "shader path recognized: " + shader);
            Equal<string?>(null, PathSafety.PerformanceSettingFormat(shader), "shader path is never a lite setting target: " + shader);
        }
        Equal<string?>("json", PathSafety.PerformanceSettingFormat(LiteSodium), "sodium options are json");
        Equal<string?>("options", PathSafety.PerformanceSettingFormat(LiteOptions), "options.txt uses key:value");
        Equal<string?>("properties", PathSafety.PerformanceSettingFormat(LiteXaero), "Xaero cfg uses key = value");
        Equal<string?>(null, PathSafety.PerformanceSettingFormat("config/grassier-grass.json"), "files outside the allow-list are refused");
        Equal(false, PathSafety.IsPerformanceOptionsKeyAllowed("resourcePacks"), "the pack list is Packed Packs' job");
        Equal(false, PathSafety.IsPerformanceOptionsKeyAllowed("key_key.jump"), "keybinds are never lite settings");
        Equal(true, PathSafety.IsPerformanceOptionsKeyAllowed("renderClouds"), "renderClouds is allowed");
        Equal(true, PathSafety.IsLiteModPath(LiteModA), "top-level jar");
        Equal(false, PathSafety.IsLiteModPath("mods/axiom-5.4.2.jar"), "optional Axiom stays player-owned");
        Equal(false, PathSafety.IsLiteModPath("mods/sub/x.jar"), "nested jars are refused");
        Equal(true, PathSafety.IsPerformanceOutcomeAllowed(LiteModA + ".disabled"), "disabled copy is a performance outcome");
        Equal(true, PathSafety.IsPerformanceOutcomeAllowed(PathSafety.PerformanceLedgerPath), "ledger is a performance outcome");
        Equal(false, PathSafety.IsPerformanceOutcomeAllowed("config/iris.properties"), "Iris settings are never a performance outcome");
        Equal(false, PathSafety.IsPerformanceOutcomeAllowed("cobble-music-updater/state.json"), "installed state is never a performance outcome");
    }

    private static void TestGpuTierList()
    {
        PerformanceDetectionRules rules = LiteRules();
        Equal(GpuTier.Lite, GpuTierList.Classify("NVIDIA GeForce RTX 3050", rules), "Jim's RTX 3050 is lite");
        Equal(GpuTier.Lite, GpuTierList.Classify("NVIDIA GeForce RTX 3050 Laptop GPU", rules), "3050 laptop is lite");
        Equal(GpuTier.Full, GpuTierList.Classify("NVIDIA GeForce RTX 3080", rules), "DONGLORD9000's RTX 3080 is full");
        Equal(GpuTier.Full, GpuTierList.Classify("NVIDIA GeForce RTX 2070 Super", rules), "Kewz's RTX 2070 Super is full");
        Equal(GpuTier.Lite, GpuTierList.Classify("Intel(R) UHD Graphics", rules), "(R) marks are ignored");
        Equal(GpuTier.Lite, GpuTierList.Classify("AMD Radeon 780M Graphics", rules), "'#' matches one digit");
        Equal(GpuTier.Full, GpuTierList.Classify("AMD Radeon RX 6600 XT", rules), "6600 XT is full");
        Equal(GpuTier.Unknown, GpuTierList.Classify("AMD Radeon RX 6600", rules), "plain 6600 is on neither list (Kewz decides)");
        Equal(GpuTier.Unknown, GpuTierList.Classify("NVIDIA GeForce RTX 30500", rules), "whole words only");
        Equal(GpuTier.Full, GpuTierList.ClassifyMachine([Gpu("Intel(R) UHD Graphics"), Gpu("NVIDIA GeForce RTX 2070 Super")], rules),
            "a laptop with a full dedicated card is full on graphics");
        Equal(GpuTier.Lite, GpuTierList.ClassifyMachine([Gpu("Intel(R) UHD Graphics"), Gpu("NVIDIA GeForce RTX 3050 Laptop GPU")], rules),
            "only lite adapters vote lite");
        Equal(GpuTier.Unknown, GpuTierList.ClassifyMachine([Gpu("Intel(R) UHD Graphics"), Gpu("NVIDIA GeForce RTX 9090")], rules),
            "an unknown card keeps graphics neutral");
        Equal(GpuTier.Unknown, GpuTierList.ClassifyMachine([], rules), "no adapters read is neutral");
        Equal(true, GpuRegistry.IsVirtualAdapter("Microsoft Basic Display Adapter"), "basic display adapter is skipped");
    }

    private static void TestSettingTextEdits()
    {
        // JSON: nested key, UTF-8 BOM, CRLF; only the value token changes, quotes included in the span.
        string json = "\uFEFF{\r\n  \"quality\": {\r\n    \"weather_quality\": \"DEFAULT\",\r\n    \"leaves_quality\": \"FANCY\"\r\n  },\r\n"
            + "  \"list\": [ { \"leaves_quality\": \"X\" } ],\r\n  \"leaves_quality\": \"TOP\"\r\n}\r\n";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        SettingText.ValueSpan? span = SettingText.Find(bytes, "json", "quality.leaves_quality");
        Equal("\"FANCY\"", span?.Raw, "json scalar span includes its quotes");
        byte[] edited = SettingText.Replace(bytes, span!.Value, "\"FAST\"");
        Equal(json.Replace("\"FANCY\"", "\"FAST\""), Encoding.UTF8.GetString(edited), "only the value changed, BOM and CRLF kept");
        Equal("\"TOP\"", SettingText.Find(bytes, "json", "leaves_quality")?.Raw, "root-level key is distinct from nested");
        Equal(false, SettingText.Find(bytes, "json", "quality").HasValue, "objects are not scalars");
        Equal(false, SettingText.Find(bytes, "json", "list.leaves_quality").HasValue, "values inside arrays are not addressable");
        Equal(false, SettingText.Find(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}"), "json", "a").HasValue, "a repeated key is ambiguous");
        Equal("false", SettingText.Find(Encoding.UTF8.GetBytes("{\"a\": {\"b\": false}}"), "json", "a.b")?.Raw, "booleans are scalars");

        // options.txt: exact key, CRLF, value text after the first ':'.
        string options = "version:3955\r\nrenderClouds:\"true\"\r\nrenderCloudsX:1\r\nkey_key.jump:key.keyboard.space\r\n";
        byte[] optionsBytes = Encoding.UTF8.GetBytes(options);
        SettingText.ValueSpan? clouds = SettingText.Find(optionsBytes, "options", "renderClouds");
        Equal("\"true\"", clouds?.Raw, "options value text");
        Equal(options.Replace("renderClouds:\"true\"", "renderClouds:\"false\""),
            Encoding.UTF8.GetString(SettingText.Replace(optionsBytes, clouds!.Value, "\"false\"")), "options edit keeps CRLF and other lines");
        Equal(false, SettingText.Find(Encoding.UTF8.GetBytes("a:1\na:2\n"), "options", "a").HasValue, "a repeated options key is ambiguous");
        Equal(false, SettingText.Find(new byte[] { 0x61, 0x3A, 0xFF, 0x0A }, "options", "a").HasValue, "non-UTF-8 files are never edited");
        string unicode = "name:Pokémon ✓\nparticles:0\n";
        byte[] unicodeBytes = Encoding.UTF8.GetBytes(unicode);
        SettingText.ValueSpan? particles = SettingText.Find(unicodeBytes, "options", "particles");
        Equal("name:Pokémon ✓\nparticles:1\n", Encoding.UTF8.GetString(SettingText.Replace(unicodeBytes, particles!.Value, "1")),
            "byte offsets are right after multi-byte characters");

        // properties: spaces around '=', comments and duplicates.
        string cfg = "# biome_blending = comment\nbiome_blending = true  \n  other=1\n";
        byte[] cfgBytes = Encoding.UTF8.GetBytes(cfg);
        SettingText.ValueSpan? blend = SettingText.Find(cfgBytes, "properties", "biome_blending");
        Equal("true", blend?.Raw, "properties value text, comments skipped");
        Equal(cfg.Replace("= true  ", "= false  "), Encoding.UTF8.GetString(SettingText.Replace(cfgBytes, blend!.Value, "false")),
            "properties edit keeps the spacing");
        Equal(false, SettingText.Find(Encoding.UTF8.GetBytes("a=1\n[x]\na=2\n"), "properties", "a").HasValue, "a repeated key is ambiguous");
    }

    private static void TestPackProfileText()
    {
        byte[] profile = Encoding.UTF8.GetBytes(ProfileText("Default", ["vinery:bushy_leaves", "file/Toasty's Fresher Ferns.zip", "file/sunbathing-v1.10.zip", "fabric", "vanilla"]));
        PackProfileText.PackList list = PackProfileText.Parse(profile) ?? throw new InvalidOperationException("profile parses");
        Equal(5, list.Entries.Count, "all pack ids read");
        byte[] filtered = PackProfileText.Write(profile, list, list.Entries.Where(entry => !entry.Id.Contains("Toasty") && !entry.Id.Contains("sunbathing")).ToList());
        Equal(ProfileText("Default", ["vinery:bushy_leaves", "fabric", "vanilla"]), Encoding.UTF8.GetString(filtered), "removal keeps the file's own formatting");
        PackProfileText.PackList filteredList = PackProfileText.Parse(filtered)!;
        var entries = filteredList.Entries.ToList();
        entries.Insert(1, ("file/Toasty's Fresher Ferns.zip", PackProfileText.Encode("file/Toasty's Fresher Ferns.zip")));
        entries.Insert(2, ("file/sunbathing-v1.10.zip", PackProfileText.Encode("file/sunbathing-v1.10.zip")));
        Equal(Encoding.UTF8.GetString(profile), Encoding.UTF8.GetString(PackProfileText.Write(filtered, filteredList, entries)),
            "re-inserting gives back the exact original text (apostrophes are not escaped)");
        Equal(true, PackProfileText.Parse(Encoding.UTF8.GetBytes("{\"packIds\": [1, 2]}")) is null, "non-string entries are refused");
    }

    private static void TestPerformanceManifestValidation()
    {
        Validate(_ => { }, expectValid: true, "a well-formed lite profile at 1.2.22 is valid");
        Validate(m => m.MinimumUpdaterVersion = "1.2.21", expectValid: false, "performance profiles require the 1.2.22 floor");
        Validate(m => m.PerformanceProfiles = new PerformanceProfiles(), expectValid: true, "an empty performanceProfiles object is valid");
        Validate(m => { m.PerformanceProfiles = new PerformanceProfiles(); m.MinimumUpdaterVersion = "1.2.21"; }, expectValid: false,
            "even an empty section needs the floor");
        Validate(m => m.PerformanceProfiles!.Lite!.DisabledMods.Add("mods/not-shipped.jar"), false, "a lite mod must be a managed file of the release");
        Validate(m => m.PerformanceProfiles!.Lite!.DisabledMods.Add("mods/axiom-5.jar"), false, "Axiom is never a lite mod");
        Validate(m => m.PerformanceProfiles!.Lite!.DisabledMods.Add(LiteModA), false, "duplicate lite mod");
        Validate(m => m.PerformanceProfiles!.Lite!.RemovedPackIds.Add("vanilla"), false, "vanilla can never be removed");
        Validate(m => m.PerformanceProfiles!.Lite!.RevisionId = "Lite V1", false, "revision id syntax");
        Validate(m => m.PerformanceProfiles!.Lite!.Detection.CpuScoreThreshold = 0, false, "threshold must be positive");
        Validate(m => m.PerformanceProfiles!.Lite!.Detection.LiteGpuPatterns.Add("RTX 3050"), false, "a duplicate GPU pattern");
        Validate(m => m.PerformanceProfiles!.Lite!.Detection.LiteGpuPatterns.Add("RTX.*"), false, "GPU patterns are words, not regexes");
        foreach ((string path, string format, string key, string value, string why) in new[]
        {
            ("config/iris.properties", "properties", "enableShaders", "false", "Iris settings are rejected"),
            ("shaderpacks/ComplementaryReimagined_r5.5.1.zip.txt", "properties", "SHADOW_QUALITY", "-1", "shaderpack options are rejected"),
            ("config/kewz-shader-profiles.json", "json", "grassierGrassMode", "\"OFF\"", "shader profiles are rejected"),
            (LiteOptions, "options", "resourcePacks", "[]", "the options pack list is rejected"),
            (LiteOptions, "options", "key_key.jump", "key.keyboard.x", "keybinds are rejected"),
            (LiteSodium, "json", "quality", "{}", "non-scalar JSON values are rejected"),
            (LiteSodium, "options", "quality.leaves_quality", "FAST", "the format must match the allow-list"),
            (LiteSodium, "json", "quality.leaves_quality", "\"FANCY\"\n", "control characters are rejected"),
            ("config/voxy-config.json", "json", "enabled", "false", "a file this release does not declare as a default is rejected"),
            (LiteModA, "json", "x", "1", "managed files are never setting targets")
        })
        {
            Validate(m => m.PerformanceProfiles!.Lite!.Settings.Add(new PerformanceSetting { Path = path, Format = format, Key = key, Value = value }), false, why);
        }
        Validate(m => m.PerformanceProfiles!.Lite!.Settings.Add(new PerformanceSetting
        {
            Path = LiteSodium, Format = "json", Key = "quality.leaves_quality", Value = "\"FAST\""
        }), false, "the same setting twice");
    }

    private static void Validate(Action<UpdateManifest> mutate, bool expectValid, string context)
    {
        byte[] jarA = ConvergenceFabricJar("atmospherics", "2.6.6");
        var manifest = new UpdateManifest
        {
            SchemaVersion = 1, ModpackId = BuildInfo.DefaultModpackId, Channel = "stable", Version = "1.0.61",
            ReleaseTag = "modpack-v1.0.61", MinimumUpdaterVersion = "1.2.22",
            Files = [ConvergenceFile(LiteModA, jarA), ConvergenceFile(KeptMod, ConvergenceFabricJar("inventoryparticles", "3.0.0"))],
            SeedFiles = [FileEntry(LiteOptions, "renderClouds:\"true\"\n"), FileEntry(LiteSodium, "{}")],
            Payload = new UpdatePayload
            {
                ArchiveName = "payload.zip", Size = 10, Sha256 = HashText("payload"),
                Parts = [new PayloadPart { Name = "payload.part001", Size = 10, Sha256 = HashText("payload") }]
            },
            PerformanceProfiles = new PerformanceProfiles
            {
                Lite = new LitePerformanceProfile
                {
                    RevisionId = "lite-1.0.61-v1",
                    Detection = LiteRules(),
                    DisabledMods = [LiteModA],
                    RemovedPackIds = ["file/sunbathing-v1.10.zip"],
                    Settings = [new PerformanceSetting { Path = LiteSodium, Format = "json", Key = "quality.leaves_quality", Value = "\"FAST\"" }]
                }
            }
        };
        mutate(manifest);
        var urls = new Dictionary<string, Uri> { ["payload.part001"] = new("https://example.invalid/payload.part001") };
        bool valid = true;
        try { ManifestParser.Validate(manifest, new UpdaterConfiguration { AllowedRoots = ["mods", "config"] }, urls); }
        catch (InvalidDataException) { valid = false; }
        Equal(expectValid, valid, context);
    }

    private static async Task TestPerformanceJournalPolicyAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        var journal = new TransactionJournal
        {
            PreviousState = new InstalledState(), NextState = new InstalledState(),
            PerformanceOutcomes = [new ManagedFileState { Path = "config/iris.properties", Size = 1, Sha256 = HashText("x") }]
        };
        await TransactionStore.SaveAsync(paths, journal, NoCancellation);
        await ThrowsAsync<TransactionRecoveryException>(() => TransactionStore.RecoverIfNeededAsync(paths, BuildInfo.SupportedRoots, _ => { }));
        File.Delete(TransactionStore.JournalPath(paths));
    }

    // ---- detection ------------------------------------------------------------------------------------------

    private static async Task TestPerformanceDetectionAsync(string root)
    {
        var lite = new UpdateManifest { PerformanceProfiles = new PerformanceProfiles { Lite = new LitePerformanceProfile { Detection = LiteRules() } } };

        async Task<(PerformancePlan Plan, List<string> Logs)> DecideAsync(string name, PerformanceEnvironment environment, bool checkOnly = false)
        {
            UpdaterPaths paths = Paths(Path.Combine(root, name));
            Directory.CreateDirectory(paths.MinecraftDirectory);
            var logs = new List<string>();
            var engine = new UpdateEngine(paths, Configuration(), logs.Add, performanceEnvironment: environment);
            return (await engine.PreparePerformancePlanAsync(lite, checkOnly, NoCancellation), logs);
        }

        // Jim, DONGLORD9000 and Kewz's own laptop (numbers from lite0924/research: 833 / 1324 / ~1000).
        (PerformancePlan jim, List<string> jimLogs) = await DecideAsync("jim", Machine("AMD Ryzen 7 2700 Eight-Core Processor", 833, "NVIDIA GeForce RTX 3050"));
        Equal(PerformanceMode.Lite, jim.Mode, "Jim is lite");
        string line = jimLogs.Single(entry => entry.StartsWith("Performance check:", StringComparison.Ordinal));
        Equal(true, line.Contains("\"AMD Ryzen 7 2700 Eight-Core Processor\" score 833 (lite below 1050)", StringComparison.Ordinal)
            && line.Contains("\"NVIDIA GeForce RTX 3050\" (lite list)", StringComparison.Ordinal)
            && line.Contains("result LITE (processor and graphics card)", StringComparison.Ordinal), "one clear log line: " + line);
        (PerformancePlan don, _) = await DecideAsync("donglord", Machine("AMD Ryzen 7 5800X 8-Core Processor", 1324, "NVIDIA GeForce RTX 3080"));
        Equal(PerformanceMode.Full, don.Mode, "DONGLORD9000 is full");
        (PerformancePlan kewz, List<string> kewzLogs) = await DecideAsync("kewz",
            Machine("Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz", 1000, "Intel(R) UHD Graphics", "NVIDIA GeForce RTX 2070 Super"));
        Equal(PerformanceMode.Lite, kewz.Mode, "Kewz's i7-10750H is lite at threshold 1050 (processor only)");
        Equal(true, kewzLogs.Any(entry => entry.Contains("result LITE (processor)", StringComparison.Ordinal)), "Kewz's reason is the processor");

        // Fallback: nothing readable -> full, with the reasons in the log.
        (PerformancePlan broken, List<string> brokenLogs) = await DecideAsync("broken", new PerformanceEnvironment
        {
            ReadCpuName = () => throw new UnauthorizedAccessException("denied"),
            ReadGpus = () => throw new System.Security.SecurityException("registry denied"),
            MeasureCpu = _ => throw new InvalidOperationException("probe broke")
        });
        Equal(PerformanceMode.Full, broken.Mode, "unreadable hardware means full");
        Equal(true, brokenLogs.Any(entry => entry.Contains("processor check failed", StringComparison.Ordinal)), "probe failure logged");
        Equal(true, brokenLogs.Any(entry => entry.Contains("graphics card not read (SecurityException", StringComparison.Ordinal)), "GPU failure logged");

        // A GPU read failure alone does not hide a slow processor.
        (PerformancePlan noGpu, _) = await DecideAsync("no-gpu", new PerformanceEnvironment
        {
            ReadCpuName = () => "AMD Ryzen 7 2700 Eight-Core Processor",
            ReadGpus = () => throw new IOException("no registry"),
            MeasureCpu = _ => new CpuProbeResult(833, 5400, 950)
        });
        Equal(PerformanceMode.Lite, noGpu.Mode, "the processor still votes when the graphics card cannot be read");

        // Timeout: a probe that never finishes is abandoned after ProbeTimeout; the update is not held up.
        var clock = Stopwatch.StartNew();
        (PerformancePlan slow, List<string> slowLogs) = await DecideAsync("timeout", new PerformanceEnvironment
        {
            ReadCpuName = () => "Stuck CPU",
            ReadGpus = () => [Gpu("NVIDIA GeForce RTX 3080")],
            MeasureCpu = token => { Task.Delay(TimeSpan.FromSeconds(30), token).Wait(token); return new CpuProbeResult(1, 1, 1); },
            ProbeTimeout = TimeSpan.FromMilliseconds(300)
        });
        Equal(true, clock.Elapsed < TimeSpan.FromSeconds(5), "a stuck processor check never holds the update");
        Equal(PerformanceMode.Full, slow.Mode, "timed-out check on a full graphics card is full");
        Equal(true, slowLogs.Any(entry => entry.Contains("timed out after 0.3 s", StringComparison.Ordinal)), "timeout logged");

        // Once per PC: the probe runs once, its raw numbers are stored machine-wide and reused by every instance.
        // Two instances on one PC: their per-instance data folders share one parent (%LOCALAPPDATA%\CobbleMusicUpdater).
        UpdaterPaths first = PathsWithLocal(Path.Combine(root, "once", "a"), Path.Combine(root, "once", "machine", "instance-a"));
        UpdaterPaths second = PathsWithLocal(Path.Combine(root, "once", "b"), Path.Combine(root, "once", "machine", "instance-b"));
        Directory.CreateDirectory(first.MinecraftDirectory);
        Directory.CreateDirectory(second.MinecraftDirectory);
        int runs = 0;
        string cpuName = "AMD Ryzen 7 2700 Eight-Core Processor";
        var counting = new PerformanceEnvironment
        {
            ReadCpuName = () => cpuName,
            ReadGpus = () => [Gpu("NVIDIA GeForce RTX 3050")],
            MeasureCpu = _ => { runs++; return new CpuProbeResult(833.4, 5417.1, 951); }
        };
        await new UpdateEngine(first, Configuration(), _ => { }, performanceEnvironment: counting).PreparePerformancePlanAsync(lite, false, NoCancellation);
        await new UpdateEngine(first, Configuration(), _ => { }, performanceEnvironment: counting).PreparePerformancePlanAsync(lite, false, NoCancellation);
        await new UpdateEngine(second, Configuration(), _ => { }, performanceEnvironment: counting).PreparePerformancePlanAsync(lite, false, NoCancellation);
        Equal(1, runs, "the processor check runs once per PC");
        MachinePerformanceRecord stored = MachinePerformanceStore.Load(MachinePerformanceStore.PathFor(first))!;
        Equal(833.4, stored.CpuScore, "raw score recorded");
        Equal(5417.1, stored.CpuQuantaPerSecond, "raw quanta per second recorded");
        Equal("NVIDIA GeForce RTX 3050", stored.Gpus.Single().Name, "graphics card recorded");
        cpuName = "AMD Ryzen 7 5800X 8-Core Processor";
        await new UpdateEngine(first, Configuration(), _ => { }, performanceEnvironment: counting).PreparePerformancePlanAsync(lite, false, NoCancellation);
        Equal(2, runs, "a new processor is measured again");
        await new UpdateEngine(first, Configuration(), _ => { }, performanceEnvironment: counting).PreparePerformancePlanAsync(lite, checkOnly: true, NoCancellation);
        Equal(2, runs, "check-only never measures");

        // A failing probe is retried on later launches, at most MaximumCpuAttempts times.
        UpdaterPaths retry = Paths(Path.Combine(root, "retry"));
        Directory.CreateDirectory(retry.MinecraftDirectory);
        int failures = 0;
        var failing = new PerformanceEnvironment
        {
            ReadCpuName = () => "Flaky CPU",
            ReadGpus = () => [],
            MeasureCpu = _ => { failures++; throw new InvalidOperationException("busy"); }
        };
        for (int launch = 0; launch < 5; launch++)
            await new UpdateEngine(retry, Configuration(), _ => { }, performanceEnvironment: failing).PreparePerformancePlanAsync(lite, false, NoCancellation);
        Equal(3, failures, "a failing check is retried at most three times");

        // No lite list in the release: no check at all.
        UpdaterPaths none = Paths(Path.Combine(root, "no-lite"));
        Directory.CreateDirectory(none.MinecraftDirectory);
        int noneRuns = 0;
        PerformancePlan nonePlan = await new UpdateEngine(none, Configuration(), _ => { }, performanceEnvironment: new PerformanceEnvironment
        {
            ReadCpuName = () => "x", ReadGpus = () => [], MeasureCpu = _ => { noneRuns++; return new CpuProbeResult(1, 1, 1); }
        }).PreparePerformancePlanAsync(new UpdateManifest(), false, NoCancellation);
        Equal(PerformanceMode.Full, nonePlan.Mode, "no lite list = full");
        Equal(0, noneRuns, "no lite list = no hardware check");
        Equal(false, File.Exists(PerformanceModeFile.PathFor(none)), "no lite list = no mode file");
    }

    private static async Task TestPerformanceModeFileAsync(string root)
    {
        var lite = new UpdateManifest { PerformanceProfiles = new PerformanceProfiles { Lite = new LitePerformanceProfile { Detection = LiteRules() } } };
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        int runs = 0;
        var fast = new PerformanceEnvironment
        {
            ReadCpuName = () => "AMD Ryzen 7 5800X 8-Core Processor",
            ReadGpus = () => [Gpu("NVIDIA GeForce RTX 3080")],
            MeasureCpu = _ => { runs++; return new CpuProbeResult(1324, 8606, 950); }
        };
        async Task<PerformanceMode> ModeAsync(List<string>? logs = null) =>
            (await new UpdateEngine(paths, Configuration(), line => logs?.Add(line), performanceEnvironment: fast)
                .PreparePerformancePlanAsync(lite, false, NoCancellation)).Mode;

        Equal(PerformanceMode.Full, await ModeAsync(), "auto on a fast PC is full");
        string file = PerformanceModeFile.PathFor(paths);
        string created = await File.ReadAllTextAsync(file);
        Equal(true, created.Contains("\r\nmode=auto\r\n", StringComparison.Ordinal), "the file is created with mode=auto");
        Equal(true, created.Contains("# This computer: processor score 1324 (lite below 1050), graphics card NVIDIA GeForce RTX 3080 (full list). Picked: full.",
            StringComparison.Ordinal), "the file explains the pick in plain words: " + created);

        // The player forces lite; their exact line (spacing, capitals) is kept and the check is skipped.
        await File.WriteAllTextAsync(file, created.Replace("mode=auto", "mode = LITE"));
        int before = runs;
        Equal(PerformanceMode.Lite, await ModeAsync(), "mode=lite forces lite on a fast PC");
        Equal(before, runs, "a forced mode skips the hardware check");
        string forced = await File.ReadAllTextAsync(file);
        Equal(true, forced.Contains("\r\nmode = LITE\r\n", StringComparison.Ordinal), "the player's mode line is untouched");
        Equal(true, forced.Contains("Picked: lite.", StringComparison.Ordinal), "the status line follows the pick");

        await File.WriteAllTextAsync(file, "mode=turbo\n");
        var logs = new List<string>();
        Equal(PerformanceMode.Full, await ModeAsync(logs), "an unknown value means auto");
        Equal(true, logs.Any(entry => entry.Contains("mode=turbo, which is not auto, lite or full", StringComparison.Ordinal)), "the bad value is logged");
        Equal(true, (await File.ReadAllTextAsync(file)).StartsWith("mode=turbo\n# This computer:", StringComparison.Ordinal), "status appended, player text kept");
    }

    // ---- end to end through convergence ----------------------------------------------------------------------

    private sealed class LiteFixture
    {
        public required UpdaterPaths Paths { get; init; }
        public UpdaterConfiguration Configuration { get; } = new() { AllowedRoots = ["mods", "config", "resourcepacks"] };
        public Dictionary<string, byte[]> Managed { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> Seeds { get; } = new(StringComparer.Ordinal);
        public List<RemoteRelease> Catalog { get; } = [];
    }

    private static LiteFixture NewLiteFixture(string root)
    {
        var fixture = new LiteFixture { Paths = Paths(root) };
        Directory.CreateDirectory(fixture.Paths.MinecraftDirectory);
        fixture.Managed[LiteModA] = ConvergenceFabricJar("atmospherics", "2.6.6");
        fixture.Managed[LiteModB] = ConvergenceFabricJar("particular", "1.1.2");
        fixture.Managed[KeptMod] = ConvergenceFabricJar("inventoryparticles", "3.0.0");
        fixture.Managed[LiteDefaultProfile] = Encoding.UTF8.GetBytes(ProfileText("Default",
            ["vinery:bushy_leaves", "file/Connected-Bricks 1.21-1.21.3 v3.1.zip", "file/Toasty's Fresher Ferns.zip", "file/sunbathing-v1.10.zip",
             "atmospherics:atmospherics_pack", "file/Kewz Client Compatibility.zip", "fabric", "vanilla"]));
        fixture.Managed[LiteRealisticProfile] = Encoding.UTF8.GetBytes(ProfileText("Realistic",
            ["file/Connected-Bricks 1.21-1.21.3 v3.1.zip", "file/Realistic Water.zip", "file/Toasty's Fresher Ferns.zip", "fabric", "vanilla"]));
        fixture.Seeds[LiteSodium] = Encoding.UTF8.GetBytes("\uFEFF{\r\n  \"quality\": {\r\n    \"weather_quality\": \"DEFAULT\",\r\n    \"leaves_quality\": \"FANCY\",\r\n"
            + "    \"enable_vignette\": false\r\n  },\r\n  \"performance\": {\r\n    \"chunk_builder_threads\": 0\r\n  }\r\n}");
        fixture.Seeds[LiteOptions] = Encoding.UTF8.GetBytes("version:3955\r\nbiomeBlendRadius:3\r\nentityShadows:true\r\nparticles:0\r\n"
            + "renderClouds:\"true\"\r\nresourcePacks:[\"vanilla\",\"fabric\"]\r\nkey_key.jump:key.keyboard.space\r\n");
        fixture.Seeds[LiteXaero] = Encoding.UTF8.GetBytes("############\n## Client Config Profile \"cobbleverse\"\n##\n\nprofile_name = COBBLEVERSE\nbiome_blending = true\n");
        return fixture;
    }

    private static LitePerformanceProfile FixtureLite() => new()
    {
        RevisionId = "lite-test-v1",
        Detection = LiteRules(),
        DisabledMods = [LiteModA, LiteModB],
        RemovedPackIds = ["file/Connected-Bricks 1.21-1.21.3 v3.1.zip", "file/Toasty's Fresher Ferns.zip", "file/sunbathing-v1.10.zip",
            "atmospherics:atmospherics_pack", "file/not-in-any-profile.zip"],
        Settings =
        [
            new PerformanceSetting { Path = LiteSodium, Format = "json", Key = "quality.leaves_quality", Value = "\"FAST\"" },
            new PerformanceSetting { Path = LiteOptions, Format = "options", Key = "renderClouds", Value = "\"false\"" },
            new PerformanceSetting { Path = LiteOptions, Format = "options", Key = "entityShadows", Value = "false" },
            new PerformanceSetting { Path = LiteXaero, Format = "properties", Key = "biome_blending", Value = "false" }
        ]
    };

    private static async Task<RemoteRelease> PublishLiteAsync(LiteFixture fixture, string version, LitePerformanceProfile? lite, ConvergenceTestSigner signer)
    {
        var payload = new Dictionary<string, byte[]>(fixture.Managed);
        foreach ((string path, byte[] bytes) in fixture.Seeds) payload[path] = bytes;
        RemoteRelease release = await CreateSignedConvergenceReleaseAsync(fixture.Paths, new UpdateManifest
        {
            SchemaVersion = 1, Version = version,
            Files = fixture.Managed.Select(pair => ConvergenceFile(pair.Key, pair.Value)).ToList(),
            SeedFiles = fixture.Seeds.Select(pair => ConvergenceFile(pair.Key, pair.Value)).ToList(),
            PerformanceProfiles = lite is null ? null : new PerformanceProfiles { Lite = lite }
        }, payload, signer, fixture.Configuration);
        fixture.Catalog.Add(release);
        return release;
    }

    private static async Task<List<string>> LaunchLiteAsync(LiteFixture fixture, PerformanceEnvironment environment)
    {
        var logs = new List<string>();
        await new UpdateEngine(fixture.Paths, fixture.Configuration, logs.Add, verifiedCatalog: fixture.Catalog, performanceEnvironment: environment)
            .CheckAndUpdateAsync([], checkOnly: false, NoCancellation);
        Equal(false, File.Exists(TransactionStore.JournalPath(fixture.Paths)), "every launch finishes its journal");
        return logs;
    }

    private static Dictionary<string, string> GameSnapshot(UpdaterPaths paths) =>
        Directory.GetFiles(paths.MinecraftDirectory, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(paths.MinecraftDirectory, path).Replace('\\', '/'), Full: path))
            .Where(item => !item.Relative.StartsWith("cobble-music-updater/", StringComparison.Ordinal))
            .ToDictionary(item => item.Relative, item => HashBytes(File.ReadAllBytes(item.Full)), StringComparer.Ordinal);

    private static void SameSnapshot(Dictionary<string, string> expected, Dictionary<string, string> actual, string context)
    {
        var differences = expected.Keys.Union(actual.Keys).Where(key =>
            !expected.TryGetValue(key, out string? left) || !actual.TryGetValue(key, out string? right) || left != right).OrderBy(key => key).ToList();
        Equal("", string.Join(", ", differences), context);
    }

    private static string LocalText(LiteFixture fixture, string relative) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, relative)));

    private static bool LocalExists(LiteFixture fixture, string relative) =>
        File.Exists(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, relative));

    private static async Task SetModeAsync(LiteFixture fixture, string mode)
    {
        string file = PerformanceModeFile.PathFor(fixture.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "mode=" + mode + "\r\n");
    }

    private static void DropPayloadCaches(LiteFixture fixture)
    {
        // Any download after this would hit https://example.invalid and fail the test.
        string staging = Path.Combine(fixture.Paths.LocalDataDirectory, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
    }

    private static async Task TestLiteFullPcUntouchedAsync(string root, ConvergenceTestSigner signer)
    {
        PerformanceEnvironment donglord = Machine("AMD Ryzen 7 5800X 8-Core Processor", 1324, "NVIDIA GeForce RTX 3080");
        LiteFixture plain = NewLiteFixture(Path.Combine(root, "no-lite-list"));
        await PublishLiteAsync(plain, "1.0.61", null, signer);
        await LaunchLiteAsync(plain, donglord);
        LiteFixture withList = NewLiteFixture(Path.Combine(root, "lite-list-full-pc"));
        await PublishLiteAsync(withList, "1.0.61", FixtureLite(), signer);
        List<string> logs = await LaunchLiteAsync(withList, donglord);
        SameSnapshot(GameSnapshot(plain.Paths), GameSnapshot(withList.Paths), "a FULL PC gets byte-for-byte the same game files as a release without a lite list");
        Equal(false, LocalExists(withList, PathSafety.PerformanceLedgerPath), "a FULL PC never gets a lite record");
        Equal(true, logs.Any(line => line.Contains("result FULL", StringComparison.Ordinal)), "the FULL verdict is logged");
        Equal(false, logs.Any(line => line.StartsWith("Performance mode FULL applied", StringComparison.Ordinal)), "nothing is switched on a FULL PC");
        string rollback = Path.Combine(withList.Paths.LocalDataDirectory, "rollback");
        int backupsBefore = Directory.Exists(rollback) ? Directory.GetFiles(rollback, "*", SearchOption.AllDirectories).Length : 0;
        DropPayloadCaches(withList);
        await LaunchLiteAsync(withList, donglord);
        Equal(backupsBefore, Directory.Exists(rollback) ? Directory.GetFiles(rollback, "*", SearchOption.AllDirectories).Length : 0,
            "a second FULL launch makes no transaction");
    }

    private static async Task TestLiteRoundTripAsync(string root, ConvergenceTestSigner signer)
    {
        PerformanceEnvironment jim = Machine("AMD Ryzen 7 2700 Eight-Core Processor", 833, "NVIDIA GeForce RTX 3050");
        LiteFixture fixture = NewLiteFixture(root);
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);

        // The full pack first (Jim forced full), so there is an exact "before lite" picture.
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        Dictionary<string, string> beforeLite = GameSnapshot(fixture.Paths);
        string optionsBefore = LocalText(fixture, LiteOptions);
        string sodiumBefore = LocalText(fixture, LiteSodium);
        string xaeroBefore = LocalText(fixture, LiteXaero);
        Equal(false, LocalExists(fixture, PathSafety.PerformanceLedgerPath), "forced full on a lite PC changes nothing");

        // Auto on Jim's PC: lite.
        DropPayloadCaches(fixture);
        await SetModeAsync(fixture, "auto");
        List<string> logs = await LaunchLiteAsync(fixture, jim);
        Equal(true, logs.Any(line => line.Contains("result LITE", StringComparison.Ordinal)), "Jim's PC picks lite");
        foreach (string mod in new[] { LiteModA, LiteModB })
        {
            Equal(false, LocalExists(fixture, mod), "lite mod switched off: " + mod);
            Equal(HashBytes(fixture.Managed[mod]), HashBytes(File.ReadAllBytes(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, mod + ".disabled"))),
                "the Prism-visible .jar.disabled holds the exact signed bytes: " + mod);
        }
        Equal(HashBytes(fixture.Managed[KeptMod]), GameSnapshot(fixture.Paths)[KeptMod], "Inventory Particles stays on");
        Equal(ProfileText("Default", ["vinery:bushy_leaves", "file/Kewz Client Compatibility.zip", "fabric", "vanilla"]),
            LocalText(fixture, LiteDefaultProfile), "Default profile loses exactly the listed packs");
        Equal(ProfileText("Realistic", ["file/Realistic Water.zip", "fabric", "vanilla"]), LocalText(fixture, LiteRealisticProfile),
            "Realistic profile loses exactly the listed packs");
        Equal(sodiumBefore.Replace("\"FANCY\"", "\"FAST\""), LocalText(fixture, LiteSodium), "leaves FAST; BOM, CRLF and every other byte kept");
        Equal(optionsBefore.Replace("renderClouds:\"true\"", "renderClouds:\"false\"").Replace("entityShadows:true", "entityShadows:false"),
            LocalText(fixture, LiteOptions), "options: exactly the two listed values changed");
        Equal(xaeroBefore.Replace("biome_blending = true", "biome_blending = false"), LocalText(fixture, LiteXaero), "Xaero: exactly one value changed");
        Equal(true, LocalExists(fixture, PathSafety.PerformanceLedgerPath), "lite keeps its record");

        // Next launches: nothing is downloaded or re-applied.
        string ledger = LocalText(fixture, PathSafety.PerformanceLedgerPath);
        logs = await LaunchLiteAsync(fixture, jim);
        Equal(false, logs.Any(line => line.StartsWith("Repair needed:", StringComparison.Ordinal)), "switched-off mods are not re-added by convergence");
        Equal(false, logs.Any(line => line.StartsWith("Performance mode LITE applied", StringComparison.Ordinal)), "lite applies once");
        Equal(ledger, LocalText(fixture, PathSafety.PerformanceLedgerPath), "the record is stable");

        // The player changes a lite setting by hand: lite never fights it.
        string sodiumPlayer = LocalText(fixture, LiteSodium).Replace("\"FAST\"", "\"FANCY\"");
        await File.WriteAllTextAsync(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteSodium), sodiumPlayer, new UTF8Encoding(false));
        string optionsPlayer = LocalText(fixture, LiteOptions).Replace("renderClouds:\"false\"", "renderClouds:\"fast\"");
        await File.WriteAllTextAsync(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteOptions), optionsPlayer, new UTF8Encoding(false));
        await LaunchLiteAsync(fixture, jim);
        Equal(sodiumPlayer, LocalText(fixture, LiteSodium), "a value the player changed back is not re-applied");
        Equal(optionsPlayer, LocalText(fixture, LiteOptions), "the player's own clouds value stays");

        // The player runs the game: Packed Packs re-serializes a profile (same packs, other bytes).
        string rewritten = LocalText(fixture, LiteRealisticProfile).Replace("\n", "\r\n");
        await File.WriteAllTextAsync(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteRealisticProfile), rewritten, new UTF8Encoding(false));

        // Back to full: no download, every lite change undone exactly.
        await SetModeAsync(fixture, "full");
        logs = await LaunchLiteAsync(fixture, jim);
        Equal(false, logs.Any(line => line.StartsWith("Repair needed:", StringComparison.Ordinal)), "switching back to full needs no download");
        Dictionary<string, string> afterFull = GameSnapshot(fixture.Paths);
        var expected = new Dictionary<string, string>(beforeLite);
        expected[LiteOptions] = HashBytes(Encoding.UTF8.GetBytes(optionsBefore.Replace("renderClouds:\"true\"", "renderClouds:\"fast\"")));
        expected[LiteRealisticProfile] = HashBytes(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Managed[LiteRealisticProfile]).Replace("\n", "\r\n")));
        SameSnapshot(expected, afterFull, "full restores every file exactly (only the player's own clouds value and Packed Packs' own line endings differ)");
        Equal(false, LocalExists(fixture, LiteModA + ".disabled"), "no disabled copies remain");
        Equal(true, LocalText(fixture, LiteOptions).Contains("entityShadows:true\r\n", StringComparison.Ordinal), "untouched lite value restored");
        Equal(true, logs.Any(line => line.Contains("kept your own value for renderClouds", StringComparison.Ordinal)), "kept player value logged");
        Equal(sodiumBefore, LocalText(fixture, LiteSodium), "leaves back to the player's FANCY");

        // Lite -> full with no changes in between restores the full pack byte for byte.
        await SetModeAsync(fixture, "lite");
        await LaunchLiteAsync(fixture, jim);
        Dictionary<string, string> fullAgain = GameSnapshot(fixture.Paths);
        Equal(false, LocalExists(fixture, LiteModA), "lite again");
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        SameSnapshot(afterFull, GameSnapshot(fixture.Paths), "lite then full with nothing touched is byte-exact");
        Equal(true, fullAgain.Count > 0, "snapshot taken");
    }

    private static async Task TestLitePlayerDisabledModsAsync(string root, ConvergenceTestSigner signer)
    {
        PerformanceEnvironment jim = Machine("AMD Ryzen 7 2700 Eight-Core Processor", 833, "NVIDIA GeForce RTX 3050");
        LiteFixture fixture = NewLiteFixture(root);
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);
        // Jim had already switched one lite mod off in Prism before lite existed, and switched off a non-lite mod.
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        string modB = PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteModB);
        string kept = PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, KeptMod);
        File.Move(modB, modB + ".disabled");
        File.Move(kept, kept + ".disabled");
        await SetModeAsync(fixture, "auto");
        List<string> logs = await LaunchLiteAsync(fixture, jim);
        Equal(false, logs.Any(line => line == "Repair needed: " + LiteModB), "an exact disabled copy of a lite mod is adopted, not downloaded");
        Equal(false, File.Exists(modB), "adopted lite mod stays off");
        Equal(true, File.Exists(kept + ".disabled"), "a player-disabled copy of another mod is never deleted");
        Equal(true, File.Exists(kept), "a managed non-lite mod is still repaired as in 1.2.21");
        // Back to full: lite turns back on only what it switched off or adopted; the player's other copy stays.
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        Equal(true, File.Exists(modB), "lite mod back on");
        Equal(true, File.Exists(kept + ".disabled"), "the player's own disabled file is still there after full");
    }

    private static async Task TestLiteReleaseChangesAsync(string root, ConvergenceTestSigner signer)
    {
        PerformanceEnvironment jim = Machine("AMD Ryzen 7 2700 Eight-Core Processor", 833, "NVIDIA GeForce RTX 3050");
        LiteFixture fixture = NewLiteFixture(root);
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);
        await LaunchLiteAsync(fixture, jim);
        Equal(false, LocalExists(fixture, LiteModA), "lite applied at 1.0.61");

        // 1.0.62 updates a lite mod, retires the other, and ships a new Default profile revision.
        byte[] oldA = fixture.Managed[LiteModA];
        fixture.Managed[LiteModA] = ConvergenceFabricJar("atmospherics", "2.6.7");
        fixture.Managed.Remove(LiteModB);
        fixture.Managed[LiteDefaultProfile] = Encoding.UTF8.GetBytes(ProfileText("Default",
            ["vinery:bushy_leaves", "file/Connected-Bricks 1.21-1.21.3 v3.1.zip", "file/New Official.zip", "fabric", "vanilla"]));
        LitePerformanceProfile next = FixtureLite();
        next.DisabledMods = [LiteModA];
        await PublishLiteAsync(fixture, "1.0.62", next, signer);
        await LaunchLiteAsync(fixture, jim);
        Equal(false, LocalExists(fixture, LiteModA), "the updated lite mod stays off");
        Equal(HashBytes(fixture.Managed[LiteModA]), HashBytes(File.ReadAllBytes(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteModA + ".disabled"))),
            "its disabled copy is the NEW signed version");
        Equal(false, GameSnapshot(fixture.Paths).ContainsValue(HashBytes(oldA)), "the old version is gone");
        Equal(false, LocalExists(fixture, LiteModB + ".disabled") || LocalExists(fixture, LiteModB), "a retired lite mod leaves nothing behind");
        Equal(ProfileText("Default", ["vinery:bushy_leaves", "file/New Official.zip", "fabric", "vanilla"]), LocalText(fixture, LiteDefaultProfile),
            "a new profile revision is filtered again");

        // Jim turns the lite mod back on by hand in Prism: lite leaves it on.
        string a = PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteModA);
        File.Move(a + ".disabled", a);
        List<string> logs = await LaunchLiteAsync(fixture, jim);
        Equal(true, File.Exists(a), "a mod the player turned back on stays on");
        Equal(true, logs.Any(line => line.Contains("turned back on by hand", StringComparison.Ordinal)), "that choice is logged");
        await LaunchLiteAsync(fixture, jim);
        Equal(true, File.Exists(a), "and stays on next launch too");
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        Equal(true, File.Exists(a) && !File.Exists(a + ".disabled"), "full keeps it on");
        Equal(Encoding.UTF8.GetString(fixture.Managed[LiteDefaultProfile]), LocalText(fixture, LiteDefaultProfile), "new revision restored byte-exact");

        // A release without a lite list undoes lite everywhere.
        await SetModeAsync(fixture, "lite");
        await LaunchLiteAsync(fixture, jim);
        Equal(false, File.Exists(a), "lite again");
        await PublishLiteAsync(fixture, "1.0.63", null, signer);
        List<string> removedLogs = await LaunchLiteAsync(fixture, jim);
        Equal(true, File.Exists(a), "no lite list in the release = full pack");
        Equal(true, removedLogs.Any(line => line.Contains("this release has no lite list", StringComparison.Ordinal)), "logged");
    }

    private static async Task TestLiteCrashRecoveryAsync(string root, ConvergenceTestSigner signer)
    {
        PerformanceEnvironment jim = Machine("AMD Ryzen 7 2700 Eight-Core Processor", 833, "NVIDIA GeForce RTX 3050");
        LiteFixture fixture = NewLiteFixture(root);
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        Dictionary<string, string> full = GameSnapshot(fixture.Paths);
        await SetModeAsync(fixture, "auto");

        // An I/O error in the middle of the switch: rolled back at once, Minecraft starts with the full pack.
        int commits = 0;
        TransactionStore.CopyStageHookForTests = stage =>
        {
            if (stage == TransactionCopyStage.Committed && ++commits == 4) throw new IOException("simulated disk error");
        };
        List<string> logs;
        try { logs = await LaunchLiteAsync(fixture, jim); }
        finally { TransactionStore.CopyStageHookForTests = null; }
        Equal(true, logs.Any(line => line.Contains("mode switch could not finish (simulated disk error)", StringComparison.Ordinal)), "failure logged, launch continues");
        SameSnapshot(full, GameSnapshot(fixture.Paths), "a failed switch leaves the pack exactly as it was");

        // The process dies mid-switch: the next launch recovers the journal first, then switches cleanly.
        commits = 0;
        UpdateEngine.LeaveJournalOnFailureForTests = true;
        TransactionStore.CopyStageHookForTests = stage =>
        {
            if (stage == TransactionCopyStage.Committed && ++commits == 5) throw new InvalidOperationException("simulated power loss");
        };
        try
        {
            await ThrowsAsync<InvalidOperationException>(() => new UpdateEngine(fixture.Paths, fixture.Configuration, _ => { },
                verifiedCatalog: fixture.Catalog, performanceEnvironment: jim).CheckAndUpdateAsync([], false, NoCancellation));
        }
        finally
        {
            TransactionStore.CopyStageHookForTests = null;
            UpdateEngine.LeaveJournalOnFailureForTests = false;
        }
        Equal(true, File.Exists(TransactionStore.JournalPath(fixture.Paths)), "the interrupted switch left its journal");
        logs = await LaunchLiteAsync(fixture, jim);
        Equal(true, logs.Any(line => line.StartsWith("Recovering an interrupted local update transaction", StringComparison.Ordinal)), "recovered first");
        Equal(false, LocalExists(fixture, LiteModA) || LocalExists(fixture, LiteModB), "then lite applied cleanly");
        Equal(true, LocalExists(fixture, LiteModA + ".disabled") && LocalExists(fixture, LiteModB + ".disabled"), "both disabled copies present");
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        SameSnapshot(full, GameSnapshot(fixture.Paths), "and full after a recovered crash is still byte-exact");
    }

    private static async Task TestLiteMissingSettingFileAsync(string root, ConvergenceTestSigner signer)
    {
        PerformanceEnvironment jim = Machine("AMD Ryzen 7 2700 Eight-Core Processor", 833, "NVIDIA GeForce RTX 3050");
        LiteFixture fixture = NewLiteFixture(root);
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, jim);
        string sodium = PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteSodium);
        byte[] sodiumBytes = File.ReadAllBytes(sodium);
        File.Delete(sodium); // the player deleted it; the seed stays player-owned (not offered again)
        await SetModeAsync(fixture, "auto");
        List<string> logs = await LaunchLiteAsync(fixture, jim);
        Equal(false, File.Exists(sodium), "a missing setting file is not created by lite");
        Equal(true, logs.Any(line => line.Contains("config/sodium-options.json does not exist yet", StringComparison.Ordinal)), "missing file logged");
        Equal(false, LocalExists(fixture, LiteModA), "the rest of lite still applies");
        // Sodium writes the file again on its next start: lite sets leaves once.
        await File.WriteAllBytesAsync(sodium, sodiumBytes);
        await LaunchLiteAsync(fixture, jim);
        Equal(Encoding.UTF8.GetString(sodiumBytes).Replace("\"FANCY\"", "\"FAST\""), Encoding.UTF8.GetString(File.ReadAllBytes(sodium)),
            "applied once the file exists");
        Equal(true, LocalText(fixture, LiteXaero).Contains("biome_blending = false", StringComparison.Ordinal), "xaero applied");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static PerformanceDetectionRules LiteRules() => new()
    {
        CpuScoreThreshold = 1050,
        FullGpuPatterns = ["RTX 3080", "RTX 2070", "RX 6600 XT"],
        LiteGpuPatterns = ["RTX 3050", "UHD GRAPHICS", "RADEON ###M"]
    };

    private static GpuAdapterInfo Gpu(string name) => new() { Name = name };

    private static UpdaterPaths PathsWithLocal(string root, string local)
    {
        UpdaterPaths paths = Paths(root);
        return paths with { LocalDataDirectory = local };
    }

    private static PerformanceEnvironment Machine(string cpu, double score, params string[] gpus) => new()
    {
        ReadCpuName = () => cpu,
        ReadGpus = () => gpus.Select(Gpu).ToList(),
        MeasureCpu = _ => new CpuProbeResult(score, score * 6.5, 950)
    };

    private static string ProfileText(string name, IEnumerable<string> ids) =>
        "{\n  \"locked\": false,\n  \"name\": \"" + name + "\",\n  \"overrides\": {},\n  \"packIds\": [\n    "
        + string.Join(",\n    ", ids.Select(id => "\"" + id + "\"")) + "\n  ]\n}\n";
}
