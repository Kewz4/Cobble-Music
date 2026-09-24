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
        TestHardwareDbVectors();
        TestHardwareDecisions();
        TestSettingTextEdits();
        TestPackProfileText();
        TestPerformanceManifestValidation();
        TestStubNeverDisabled();
        TestLiteModGuard(Path.Combine(root, "mod-guard"));
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
        await TestLiteRefusedModListAsync(Path.Combine(root, "refused-mod-list"), signer);
        await TestLiteDeletedProfileAsync(Path.Combine(root, "deleted-profile"), signer);
        Console.WriteLine("Lite mode checks passed: allow-lists and shader rejection, 1.2.22 floor, 246 hwdb vectors, table decisions for Jim/DONGLORD9000/Kewz "
            + "from real strings, real-hardware filter, unknowns do not vote, lines from the manifest, re-decide only on change, override file both ways, "
            + "join fix never disabled, mod list dependency/critical/library refusal, FULL PCs untouched, no re-download of switched-off mods, "
            + "player-disabled mods kept, exact restore on full, release changes while lite, crash recovery, deleted profile refiltered.");
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
        Validate(m => m.PerformanceProfiles!.Lite!.Detection.CpuSingleThreadBelow = 0, false, "the processor line must be set");
        Validate(m => m.PerformanceProfiles!.Lite!.Detection.GpuScoreBelow = 0, false, "the graphics line must be set");
        Validate(m => m.PerformanceProfiles!.Lite!.Detection.CpuSingleThreadBelow = 100_001, false, "the processor line has a ceiling");
        Validate(m => m.PerformanceProfiles!.Lite!.Detection = new PerformanceDetectionRules { CpuSingleThreadBelow = 2700, GpuScoreBelow = 14000 }, true,
            "other lines are fine");
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
                    Detection = RecommendedLines,
                    DisabledMods = [LiteModA],
                    RemovedPackIds = ["file/sunbathing-v1.10.zip"],
                    Settings = [new PerformanceSetting { Path = LiteSodium, Format = "json", Key = "quality.leaves_quality", Value = "\"FAST\"" }]
                }
            }
        };
        mutate(manifest);
        var urls = new Dictionary<string, Uri> { ["payload.part001"] = new("https://example.invalid/payload.part001") };
        bool valid = true;
        string message = "";
        try { ManifestParser.Validate(manifest, new UpdaterConfiguration { AllowedRoots = ["mods", "config"] }, urls); }
        catch (InvalidDataException exception) { valid = false; message = exception.Message; }
        Equal(expectValid, valid, context + (message.Length > 0 ? " (" + message + ")" : ""));
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

    // ---- round 2: built-in hardware table (hwdb-norm-1) -----------------------------------------------------------

    // The Python reference (lite0924/hwdb/hwdb_norm.py) resolved all 246 hwdb test vectors and passed them
    // (tests/run_tests.py 246/246); Fixtures/Hwdb/vectors_expected.tsv holds its full result per vector
    // (build2/tools/gen_vector_expectations.py). The port must give exactly the same status, key, score, rule and
    // ambiguity for every one of them.
    private static void TestHardwareDbVectors()
    {
        HardwareDb db = HardwareDb.Embedded;
        Equal(4382, db.CpuCount, "embedded processor table size");
        Equal(2769, db.GpuCount, "embedded graphics table size");
        Equal(true, db.Version.StartsWith("hwdb-norm-1/2026-09-24/", StringComparison.Ordinal), "table version names the normaliser: " + db.Version);
        int rows = 0, cpuRows = 0, gpuRows = 0;
        foreach (string line in HwdbFixture("vectors_expected.tsv").Split('\n'))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] cells = line.Split('\t');
            Equal(11, cells.Length, "fixture row shape: " + line);
            string? Cell(int index) => cells[index] == "-" ? null : cells[index];
            HardwareMatch actual = cells[0] == "cpu"
                ? db.ResolveCpu(cells[1])
                : db.ResolveGpu(cells[1], Cell(2), Cell(3) is string gib ? long.Parse(gib, System.Globalization.CultureInfo.InvariantCulture) << 30 : null, Cell(4));
            string status = actual.Status.ToString().ToLowerInvariant();
            string got = $"{status}|{actual.Key ?? "-"}|{actual.Score?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}|{actual.Rule}|{(actual.Ambiguous ? "true" : "false")}";
            string want = $"{cells[5]}|{cells[6]}|{cells[7]}|{cells[8]}|{cells[9]}";
            Equal(want, got, $"hwdb vector [{cells[10]}] {cells[0]} '{cells[1]}' dev={cells[2]} mem={cells[3]}");
            rows++;
            if (cells[0] == "cpu") cpuRows++; else gpuRows++;
        }
        Equal(246, rows, "every hwdb vector ran");
        Equal(118, cpuRows, "processor vectors");
        Equal(128, gpuRows, "graphics vectors");

        // The two raw vector files ship next to the expectations, unchanged from lite0924/hwdb/tests.
        Equal(118, HwdbFixture("cpu_vectors.tsv").Split('\n').Count(line => line.Length > 0 && !line.StartsWith('#')), "cpu_vectors.tsv rows");
        Equal(128, HwdbFixture("gpu_vectors.tsv").Split('\n').Count(line => line.Length > 0 && !line.StartsWith('#')), "gpu_vectors.tsv rows");

        // Normaliser details the spec calls out.
        Equal("core i7-10750h", HardwareNames.CpuKey("Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz"), "clock after @ and filler words go");
        Equal("ryzen 7 2700", HardwareNames.CpuKey("AMD Ryzen 7 2700 Eight-Core Processor"), "core count goes");
        Equal("core i5-750", HardwareNames.CpuKey("Intel(R) Core(TM) i5 CPU         750  @ 2.67GHz"), "first-generation Core i gets its hyphen");
        Equal("geforce rtx 3050 laptop", HardwareNames.GpuKey("NVIDIA GeForce RTX 3050 Laptop GPU"), "laptop GPU");
        Equal("core i7-10750h", HardwareNames.CpuKey("Intel（R） Core(TM) i7－10750H CPU @ 2.60GHz"), "NFKC folds full-width characters");
        // Same as the reference: NFKC turns a bare trade-mark sign into "TM" before step 2 could delete it. Windows
        // writes "(TM)", so real names are not affected; the port keeps the reference's order on purpose.
        Equal("coretm i7-10750h", HardwareNames.CpuKey("Intel（R） Core™ i7-10750H CPU @ 2.60GHz"), "reference quirk kept");

        // Updater-only rule: vendor 1022 (AMD's processor vendor id) counts as AMD graphics hardware.
        HardwareMatch vendor1022 = db.ResolveGpu("AMD Radeon(TM) Graphics", @"PCI\VEN_1022&DEV_1638", null, "AMD Ryzen 7 5800H with Radeon Graphics");
        HardwareMatch vendor1002 = db.ResolveGpu("AMD Radeon(TM) Graphics", @"PCI\VEN_1002&DEV_1638", null, "AMD Ryzen 7 5800H with Radeon Graphics");
        Equal(HardwareMatchStatus.Known, vendor1022.Status, "vendor 1022 is AMD graphics");
        Equal(vendor1002.Key, vendor1022.Key, "1022 resolves like 1002");
    }

    private static string HwdbFixture(string name)
    {
        using Stream stream = typeof(Program).Assembly.GetManifestResourceStream("HwdbFixtures." + name)
            ?? throw new InvalidOperationException("missing test fixture " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static PerformanceDetectionRules RecommendedLines => new() { CpuSingleThreadBelow = 2500, GpuScoreBelow = 13000 };

    // Jim (Pastel_Crows) and DONGLORD9000: the task's strings. Their MatchingDeviceIds are not known; the desktop cards'
    // own PCI ids are used (RTX 3050 8 GB: 2507 GA106 or 2582 GA107; RTX 3080: 2206, LHR 2216) and each is tested.
    private static GpuAdapterInfo Adapter(string name, string deviceId, long memoryBytes = 0, string key = "0000", bool? present = true) =>
        new() { Name = name, DeviceId = deviceId, MemoryBytes = memoryBytes, RegistryKey = key, Present = present };

    private const string JimCpu = "AMD Ryzen 7 2700 Eight-Core Processor";
    private const string DonCpu = "AMD Ryzen 7 5800X 8-Core Processor";
    private const string KewzCpu = "Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz";
    private static GpuAdapterInfo JimGpu(string device = "2507") => Adapter("NVIDIA GeForce RTX 3050", @"pci\ven_10de&dev_" + device, 8192L << 20);
    private static GpuAdapterInfo DonGpu(string device = "2206") => Adapter("NVIDIA GeForce RTX 3080", @"pci\ven_10de&dev_" + device, 10240L << 20);
    // Kewz's PC exactly as its display-class key reads (2026-09-24): 0001 Intel UHD (MemorySize 1 GiB), 0002 RTX 2070 Super.
    private static GpuAdapterInfo[] KewzGpus() =>
    [
        Adapter("Intel(R) UHD Graphics", @"PCI\VEN_8086&DEV_9BC4&SUBSYS_12B41462", 1L << 30, "0001"),
        Adapter("NVIDIA GeForce RTX 2070 Super", @"pci\ven_10de&dev_1e91&subsys_12b41462", 8589934592, "0002")
    ];

    private static void TestHardwareDecisions()
    {
        HardwareDb db = HardwareDb.Embedded;
        HardwareDecision Decide(string cpu, PerformanceDetectionRules? rules = null, params GpuAdapterInfo[] gpus) =>
            HardwareVerdict.Decide(cpu, gpus, db, rules ?? RecommendedLines);

        // Jim: LITE on both votes, whichever RTX 3050 8 GB chip he has.
        foreach (string device in new[] { "2507", "2582" })
        {
            HardwareDecision jim = Decide(JimCpu, null, JimGpu(device));
            Equal(PerformanceMode.Lite, jim.Mode, "Jim is LITE (device " + device + ")");
            Equal(2170, jim.Cpu.Score, "Ryzen 7 2700 single-thread rating");
            Equal("geforce rtx 3050 8gb", jim.BestGpu?.Match.Key, "8 GiB picks the RTX 3050 8GB row (device " + device + ")");
            Equal(12459, jim.BestGpu?.Match.Score, "RTX 3050 8GB G3D Mark");
            Equal(true, jim.CpuLite && jim.GpuLite, "both votes say lite");
        }
        // DONGLORD9000: FULL on both votes, 10 GB or LHR card.
        foreach (string device in new[] { "2206", "2216" })
        {
            HardwareDecision don = Decide(DonCpu, null, DonGpu(device));
            Equal(PerformanceMode.Full, don.Mode, "DONGLORD9000 is FULL (device " + device + ")");
            Equal(3448, don.Cpu.Score, "Ryzen 7 5800X single-thread rating");
            Equal(24978, don.BestGpu?.Match.Score, "RTX 3080 G3D Mark");
            Equal(false, don.CpuLite || don.GpuLite, "neither vote says lite");
        }
        // Kewz's laptop: FULL; the RTX 2070 Super is read as the laptop (Max-Q) chip by its device id 1E91.
        HardwareDecision kewz = Decide(KewzCpu, null, KewzGpus());
        Equal(PerformanceMode.Full, kewz.Mode, "Kewz's PC is FULL at 2500/13000");
        Equal(2605, kewz.Cpu.Score, "i7-10750H single-thread rating");
        Equal("geforce rtx 2070 super max-q", kewz.BestGpu?.Match.Key, "best real adapter is the laptop 2070 Super");
        Equal(13206, kewz.BestGpu?.Match.Score, "RTX 2070 Super Max-Q G3D Mark");
        Equal(2, kewz.Adapters.Count(item => item.Real), "Intel UHD and the NVIDIA card are both real");

        // Lines come from the rules (the signed manifest), not from the code.
        Equal(PerformanceMode.Lite, Decide(KewzCpu, new() { CpuSingleThreadBelow = 2700, GpuScoreBelow = 13000 }, KewzGpus()).Mode, "processor line 2700 makes Kewz's PC lite");
        Equal(PerformanceMode.Lite, Decide(KewzCpu, new() { CpuSingleThreadBelow = 2500, GpuScoreBelow = 14000 }, KewzGpus()).Mode, "graphics line 14000 makes Kewz's PC lite");
        Equal(PerformanceMode.Full, Decide(JimCpu, new() { CpuSingleThreadBelow = 2000, GpuScoreBelow = 12000 }, JimGpu()).Mode, "low enough lines make Jim full");

        // Only real hardware counts: leftovers, virtual/remote/indirect/USB adapters and non-GPU vendors never vote and
        // never cancel a vote.
        HardwareDecision noisyJim = Decide(JimCpu, null,
            JimGpu(),
            Adapter("NVIDIA GeForce GTX 1080 Ti", @"pci\ven_10de&dev_1b06", 11L << 30, "0001", present: false),
            Adapter("spacedesk Graphics Adapter", @"ROOT\spacedesk", 0, "0002"),
            Adapter("Microsoft Basic Display Adapter", @"PCI\CC_0300", 0, "0003"),
            Adapter("Virtual Desktop Monitor", @"ROOT\VirtualDesktopMonitor", 0, "0004"),
            Adapter("ASPEED Graphics Family(WDDM)", @"PCI\VEN_1A03&DEV_2000", 0, "0005"),
            Adapter("NVIDIA GeForce RTX 4090", "", 24L << 30, "0006"));
        Equal(PerformanceMode.Lite, noisyJim.Mode, "Jim stays LITE with leftover, virtual and non-GPU entries around");
        Equal(1, noisyJim.Adapters.Count(item => item.Real), "only the RTX 3050 is real");
        Equal(true, noisyJim.Adapters.Single(item => item.Adapter.RegistryKey == "0001").NotRealReason.StartsWith("not present", StringComparison.Ordinal),
            "a removed card's leftover entry is not present");
        Equal(true, noisyJim.Adapters.Single(item => item.Adapter.RegistryKey == "0005").NotRealReason.Contains("1a03", StringComparison.Ordinal),
            "ASPEED (1A03) is not a graphics vendor");
        Equal("no PCI hardware id", noisyJim.Adapters.Single(item => item.Adapter.RegistryKey == "0006").NotRealReason, "no PCI id = not proven real");
        // The hardware id of the present device stands in for a missing MatchingDeviceId.
        GpuAdapterInfo byHardwareId = Adapter("NVIDIA GeForce RTX 3080", "", 10240L << 20);
        byHardwareId.HardwareId = @"PCI\VEN_10DE&DEV_2206&SUBSYS_38901462&REV_A1";
        Equal(PerformanceMode.Full, Decide(DonCpu, null, byHardwareId).Mode, "hardware id counts as the PCI id");
        Equal(true, Decide("", null, byHardwareId).Adapters.Single().Real, "an adapter known only by its hardware id is real");
        // Presence unknown (SetupAPI unavailable): the entry is used as before.
        Equal(PerformanceMode.Lite, Decide("", null, Adapter("NVIDIA GeForce RTX 3050", @"pci\ven_10de&dev_2507", 8192L << 20, present: null)).Mode,
            "unknown presence does not drop a real card");

        // Unknowns do not vote.
        Equal(PerformanceMode.Full, Decide("Future Processor 9000", null, DonGpu()).Mode, "unknown processor + fast card = full");
        Equal(PerformanceMode.Lite, Decide("Future Processor 9000", null, JimGpu()).Mode, "unknown processor: the graphics card still votes");
        Equal(PerformanceMode.Lite, Decide(JimCpu, null, Adapter("NVIDIA GeForce RTX 9999", @"PCI\VEN_10DE&DEV_FFFF", 8L << 30)).Mode,
            "unknown graphics card: the processor still votes");
        HardwareDecision unknownDiscrete = Decide("Future Processor 9000", null,
            Adapter("Intel(R) UHD Graphics", @"PCI\VEN_8086&DEV_9BC4", 1L << 30, "0001"),
            Adapter("NVIDIA GeForce RTX 9999", @"PCI\VEN_10DE&DEV_FFFF", 8L << 30, "0002"));
        Equal(PerformanceMode.Full, unknownDiscrete.Mode, "a known built-in GPU never votes for an unknown discrete card");
        Equal(false, unknownDiscrete.GpuVotes, "graphics do not vote while a real card is unknown");
        HardwareDecision nothing = Decide("", null);
        Equal(PerformanceMode.Full, nothing.Mode, "nothing known = full");
        Equal("nothing could be looked up", nothing.Reason, "and it says so");

        // Fingerprint = processor + REAL cards only: a virtual adapter appearing does not re-decide.
        Equal(Decide(JimCpu, null, JimGpu()).Fingerprint, noisyJim.Fingerprint, "ignored adapters are not in the fingerprint");
        Equal(false, Decide(JimCpu, null, JimGpu()).Fingerprint == Decide(JimCpu, null, JimGpu("2584")).Fingerprint, "another card changes it");

        // Plain words for the mode file, all facts in the log line.
        Equal("processor AMD Ryzen 7 2700 Eight-Core Processor, speed score 2170 (lite below 2500); best graphics card NVIDIA GeForce RTX 3050, "
            + "speed score 12459 (lite below 13000). Picked: lite.", HardwareVerdict.StatusText(Decide(JimCpu, null, JimGpu()), PerformanceMode.Lite),
            "Jim's status line");
        Equal("processor Intel Core i7-10750H CPU @ 2.60GHz, speed score 2605 (lite below 2500); best graphics card NVIDIA GeForce RTX 2070 Super, "
            + "speed score 13206 as a laptop chip (lite below 13000). Picked: full.", HardwareVerdict.StatusText(kewz, PerformanceMode.Full), "Kewz's status line");
        string log = HardwareVerdict.LogLine(noisyJim, "decided now (test)");
        foreach (string fact in new[] { "\"AMD Ryzen 7 2700 Eight-Core Processor\" = \"ryzen 7 2700\" 2170", "(lite below 2500)",
            "\"NVIDIA GeForce RTX 3050\" [10de:2507, 8 GiB] = \"geforce rtx 3050 8gb\" 12459 (memory-variant)", "best 12459 (lite below 13000)",
            "ignored \"NVIDIA GeForce GTX 1080 Ti\" (not present", "\"spacedesk Graphics Adapter\" (virtual", "table hwdb-norm-1/",
            "result LITE (processor and graphics card are below their lines)", "decided now (test)" })
        {
            Equal(true, log.Contains(fact, StringComparison.Ordinal), $"log line has '{fact}': {log}");
        }
    }

    private static async Task TestPerformanceDetectionAsync(string root)
    {
        UpdateManifest Lines(int cpu, int gpu) => new()
        {
            PerformanceProfiles = new PerformanceProfiles
            {
                Lite = new LitePerformanceProfile { Detection = new PerformanceDetectionRules { CpuSingleThreadBelow = cpu, GpuScoreBelow = gpu } }
            }
        };
        UpdateManifest recommended = Lines(2500, 13000);

        async Task<(PerformancePlan Plan, List<string> Logs)> DecideAsync(string name, PerformanceEnvironment environment, UpdateManifest? manifest = null,
            bool checkOnly = false)
        {
            UpdaterPaths paths = Paths(Path.Combine(root, name));
            Directory.CreateDirectory(paths.MinecraftDirectory);
            var logs = new List<string>();
            var engine = new UpdateEngine(paths, Configuration(), logs.Add, performanceEnvironment: environment);
            return (await engine.PreparePerformancePlanAsync(manifest ?? recommended, checkOnly, NoCancellation), logs);
        }

        (PerformancePlan jim, List<string> jimLogs) = await DecideAsync("jim", JimPc());
        Equal(PerformanceMode.Lite, jim.Mode, "Jim is lite");
        string line = jimLogs.Single(entry => entry.StartsWith("Performance check:", StringComparison.Ordinal));
        Equal(true, line.Contains("result LITE (processor and graphics card are below their lines); decided now (first decision on this computer)", StringComparison.Ordinal),
            "one plain log line: " + line);
        (PerformancePlan don, _) = await DecideAsync("donglord", DonPc());
        Equal(PerformanceMode.Full, don.Mode, "DONGLORD9000 is full");
        (PerformancePlan kewz, List<string> kewzLogs) = await DecideAsync("kewz", KewzPc());
        Equal(PerformanceMode.Full, kewz.Mode, "Kewz's PC is full at the recommended lines");
        Equal(true, kewzLogs.Any(entry => entry.Contains("\"geforce rtx 2070 super max-q\" 13206 (nvidia-mobile-by-device-id, ambiguous)", StringComparison.Ordinal)),
            "the laptop rule is visible in the log");

        // The lines come from the signed manifest: the same PC and table give another verdict with other lines, and the
        // stored pick is re-decided because the lines changed.
        (PerformancePlan kewz2700, List<string> kewz2700Logs) = await DecideAsync("kewz", KewzPc(), Lines(2700, 13000));
        Equal(PerformanceMode.Lite, kewz2700.Mode, "processor line 2700 from the manifest makes Kewz's PC lite");
        Equal(true, kewz2700Logs.Any(entry => entry.Contains("decided now (the lines in this release changed)", StringComparison.Ordinal)), "re-decided on new lines");
        Equal(PerformanceMode.Lite, (await DecideAsync("kewz-gpu-line", KewzPc(), Lines(2500, 14000))).Plan.Mode, "graphics line 14000 from the manifest");

        // Round-2 verifier FIX 2: an ill-formed hardware name (lone surrogate) must not stop the launch.
        var brokenName = new PerformanceEnvironment { ReadCpuName = () => "AMD Ryzen 7 2700\uD800 Eight-Core Processor", ReadGpus = () => [JimGpu()] };
        (PerformancePlan illFormed, List<string> illFormedLogs) = await DecideAsync("broken-name", brokenName);
        Equal(PerformanceMode.Full, illFormed.Mode, "a failed hardware check gives the full pack for that launch");
        Equal(true, illFormedLogs.Any(entry => entry.StartsWith("Performance check: failed (", StringComparison.Ordinal)), "the failure is logged: " + string.Join(" | ", illFormedLogs));

        // Stored pick: kept while nothing changed, re-decided when the hardware or the table changes.
        UpdaterPaths once = Paths(Path.Combine(root, "once"));
        Directory.CreateDirectory(once.MinecraftDirectory);
        string cpuName = JimCpu;
        GpuAdapterInfo[] gpus = [JimGpu()];
        var changing = new PerformanceEnvironment { ReadCpuName = () => cpuName, ReadGpus = () => gpus };
        async Task<(PerformanceMode Mode, List<string> Logs)> LaunchAsync(PerformanceEnvironment environment, bool checkOnly = false)
        {
            var logs = new List<string>();
            PerformancePlan plan = await new UpdateEngine(once, Configuration(), logs.Add, performanceEnvironment: environment)
                .PreparePerformancePlanAsync(recommended, checkOnly, NoCancellation);
            return (plan.Mode, logs);
        }
        await LaunchAsync(changing);
        MachinePerformanceRecord stored = MachinePerformanceStore.Load(MachinePerformanceStore.PathFor(once))!;
        Equal("lite", stored.Verdict, "verdict stored");
        Equal(2170, stored.CpuScore, "processor score stored");
        Equal("ryzen 7 2700", stored.CpuKey, "processor key stored");
        Equal("geforce rtx 3050 8gb", stored.Gpus.Single().TableKey, "graphics key stored");
        Equal(HardwareDb.Embedded.Version, stored.DatabaseVersion, "table version stored");
        (_, List<string> keptLogs) = await LaunchAsync(changing);
        Equal(true, keptLogs.Single().Contains("kept from ", StringComparison.Ordinal), "same inputs keep the stored pick");
        // Tamper test: with the inputs unchanged, the STORED verdict is what counts (re-decision only on a change).
        stored.Verdict = "full";
        MachinePerformanceStore.Save(MachinePerformanceStore.PathFor(once), stored, _ => { });
        Equal(PerformanceMode.Full, (await LaunchAsync(changing)).Mode, "the stored pick is used while nothing changed");
        gpus = [DonGpu()];
        (PerformanceMode upgraded, List<string> upgradeLogs) = await LaunchAsync(changing);
        Equal(PerformanceMode.Lite, upgraded, "new graphics card: re-decided (the 2700 still votes lite)");
        Equal(true, upgradeLogs.Single().Contains("decided now (the hardware changed)", StringComparison.Ordinal), "hardware change logged");
        cpuName = DonCpu;
        Equal(PerformanceMode.Full, (await LaunchAsync(changing)).Mode, "new processor too: full");
        // A new built-in table re-decides as well.
        byte[] cpuTable = UpdaterResource("HardwareDb.cpu_scores.json");
        byte[] gpuTable = UpdaterResource("HardwareDb.gpu_scores.json");
        byte[] editedCpu = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(cpuTable).Replace("\"ryzen 7 5800x\":3448", "\"ryzen 7 5800x\":2400", StringComparison.Ordinal));
        Equal(false, editedCpu.AsSpan().SequenceEqual(cpuTable), "the table copy was edited");
        HardwareDb editedDb = HardwareDb.Load(editedCpu, gpuTable);
        (PerformanceMode newTable, List<string> tableLogs) = await LaunchAsync(new PerformanceEnvironment
        {
            ReadCpuName = () => cpuName, ReadGpus = () => gpus, Database = () => editedDb
        });
        Equal(PerformanceMode.Lite, newTable, "a new table (5800X at 2400) re-decides");
        Equal(true, tableLogs.Single().Contains("decided now (the built-in table changed)", StringComparison.Ordinal), "table change logged");
        // check-only never saves.
        string before = File.ReadAllText(MachinePerformanceStore.PathFor(once));
        await LaunchAsync(DonPc(), checkOnly: true);
        Equal(before, File.ReadAllText(MachinePerformanceStore.PathFor(once)), "check-only never saves a decision");

        // A launch where the hardware cannot be read keeps the stored pick and saves nothing.
        var broken = new PerformanceEnvironment
        {
            ReadCpuName = () => throw new UnauthorizedAccessException("denied"),
            ReadGpus = () => throw new System.Security.SecurityException("registry denied")
        };
        (PerformanceMode kept, List<string> brokenLogs) = await LaunchAsync(broken);
        Equal(PerformanceMode.Lite, kept, "read errors keep the stored pick");
        Equal(true, brokenLogs.Single().Contains("could not read the processor name (UnauthorizedAccessException: denied) and the graphics cards", StringComparison.Ordinal),
            "the read errors are logged");
        Equal(before, File.ReadAllText(MachinePerformanceStore.PathFor(once)), "nothing saved after a read error");
        (PerformancePlan fresh, List<string> freshLogs) = await DecideAsync("broken-first", broken);
        Equal(PerformanceMode.Full, fresh.Mode, "nothing readable and nothing stored = full");
        Equal(false, File.Exists(MachinePerformanceStore.PathFor(Paths(Path.Combine(root, "broken-first")))), "and nothing is saved");
        Equal(true, freshLogs.Single().Contains("result FULL (nothing could be looked up)", StringComparison.Ordinal), "reason logged");
        (PerformancePlan gpuOnlyBroken, _) = await DecideAsync("gpu-broken", new PerformanceEnvironment
        {
            ReadCpuName = () => JimCpu,
            ReadGpus = () => throw new IOException("no registry")
        });
        Equal(PerformanceMode.Lite, gpuOnlyBroken.Mode, "the processor still votes when the graphics cards cannot be read");

        // No lite list in the release: no check at all.
        UpdaterPaths none = Paths(Path.Combine(root, "no-lite"));
        Directory.CreateDirectory(none.MinecraftDirectory);
        int reads = 0;
        PerformancePlan nonePlan = await new UpdateEngine(none, Configuration(), _ => { }, performanceEnvironment: new PerformanceEnvironment
        {
            ReadCpuName = () => { reads++; return "x"; }, ReadGpus = () => { reads++; return []; }
        }).PreparePerformancePlanAsync(new UpdateManifest(), false, NoCancellation);
        Equal(PerformanceMode.Full, nonePlan.Mode, "no lite list = full");
        Equal(0, reads, "no lite list = no hardware read");
        Equal(false, File.Exists(PerformanceModeFile.PathFor(none)), "no lite list = no mode file");
    }

    private static async Task TestPerformanceModeFileAsync(string root)
    {
        var lite = new UpdateManifest { PerformanceProfiles = new PerformanceProfiles { Lite = new LitePerformanceProfile { Detection = RecommendedLines } } };
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        int reads = 0;
        var fast = new PerformanceEnvironment
        {
            ReadCpuName = () => { reads++; return DonCpu; },
            ReadGpus = () => [DonGpu()]
        };
        async Task<PerformanceMode> ModeAsync(List<string>? logs = null) =>
            (await new UpdateEngine(paths, Configuration(), line => logs?.Add(line), performanceEnvironment: fast)
                .PreparePerformancePlanAsync(lite, false, NoCancellation)).Mode;

        Equal(PerformanceMode.Full, await ModeAsync(), "auto on DONGLORD9000's PC is full");
        string file = PerformanceModeFile.PathFor(paths);
        string created = await File.ReadAllTextAsync(file);
        Equal(true, created.Contains("\r\nmode=auto\r\n", StringComparison.Ordinal), "the file is created with mode=auto");
        Equal(true, created.Contains("# This computer: processor AMD Ryzen 7 5800X 8-Core Processor, speed score 3448 (lite below 2500); "
            + "best graphics card NVIDIA GeForce RTX 3080, speed score 24978 (lite below 13000). Picked: full.", StringComparison.Ordinal),
            "the file explains the pick in plain words: " + created);
        Equal(false, created.Contains("hwdb", StringComparison.Ordinal) || created.Contains("geforce rtx", StringComparison.Ordinal),
            "no table keys or internal ids in the player's file");

        // The player forces lite; their exact line (spacing, capitals) is kept and the automatic pick is not used.
        await File.WriteAllTextAsync(file, created.Replace("mode=auto", "mode = LITE"));
        int before = reads;
        Equal(PerformanceMode.Lite, await ModeAsync(), "mode=lite forces lite on a fast PC");
        Equal(before, reads, "a forced mode skips the hardware read");
        string forced = await File.ReadAllTextAsync(file);
        Equal(true, forced.Contains("\r\nmode = LITE\r\n", StringComparison.Ordinal), "the player's mode line is untouched");
        Equal(true, forced.Contains("Picked: lite.", StringComparison.Ordinal), "the status line follows the pick");

        // mode=full on Jim's PC wins over the automatic LITE.
        UpdaterPaths jimPaths = Paths(Path.Combine(root, "jim"));
        Directory.CreateDirectory(Path.GetDirectoryName(PerformanceModeFile.PathFor(jimPaths))!);
        await File.WriteAllTextAsync(PerformanceModeFile.PathFor(jimPaths), "mode=full\n");
        Equal(PerformanceMode.Full, (await new UpdateEngine(jimPaths, Configuration(), _ => { }, performanceEnvironment: JimPc())
            .PreparePerformancePlanAsync(lite, false, NoCancellation)).Mode, "mode=full wins on a lite PC");

        await File.WriteAllTextAsync(file, "mode=turbo\n");
        var logs = new List<string>();
        Equal(PerformanceMode.Full, await ModeAsync(logs), "an unknown value means auto");
        Equal(true, logs.Any(entry => entry.Contains("mode=turbo, which is not auto, lite or full", StringComparison.Ordinal)), "the bad value is logged");
        Equal(true, (await File.ReadAllTextAsync(file)).StartsWith("mode=turbo\n# This computer:", StringComparison.Ordinal), "status appended, player text kept");
    }

    // ---- round 2: lite mod list guard (critical/library mods, dependency closure) ------------------------------

    private static byte[] ModJar(string json, Dictionary<string, byte[]>? nested = null)
    {
        var files = new Dictionary<string, byte[]> { ["fabric.mod.json"] = Encoding.UTF8.GetBytes(json) };
        foreach ((string path, byte[] bytes) in nested ?? []) files[path] = bytes;
        return ConvergenceZip(files);
    }

    private static void TestLiteModGuard(string root)
    {
        Directory.CreateDirectory(root);
        string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        byte[] libX = ModJar("{\"schemaVersion\":1,\"id\":\"libx\",\"version\":\"1\"}");
        string effects = Write("effects.jar", ModJar("{\"schemaVersion\":1,\"id\":\"effects\",\"version\":\"1\",\"description\":\"line one\nline two\","
            + "\"jars\":[{\"file\":\"META-INF/jars/libx.jar\"}],\"provides\":[\"effects_api\"]}", new() { ["META-INF/jars/libx.jar"] = libX }));
        string user = Write("user.jar", ModJar("{\"schemaVersion\":1,\"id\":\"user\",\"version\":\"1\",\"depends\":{\"effects\":\"*\",\"minecraft\":\"1.21.1\"}}"));
        string recommender = Write("recommender.jar", ModJar("{\"schemaVersion\":1,\"id\":\"rec\",\"version\":\"1\",\"recommends\":{\"effects\":\"*\"},\"suggests\":{\"effects_api\":\"*\"}}"));
        string libUser = Write("libuser.jar", ModJar("{\"schemaVersion\":1,\"id\":\"libuser\",\"version\":\"1\",\"depends\":{\"libx\":\">=1\"}} // trailing comment"));
        string otherLibHolder = Write("holder.jar", ModJar("{\"schemaVersion\":1,\"id\":\"holder\",\"version\":\"1\",\"jars\":[{\"file\":\"META-INF/jars/libx.jar\"}],}",
            new() { ["META-INF/jars/libx.jar"] = libX }));
        string apiUser = Write("apiuser.jar", ModJar("{\"schemaVersion\":1,\"id\":\"apiuser\",\"version\":\"1\",\"depends\":{\"effects_api\":\"*\"}}"));
        string alreadyBroken = Write("broken.jar", ModJar("{\"schemaVersion\":1,\"id\":\"broken\",\"version\":\"1\",\"depends\":{\"never_shipped\":\"*\"}}"));
        string stubRenamed = Write("join-fix.jar", ModJar("{\"schemaVersion\":1,\"id\":\"kewz_subtle_stub\",\"version\":\"1.0.0\",\"environment\":\"client\"}"));
        string sodium = Write("sodium.jar", ModJar("{\"schemaVersion\":1,\"id\":\"sodium\",\"version\":\"0.8.13\"}"));
        string library = Write("lib.jar", ModJar("{\"schemaVersion\":1,\"id\":\"somelib\",\"version\":\"1\",\"custom\":{\"modmenu\":{\"badges\":[\"library\"]}}}"));
        string notFabric = Write("plain.jar", ConvergenceZip(new() { ["a.txt"] = [1] }));
        (string, string) J(string path) => (Path.GetFileName(path), path);

        Equal(0, LiteModGuard.Check([J(recommender), J(alreadyBroken)], [J(effects)]).Count, "recommends/suggests do not block, an already-missing dependency is not lite's doing");
        List<string> depends = LiteModGuard.Check([J(user)], [J(effects)]);
        Equal(1, depends.Count, "an enabled mod depending on an off mod refuses the list");
        Equal(true, depends[0].Contains("user.jar (user) depends on effects, which only effects.jar provides", StringComparison.Ordinal), depends[0]);
        Equal(1, LiteModGuard.Check([J(apiUser)], [J(effects)]).Count, "a provided id counts as the mod");
        Equal(1, LiteModGuard.Check([J(libUser)], [J(effects)]).Count, "a library only nested in an off mod is lost");
        Equal(0, LiteModGuard.Check([J(libUser), J(otherLibHolder)], [J(effects)]).Count, "the same library nested in an enabled mod keeps it available");
        Equal(true, LiteModGuard.Check([J(user)], [J(stubRenamed)])[0].Contains("may never switch off (kewz_subtle_stub)", StringComparison.Ordinal),
            "the Subtle Effects join fix is refused by mod id, whatever the file is called");
        string subtle = Write("SubtleEffects-fabric-1.21.1-1.14.3.jar", ModJar("{\"schemaVersion\":1,\"id\":\"subtle_effects\",\"version\":\"1.14.3\"}"));
        List<string> noJoinFix = LiteModGuard.Check([J(user)], [J(subtle)]);
        Equal(1, noJoinFix.Count, "Subtle Effects off without the join fix enabled is refused");
        Equal(true, noJoinFix[0].Contains("(subtle_effects) may only be switched off while kewz_subtle_stub is on", StringComparison.Ordinal), noJoinFix[0]);
        Equal(0, LiteModGuard.Check([J(user), J(stubRenamed)], [J(subtle)]).Count, "with the join fix enabled Subtle Effects may be switched off");
        Equal(1, LiteModGuard.Check([], [J(sodium)]).Count, "critical mods are refused");
        Equal(true, LiteModGuard.Check([], [J(library)])[0].Contains("is a library", StringComparison.Ordinal), "library badge refused");
        Equal(true, LiteModGuard.Check([], [J(notFabric)])[0].Contains("is not a Fabric mod", StringComparison.Ordinal), "non-Fabric jars refused");
        Equal(true, LiteModGuard.Check([], [(Path.GetFileName(effects), effects + ".missing")])[0].Contains("could not be read", StringComparison.Ordinal),
            "an unreadable off jar is refused");
        Equal("effects", LiteModGuard.ReadJar(effects, "effects.jar")[0].Id, "raw line breaks inside strings are read like Fabric Loader does");
        Equal(2, LiteModGuard.ReadJar(effects, "effects.jar").Count, "nested jars are read");
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
        Detection = RecommendedLines,
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
        PerformanceEnvironment donglord = DonPc();
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
        PerformanceEnvironment jim = JimPc();
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
        PerformanceEnvironment jim = JimPc();
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
        PerformanceEnvironment jim = JimPc();
        LiteFixture fixture = NewLiteFixture(root);
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);
        await LaunchLiteAsync(fixture, jim);
        Equal(false, LocalExists(fixture, LiteModA), "lite applied at 1.0.61");

        // 1.0.62 changes only other jars: the lite mods, already off and unchanged, stay off (round-2 verifier V4).
        const string otherMod = "mods/other-mod-1.0.jar";
        fixture.Managed[otherMod] = ConvergenceFabricJar("othermod", "1.0");
        await PublishLiteAsync(fixture, "1.0.62", FixtureLite(), signer);
        await LaunchLiteAsync(fixture, jim);
        Equal(true, LocalExists(fixture, otherMod), "the new jar of 1.0.62 is installed");
        Equal(false, LocalExists(fixture, LiteModA) || LocalExists(fixture, LiteModB), "lite mods already off stay off when only other jars change");
        Equal(true, LocalExists(fixture, LiteModA + ".disabled") && LocalExists(fixture, LiteModB + ".disabled"), "their disabled copies are kept");

        // 1.0.63 updates a lite mod, retires the other, and ships a new Default profile revision.
        byte[] oldA = fixture.Managed[LiteModA];
        fixture.Managed[LiteModA] = ConvergenceFabricJar("atmospherics", "2.6.7");
        fixture.Managed.Remove(LiteModB);
        fixture.Managed[LiteDefaultProfile] = Encoding.UTF8.GetBytes(ProfileText("Default",
            ["vinery:bushy_leaves", "file/Connected-Bricks 1.21-1.21.3 v3.1.zip", "file/New Official.zip", "fabric", "vanilla"]));
        LitePerformanceProfile next = FixtureLite();
        next.DisabledMods = [LiteModA];
        await PublishLiteAsync(fixture, "1.0.63", next, signer);
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
        await PublishLiteAsync(fixture, "1.0.64", null, signer);
        List<string> removedLogs = await LaunchLiteAsync(fixture, jim);
        Equal(true, File.Exists(a), "no lite list in the release = full pack");
        Equal(true, removedLogs.Any(line => line.Contains("this release has no lite list", StringComparison.Ordinal)), "logged");
    }

    private static async Task TestLiteCrashRecoveryAsync(string root, ConvergenceTestSigner signer)
    {
        PerformanceEnvironment jim = JimPc();
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
        PerformanceEnvironment jim = JimPc();
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

    // An enabled mod that DEPENDS on a lite mod: the updater refuses the mod list on the PC (no mod switched off, the
    // refusal logged once per release and list); packs and settings still apply. Removing the dependent mod in the next
    // release lets the list through.
    private static async Task TestLiteRefusedModListAsync(string root, ConvergenceTestSigner signer)
    {
        LiteFixture fixture = NewLiteFixture(root);
        const string dependent = "mods/sky-addon-1.0.jar";
        fixture.Managed[dependent] = ModJar("{\"schemaVersion\":1,\"id\":\"sky_addon\",\"version\":\"1.0\",\"depends\":{\"atmospherics\":\">=2.6\"}}");
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);
        List<string> logs = await LaunchLiteAsync(fixture, JimPc());
        string refusal = logs.Single(line => line.StartsWith("Performance mode: the lite mod list is refused", StringComparison.Ordinal));
        Equal(true, refusal.Contains("sky-addon-1.0.jar (sky_addon) depends on atmospherics, which only atmospherics-2.6.6.jar provides", StringComparison.Ordinal), refusal);
        Equal(true, LocalExists(fixture, LiteModA) && LocalExists(fixture, LiteModB), "no mod is switched off");
        Equal(false, LocalExists(fixture, LiteModA + ".disabled"), "no disabled copy is made");
        Equal(false, LocalText(fixture, LiteDefaultProfile).Contains("sunbathing", StringComparison.Ordinal), "packs still apply");
        Equal(true, LocalText(fixture, LiteSodium).Contains("\"FAST\"", StringComparison.Ordinal), "settings still apply");
        Equal(true, LocalText(fixture, PathSafety.PerformanceLedgerPath).Contains("\"modCheck\": \"refused:", StringComparison.Ordinal), "the refusal is recorded");
        logs = await LaunchLiteAsync(fixture, JimPc());
        Equal(false, logs.Any(line => line.Contains("lite mod list", StringComparison.Ordinal)), "the jars are not read again for the same release and list");
        Equal(true, LocalExists(fixture, LiteModA), "still on");

        // 1.0.62 retires the add-on (a real release lists it in deletedFiles; here the file is removed directly). A jar
        // still on disk would keep the list refused, because Fabric would still load it.
        fixture.Managed.Remove(dependent);
        File.Delete(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, dependent));
        await PublishLiteAsync(fixture, "1.0.62", FixtureLite(), signer);
        logs = await LaunchLiteAsync(fixture, JimPc());
        Equal(true, logs.Any(line => line.StartsWith("Performance mode: the lite mod list passed the dependency check (2 mods off", StringComparison.Ordinal)),
            "the next release is checked again and passes: " + string.Join(" | ", logs.Where(line => line.Contains("Performance", StringComparison.Ordinal) || line.Contains("sky", StringComparison.Ordinal))));
        Equal(false, LocalExists(fixture, LiteModA) || LocalExists(fixture, LiteModB), "both lite mods off now");
    }

    // A player deletes an official Packed Packs profile while lite is on: convergence puts the signed copy back and lite
    // filters it again (round-1 verifier note: it came back unfiltered).
    private static async Task TestLiteDeletedProfileAsync(string root, ConvergenceTestSigner signer)
    {
        LiteFixture fixture = NewLiteFixture(root);
        await PublishLiteAsync(fixture, "1.0.61", FixtureLite(), signer);
        await LaunchLiteAsync(fixture, JimPc());
        string filtered = LocalText(fixture, LiteDefaultProfile);
        Equal(false, filtered.Contains("sunbathing", StringComparison.Ordinal), "filtered first");
        File.Delete(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteDefaultProfile));
        List<string> logs = await LaunchLiteAsync(fixture, JimPc());
        Equal(true, logs.Any(line => line == "Repair needed: " + LiteDefaultProfile), "convergence restores the signed profile");
        Equal(true, logs.Any(line => line.Contains("Default.profile.json came back unfiltered", StringComparison.Ordinal)), "lite notices");
        Equal(filtered, LocalText(fixture, LiteDefaultProfile), "and filters it again, byte for byte as before");
        // A profile Packed Packs rewrote with the packs back in (other bytes) is the player's choice and stays.
        string playerChoice = Encoding.UTF8.GetString(fixture.Managed[LiteDefaultProfile]).Replace("\n", "\r\n");
        await File.WriteAllTextAsync(PathSafety.CombineUnder(fixture.Paths.MinecraftDirectory, LiteDefaultProfile), playerChoice, new UTF8Encoding(false));
        await LaunchLiteAsync(fixture, JimPc());
        Equal(playerChoice, LocalText(fixture, LiteDefaultProfile), "a player-made profile is not fought");
        // Back to full: the exact signed bytes.
        await SetModeAsync(fixture, "full");
        await LaunchLiteAsync(fixture, JimPc());
        Equal(true, LocalText(fixture, LiteDefaultProfile).Contains("sunbathing", StringComparison.Ordinal), "full has the packs");
    }

    // The Subtle Effects join fix is a normal managed mod for everyone and can never be listed in disabledMods.
    private static void TestStubNeverDisabled()
    {
        const string stub = "mods/kewz-subtle-effects-stub-1.0.0+mc1.21.1.jar";
        Equal(true, PathSafety.IsNeverDisabledModPath(stub), "the join fix is never-disabled");
        Equal(false, PathSafety.IsLiteModPath(stub), "and is not a lite mod path");
        Equal(false, PathSafety.IsPerformanceOutcomeAllowed(stub + ".disabled"), "no transaction may create its disabled copy");
        Validate(m =>
        {
            m.Files.Add(ConvergenceFile(stub, ModJar("{\"schemaVersion\":1,\"id\":\"kewz_subtle_stub\",\"version\":\"1.0.0\"}")));
            m.PerformanceProfiles!.Lite!.DisabledMods.Add(stub);
        }, false, "a signed lite profile listing the join fix is rejected");
        Validate(m => m.Files.Add(ConvergenceFile(stub, ModJar("{\"schemaVersion\":1,\"id\":\"kewz_subtle_stub\",\"version\":\"1.0.0\"}"))),
            true, "the join fix as a normal managed mod is fine");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static PerformanceEnvironment JimPc() => new() { ReadCpuName = () => JimCpu, ReadGpus = () => [JimGpu()] };
    private static PerformanceEnvironment DonPc() => new() { ReadCpuName = () => DonCpu, ReadGpus = () => [DonGpu()] };
    private static PerformanceEnvironment KewzPc() => new() { ReadCpuName = () => KewzCpu, ReadGpus = KewzGpus };

    private static byte[] UpdaterResource(string name)
    {
        using Stream stream = typeof(HardwareDb).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("missing resource " + name);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static UpdaterPaths PathsWithLocal(string root, string local)
    {
        UpdaterPaths paths = Paths(root);
        return paths with { LocalDataDirectory = local };
    }

    private static string ProfileText(string name, IEnumerable<string> ids) =>
        "{\n  \"locked\": false,\n  \"name\": \"" + name + "\",\n  \"overrides\": {},\n  \"packIds\": [\n    "
        + string.Join(",\n    ", ids.Select(id => "\"" + id + "\"")) + "\n  ]\n}\n";
}
