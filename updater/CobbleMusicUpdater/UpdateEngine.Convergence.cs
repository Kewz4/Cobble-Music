using System.IO.Compression;
using System.Text.Json;

namespace CobbleMusicUpdater;

internal sealed partial class UpdateEngine
{
    // Reconcile only signed managed destinations. Player defaults stay create-only.
    // The transaction retains every displaced file in its recovery directory.
    internal async Task ConvergeToLatestAsync(IReadOnlyList<RemoteRelease> catalog,
        bool checkOnly, CancellationToken token)
    {
        if (catalog.Count == 0)
            throw new InvalidDataException("No signed modpack release could be verified. Launch is blocked.");
        foreach (RemoteRelease release in catalog) ValidateRemoteRelease(release);
        RemoteRelease latest = catalog.OrderByDescending(r => Version.Parse(r.Manifest.Version)).First();
        UpdateManifest target = latest.Manifest;
        InstalledState state = LocalStateStore.LoadState(_paths);
        if (IsDowngradeOrMutation(state, target, latest.ManifestSha256))
            throw new InvalidDataException($"Published {target.Version} cannot validate local {state.Version}: downgrade or changed manifest identity.");
        var needed = new List<ManifestFile>();
        int checkedFiles = 0;
        foreach (ManifestFile file in target.Files)
        {
            token.ThrowIfCancellationRequested();
            if (HistoricalManifestPolicy.IsPlayerOwned(target, file) || PathSafety.IsOptionalPlayerMod(file.Path)) continue;
            if (!await MatchesLocalAsync(file, token))
            {
                needed.Add(file);
                _log($"Repair needed: {file.Path}");
            }
            Report(UpdatePhase.Validating, "Checking installed file contents", 0, 0, ++checkedFiles, target.Files.Count);
        }
        var seeds = new List<ManifestFile>();
        var offered = state.OfferedSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasState = !string.IsNullOrEmpty(state.Version);
        if (hasState && offered.Count == 0 && Version.TryParse(state.Version, out Version? oldVersion))
            offered.UnionWith(catalog.Where(r => Version.Parse(r.Manifest.Version) <= oldVersion)
                .SelectMany(r => r.Manifest.SeedFiles).Select(f => f.Path));
        foreach (ManifestFile seed in target.SeedFiles.Where(f => PathSafety.IsSeedAllowed(f.Path)))
        {
            string path = SafeLocal(seed.Path);
            if (!File.Exists(path) && !Directory.Exists(path)
                && (!hasState || !offered.Contains(seed.Path) || target.ReofferSeedPaths.Contains(seed.Path, StringComparer.OrdinalIgnoreCase)))
                seeds.Add(seed);
            else if (File.Exists(path) && target.ReofferSeedPaths.Contains(seed.Path, StringComparer.OrdinalIgnoreCase)
                && target.LegacyCleanup.Any(f => f.Path.Equals(seed.Path, StringComparison.OrdinalIgnoreCase)
                    && f.Size == new FileInfo(path).Length))
            {
                string hash = await PathSafety.Sha256Async(path, token);
                if (target.LegacyCleanup.Any(f => f.Path.Equals(seed.Path, StringComparison.OrdinalIgnoreCase)
                    && PathSafety.IsExpectedHash(hash, f.Sha256))) seeds.Add(seed);
            }
        }
        bool sameIdentity = state.Version == target.Version
            && PathSafety.IsExpectedHash(state.ManifestSha256, latest.ManifestSha256);
        bool corrective = await HasPendingCorrectiveWorkAsync(target, state, token);
        var duplicates = needed.Count == 0
            ? await FindConflictingModsAsync(target, null, token) : new List<LegacyCleanupFile>();
        if (needed.Count == 0 && seeds.Count == 0 && duplicates.Count == 0 && sameIdentity && !corrective)
        {
            _log($"Verified all {target.Files.Count} managed files against signed release {target.Version}.");
            Report(UpdatePhase.Complete, $"Verified {target.Version} — starting Minecraft.");
            return;
        }
        _log($"Converging directly to {target.Version}: {needed.Count} managed repairs and {seeds.Count} defaults.");
        if (checkOnly)
        {
            Report(UpdatePhase.Complete, $"Release {target.Version}: {needed.Count} file repairs required.");
            return;
        }
        LocalStateStore.AssertWritable(_paths);
        string work = Path.Combine(_paths.LocalDataDirectory, "staging", "converge-" + latest.ManifestSha256);
        PathSafety.AssertNoReparsePointsOnTargetPath(_paths.LocalDataDirectory, work);
        string incoming = Path.Combine(work, "incoming");
        Directory.CreateDirectory(incoming);
        var groups = new Dictionary<RemoteRelease, List<ManifestFile>>();
        foreach (ManifestFile file in needed.Concat(seeds))
        {
            RemoteRelease origin = catalog.Where(r => r.Manifest.Payload is not null
                && ManifestParser.PayloadContents(r.Manifest).Any(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)
                    && ManifestParser.SameFile(f, file)))
                .OrderBy(r => r.Manifest.Payload!.Size).FirstOrDefault()
                ?? throw new InvalidDataException($"No signed downloadable source exists for {file.Path}; release is incomplete.");
            if (!groups.TryGetValue(origin, out List<ManifestFile>? list)) groups[origin] = list = [];
            list.Add(file);
        }
        long requiredSpace = checked(needed.Concat(seeds).Sum(f => f.Size) * 2
            + groups.Keys.Sum(r => r.Manifest.Payload!.Size) + DiskReserveBytes);
        if (new DriveInfo(Path.GetPathRoot(work)!).AvailableFreeSpace < requiredSpace)
            throw new IOException($"The updater needs {requiredSpace / (1024 * 1024)} MiB of staging/rollback space.");
        var progress = new DownloadProgressScope(groups.Keys.Sum(r => CalculatePayloadDownloadBytes(r.Manifest)));
        foreach ((RemoteRelease origin, List<ManifestFile> files) in groups)
            await StageConvergenceFilesAsync(origin, files, incoming, progress, token);
        if (needed.Count != 0) duplicates = await FindConflictingModsAsync(target, incoming, token);
        // Explicit signed delta removals also apply when an old base was never
        // installed. Only exact old bytes qualify; player-modified files do not.
        duplicates.AddRange(target.DeletedFiles
            .Where(f => !PathSafety.IsOptionalPlayerMod(f.Path))
            .Select(f => new LegacyCleanupFile { Path = f.Path, Size = f.Size, Sha256 = f.Sha256 }));
        // These are signed historical defaults, not permission to manage settings.
        state.OfferedSeedPaths = offered.Where(HistoricalManifestPolicy.IsLedgerPathAllowed).ToList();
        await ApplyTransactionAsync(target, latest.ManifestSha256, incoming, state, null,
            token, reconciledFiles: needed, reconciledCleanup: duplicates);
        TryDeleteDirectory(work);
        _log($"Release {target.Version} committed and verified: {needed.Count} repaired files. Prior files remain in recovery backups.");
        Report(UpdatePhase.Complete, $"Verified {target.Version} — starting Minecraft.");
    }

    private string SafeLocal(string path)
    {
        string local = PathSafety.CombineUnder(_paths.MinecraftDirectory, path);
        PathSafety.AssertNoReparsePointsOnTargetPath(_paths.MinecraftDirectory, local);
        return local;
    }

    private async Task<bool> MatchesLocalAsync(ManifestFile file, CancellationToken token)
    {
        string path = SafeLocal(file.Path);
        return File.Exists(path) && new FileInfo(path).Length == file.Size
            && PathSafety.IsExpectedHash(await PathSafety.Sha256Async(path, token), file.Sha256);
    }

    // Only a second copy of an explicitly managed Fabric mod qualifies. Extra
    // unrelated mods and every Axiom version remain player-owned. Exact bytes
    // are rechecked by the journal before moving to the retained backup.
    private async Task<List<LegacyCleanupFile>> FindConflictingModsAsync(UpdateManifest target,
        string? incoming, CancellationToken token)
    {
        var canonical = target.Files.Where(f => f.Path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)
            && f.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && !PathSafety.IsOptionalPlayerMod(f.Path)).ToList();
        var paths = canonical.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile file in canonical)
        {
            string local = SafeLocal(file.Path);
            string staged = incoming is null ? local : PathSafety.CombineUnder(incoming, file.Path);
            string selected = File.Exists(staged) ? staged : local;
            if (!File.Exists(selected)) continue;
            await VerifyFileAsync(selected, file.Size, file.Sha256, file.Path, token);
            string? id = ReadFabricModId(selected);
            if (id is null) continue;
            if (ids.TryGetValue(id, out string? existing))
                throw new InvalidDataException($"Signed release has duplicate mod ID {id}: {existing}, {file.Path}");
            ids[id] = file.Path;
        }
        var result = new List<LegacyCleanupFile>();
        string modRoot = Path.Combine(_paths.MinecraftDirectory, "mods");
        if (!Directory.Exists(modRoot)) return result;
        foreach (string local in Directory.EnumerateFiles(modRoot, "*.jar", SearchOption.TopDirectoryOnly))
        {
            string relative = "mods/" + Path.GetFileName(local);
            if (paths.Contains(relative) || PathSafety.IsOptionalPlayerMod(relative)) continue;
            _ = SafeLocal(relative);
            string? id = ReadFabricModId(local);
            if (id is null || !ids.ContainsKey(id)) continue;
            _log($"Conflicting mod {relative} will be moved to a retained recovery backup; canonical mod: {ids[id]}.");
            result.Add(new LegacyCleanupFile { Path = relative, Size = new FileInfo(local).Length,
                Sha256 = await PathSafety.Sha256Async(local, token) });
        }
        return result;
    }

    private static string? ReadFabricModId(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("fabric.mod.json");
            if (entry is null || entry.Length > 1024 * 1024) return null;
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }
        catch (InvalidDataException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task StageConvergenceFilesAsync(RemoteRelease origin, List<ManifestFile> files,
        string incoming, DownloadProgressScope progress, CancellationToken token)
    {
        UpdatePayload payload = origin.Manifest.Payload!;
        string work = Path.Combine(_paths.LocalDataDirectory, "staging", origin.ManifestSha256);
        PathSafety.AssertNoReparsePointsOnTargetPath(_paths.LocalDataDirectory, work);
        Directory.CreateDirectory(work);
        string archive = Path.Combine(work, "payload.zip");
        if (!await TryReuseVerifiedArchiveAsync(archive, payload, token))
        {
            string parts = Path.Combine(work, "parts");
            Directory.CreateDirectory(parts);
            using var client = new ReleaseClient(TimeSpan.FromSeconds(_configuration.NetworkTimeoutSeconds));
            foreach (PayloadPart part in payload.Parts)
            {
                _log($"Downloading repair source {origin.Manifest.Version}/{part.Name}...");
                long network = await DownloadVerifiedPartAsync(client, origin.AssetUrls[part.Name], part,
                    Path.Combine(parts, part.Name), p => Report(progress.ForPart(p)), token);
                progress.CompletePart(part.Size, network);
            }
            await CombinePartsAndDeleteAsync(payload.Parts, parts, archive, token);
            await VerifyFileAsync(archive, payload.Size, payload.Sha256, "repair archive", token);
        }
        else progress.ExcludeSkippedPayload(CalculatePayloadDownloadBytes(origin.Manifest));
        using var zip = ZipFile.OpenRead(archive);
        foreach (ManifestFile file in files)
        {
            token.ThrowIfCancellationRequested();
            var entries = zip.Entries.Where(e => e.FullName.Equals(file.Path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count != 1 || entries[0].Length != file.Size
                || ((entries[0].ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException($"Signed repair archive has invalid entry: {file.Path}");
            string destination = PathSafety.CombineUnder(incoming, file.Path);
            PathSafety.AssertNoReparsePointsOnTargetPath(incoming, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (Stream input = entries[0].Open())
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, token);
            await VerifyFileAsync(destination, file.Size, file.Sha256, file.Path, token);
        }
    }
}
