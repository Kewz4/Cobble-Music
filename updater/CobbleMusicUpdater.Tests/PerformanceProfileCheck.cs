using System.Text;
using System.Text.Json;
using CobbleMusicUpdater;

// 1.2.22 lite mode, pack side: `--check-performance-profile <signed manifest.json> <profile.json> <seed dir> <profile dir> <preview out dir>`
// runs a performanceProfiles file through the updater's OWN validation against a real signed release manifest, checks
// every setting key against the real seed files (found exactly once, current value), and writes what the two official
// Packed Packs profiles become on a lite PC (same code path as the updater). Read-only apart from the preview folder.
internal static partial class Program
{
    private static int CheckPerformanceProfile(string manifestPath, string profilePath, string seedDirectory, string profileDirectory, string previewDirectory)
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
        Console.WriteLine($"revision {lite.RevisionId}; processor threshold {lite.Detection.CpuScoreThreshold}; "
            + $"{lite.Detection.FullGpuPatterns.Count} full / {lite.Detection.LiteGpuPatterns.Count} lite graphics patterns");
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
        Console.WriteLine(problems == 0 ? "RESULT: OK" : $"RESULT: {problems} problem(s)");
        return problems == 0 ? 0 : 2;
    }
}
