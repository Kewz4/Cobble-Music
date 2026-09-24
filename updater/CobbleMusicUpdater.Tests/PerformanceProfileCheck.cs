using System.Text;
using System.Text.Json;
using CobbleMusicUpdater;

// 1.2.22 lite mode, pack side: `--check-performance-profile <signed manifest.json> <profile.json> <seed dir> <profile dir> <preview out dir> [<extra mod jar>...]`
// runs a performanceProfiles file through the updater's OWN validation against a real signed release manifest, checks
// every setting key against the real seed files (found exactly once, current value), and writes what the two official
// Packed Packs profiles become on a lite PC (same code path as the updater). Round 2 also runs the updater's own lite mod
// dependency check (LiteModGuard) over <seed dir>/mods plus any extra jars (the Subtle Effects join fix before it is in the
// tree), checks each managed mod jar in <seed dir>/mods against the signed manifest, and prints the decision for the three
// named PCs from their real Windows strings. Read-only apart from the preview folder.
internal static partial class Program
{
    private static int CheckPerformanceProfile(string manifestPath, string profilePath, string seedDirectory, string profileDirectory, string previewDirectory,
        string[] extraMods)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        UpdateManifest manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllBytes(manifestPath), options)
            ?? throw new InvalidDataException("manifest is empty");
        PerformanceProfiles profiles = JsonSerializer.Deserialize<PerformanceProfiles>(File.ReadAllBytes(profilePath), options)
            ?? throw new InvalidDataException("profile file is empty");
        manifest.PerformanceProfiles = profiles;
        var files = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seeds = manifest.SeedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        PerformanceProfilePolicy.Validate(manifest, files, seeds, PerformanceProfilePolicy.MinimumUpdater);
        LitePerformanceProfile lite = profiles.Lite ?? throw new InvalidDataException("no lite profile");
        int problems = 0;
        Console.WriteLine($"VALID for updater {BuildInfo.Version}: {profilePath} against {manifest.Version} ({files.Count} managed, {seeds.Count} defaults)");
        Console.WriteLine($"revision {lite.RevisionId}; lines: processor single-thread below {lite.Detection.CpuSingleThreadBelow}, "
            + $"graphics G3D below {lite.Detection.GpuScoreBelow}; built-in table {HardwareDb.Embedded.Version} "
            + $"({HardwareDb.Embedded.CpuCount} processors, {HardwareDb.Embedded.GpuCount} graphics cards)");
        foreach (string mod in lite.DisabledMods)
            Console.WriteLine($"mod off: {mod} ({files[mod].Size:N0} bytes, sha256 {files[mod].Sha256[..12]})");

        foreach (PerformanceSetting setting in lite.Settings)
        {
            string local = PathSafety.CombineUnder(seedDirectory, setting.Path);
            byte[] bytes = File.ReadAllBytes(local);
            string localHash = HashBytes(bytes);
            bool isSignedSeed = localHash == seeds[setting.Path].Sha256.ToLowerInvariant();
            SettingText.ValueSpan? span = SettingText.Find(bytes, setting.Format, setting.Key);
            if (span is null) problems++;
            Console.WriteLine($"setting: {setting.Path} {setting.Key}: {(span is null ? "NOT FOUND EXACTLY ONCE" : span.Value.Raw)} -> {setting.Value}"
                + (isSignedSeed ? " (file = signed default)" : " (WARNING: file differs from the signed default)"));
        }

        Directory.CreateDirectory(previewDirectory);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ManifestFile profile in manifest.Files.Where(file => PathSafety.IsOfficialPackProfilePath(file.Path)))
        {
            byte[] bytes = File.ReadAllBytes(PathSafety.CombineUnder(profileDirectory, profile.Path));
            if (HashBytes(bytes) != profile.Sha256.ToLowerInvariant())
            {
                problems++;
                Console.WriteLine($"PROFILE MISMATCH: {profile.Path} in {profileDirectory} is not the signed revision");
                continue;
            }
            PackProfileText.PackList list = PackProfileText.Parse(bytes) ?? throw new InvalidDataException("profile does not parse: " + profile.Path);
            var removed = list.Entries.Select((entry, index) => (entry.Id, index)).Where(item => lite.RemovedPackIds.Contains(item.Id)).ToList();
            foreach (var item in removed) seen.Add(item.Id);
            byte[] filtered = PackProfileText.Write(bytes, list, list.Entries.Where(entry => !lite.RemovedPackIds.Contains(entry.Id)).ToList());
            string name = Path.GetFileName(profile.Path);
            File.WriteAllBytes(Path.Combine(previewDirectory, "lite-" + name), filtered);
            Console.WriteLine($"profile {name}: {list.Entries.Count} -> {list.Entries.Count - removed.Count} packs; removed "
                + string.Join(", ", removed.Select(item => $"#{item.index} {item.Id}")));
        }
        foreach (string id in lite.RemovedPackIds.Where(id => !seen.Contains(id)))
        {
            Console.WriteLine($"note: pack id not in either signed profile (nothing to remove): {id}");
        }
        // The release tree's mod jars must be the signed ones, and the lite mod list must pass the updater's own dependency
        // check against them (plus the extra jars that will be added to the tree, such as the Subtle Effects join fix).
        string modsDirectory = Path.Combine(seedDirectory, "mods");
        int mismatched = 0, checkedJars = 0;
        foreach (ManifestFile file in manifest.Files.Where(file => file.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            && file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !file.Path[5..].Contains('/')))
        {
            string local = PathSafety.CombineUnder(seedDirectory, file.Path);
            checkedJars++;
            if (!File.Exists(local) || HashBytes(File.ReadAllBytes(local)) != file.Sha256.ToLowerInvariant())
            {
                mismatched++;
                Console.WriteLine($"TREE MISMATCH: {file.Path} is missing or differs from the signed manifest");
            }
        }
        problems += mismatched;
        Console.WriteLine($"tree: {checkedJars} signed top-level mod jars checked against {modsDirectory}, {mismatched} mismatched");
        var wanted = lite.DisabledMods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = Directory.GetFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase)
            .Where(jar => !wanted.Contains("mods/" + Path.GetFileName(jar)))
            .Select(jar => (Path.GetFileName(jar), jar))
            .Concat(extraMods.Select(jar => ("extra " + Path.GetFileName(jar), Path.GetFullPath(jar))))
            .ToList();
        var off = lite.DisabledMods.Select(path => (Path.GetFileName(path), PathSafety.CombineUnder(seedDirectory, path))).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        List<string> modProblems = LiteModGuard.Check(enabled, off);
        Console.WriteLine($"lite mod dependency check (updater LiteModGuard): {off.Count} off, {enabled.Count} stay on, {modProblems.Count} problem(s), {clock.ElapsedMilliseconds} ms");
        foreach (string problem in modProblems) Console.WriteLine("REFUSED: " + problem);
        problems += modProblems.Count;
        foreach (string extra in extraMods)
        {
            List<LiteModGuard.ModInfo> info = LiteModGuard.ReadJar(extra, Path.GetFileName(extra));
            Console.WriteLine($"extra mod on for everyone: {Path.GetFileName(extra)} (id {info[0].Id}, sha256 {HashBytes(File.ReadAllBytes(extra))[..12]}); "
                + $"lite may list it: {(PathSafety.IsLiteModPath("mods/" + Path.GetFileName(extra)) ? "yes" : "no")}");
        }

        // The three PCs Kewz named, from their real (or Windows-format) strings, through the embedded table and these lines.
        foreach ((string who, string cpu, GpuAdapterInfo[] gpus) in NamedPcs())
        {
            HardwareDecision decision = HardwareVerdict.Decide(cpu, gpus, HardwareDb.Embedded, lite.Detection);
            Console.WriteLine($"{who}: {decision.Mode.ToString().ToUpperInvariant()} - {HardwareVerdict.StatusText(decision, decision.Mode)}");
        }
        Console.WriteLine(problems == 0 ? "RESULT: OK" : $"RESULT: {problems} problem(s)");
        return problems == 0 ? 0 : 2;
    }

    // Jim (Pastel_Crows) and DONGLORD9000 from the task's strings; Kewz's PC exactly as its registry reads (2026-09-24).
    // Jim's and DONGLORD9000's MatchingDeviceIds are not known; the PCI ids used are the desktop cards' own ids.
    internal static IEnumerable<(string Who, string Cpu, GpuAdapterInfo[] Gpus)> NamedPcs() =>
    [
        ("Jim (Pastel_Crows)", "AMD Ryzen 7 2700 Eight-Core Processor",
            [new GpuAdapterInfo { Name = "NVIDIA GeForce RTX 3050", DeviceId = @"pci\ven_10de&dev_2507", MemoryBytes = 8192L << 20, RegistryKey = "0000", Present = true }]),
        ("DONGLORD9000", "AMD Ryzen 7 5800X 8-Core Processor",
            [new GpuAdapterInfo { Name = "NVIDIA GeForce RTX 3080", DeviceId = @"pci\ven_10de&dev_2206", MemoryBytes = 10240L << 20, RegistryKey = "0000", Present = true }]),
        ("Kewz's PC", "Intel(R) Core(TM) i7-10750H CPU @ 2.60GHz",
            [new GpuAdapterInfo { Name = "Intel(R) UHD Graphics", DeviceId = @"PCI\VEN_8086&DEV_9BC4&SUBSYS_12B41462", MemoryBytes = 1L << 30, RegistryKey = "0001", Present = true },
             new GpuAdapterInfo { Name = "NVIDIA GeForce RTX 2070 Super", DeviceId = @"pci\ven_10de&dev_1e91&subsys_12b41462", MemoryBytes = 8589934592, RegistryKey = "0002", Present = true }])
    ];
}
