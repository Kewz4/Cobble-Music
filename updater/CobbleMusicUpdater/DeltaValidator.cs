namespace CobbleMusicUpdater;

internal static class DeltaValidator
{
    public static async Task ValidateBaseAsync(
        UpdateManifest delta,
        UpdateManifest signedBase,
        string signedBaseManifestHash,
        InstalledState installedState,
        UpdaterPaths paths,
        UpdaterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (delta.SchemaVersion != 2 || delta.Base is null)
        {
            throw new InvalidOperationException("Delta base validation was requested for a non-delta manifest.");
        }
        if (!string.Equals(delta.Base.Version, signedBase.Version, StringComparison.Ordinal)
            || !string.Equals(delta.Base.ManifestSha256, signedBaseManifestHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(installedState.Version, signedBase.Version, StringComparison.Ordinal)
            || !string.Equals(installedState.ManifestSha256, signedBaseManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Delta {delta.Version} does not start from the exact installed signed base.");
        }

        Dictionary<string, ManifestFile> baseFiles = signedBase.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManifestFile> postFiles = delta.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManifestFile> payloadFiles = delta.PayloadFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManifestFile> deletedFiles = delta.DeletedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManifestFile> seedFiles = delta.SeedFiles.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        ILookup<string, LegacyCleanupFile> legacyCleanup = delta.LegacyCleanup.ToLookup(file => file.Path, StringComparer.OrdinalIgnoreCase);
        HashSet<string> reofferSeedPaths = delta.ReofferSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ManagedFileState> recordedFiles = ValidateInstalledState(installedState, configuration);

        if (recordedFiles.Count != baseFiles.Count)
        {
            throw new InvalidDataException("Installed state does not contain the complete signed delta base.");
        }
        foreach ((string path, ManifestFile baseFile) in baseFiles)
        {
            if (!recordedFiles.TryGetValue(path, out ManagedFileState? recorded)
                || !ManifestParser.SameFile(recorded, baseFile))
            {
                throw new InvalidDataException($"Installed state differs from the signed delta base: {path}");
            }

            bool remains = postFiles.TryGetValue(path, out ManifestFile? postFile);
            bool carried = payloadFiles.TryGetValue(path, out ManifestFile? payloadFile);
            bool deleted = deletedFiles.TryGetValue(path, out ManifestFile? deletedFile);
            bool becomesPlayerOwned = !remains
                && seedFiles.ContainsKey(path)
                && reofferSeedPaths.Contains(path)
                && legacyCleanup[path].Any(transitionIdentity =>
                    transitionIdentity.Size == baseFile.Size
                    && string.Equals(transitionIdentity.Sha256, baseFile.Sha256, StringComparison.OrdinalIgnoreCase));
            if (remains && ManifestParser.SameFile(baseFile, postFile!))
            {
                if (carried || deleted)
                {
                    throw new InvalidDataException($"Delta redundantly carries or deletes an unchanged file: {path}");
                }
            }
            else if (remains)
            {
                if (!carried || deleted || !ManifestParser.SameFile(payloadFile!, postFile!))
                {
                    throw new InvalidDataException($"Delta omits the changed payload file: {path}");
                }
            }
            else if (becomesPlayerOwned)
            {
                if (deleted || carried)
                {
                    throw new InvalidDataException($"Managed-to-player-owned transition redundantly carries or deletes: {path}");
                }
            }
            else if (!deleted || carried || !ManifestParser.SameFile(baseFile, deletedFile!))
            {
                throw new InvalidDataException($"Delta omits exact signed deletion metadata for: {path}");
            }
        }

        foreach ((string path, ManifestFile postFile) in postFiles)
        {
            if (!baseFiles.ContainsKey(path)
                && (!payloadFiles.TryGetValue(path, out ManifestFile? payloadFile)
                    || !ManifestParser.SameFile(payloadFile, postFile)))
            {
                throw new InvalidDataException($"Delta omits the new payload file: {path}");
            }
        }
        if (deletedFiles.Keys.Any(path => !baseFiles.ContainsKey(path)))
        {
            throw new InvalidDataException("Delta attempts to delete a file that was not in its signed base.");
        }

        // Validate every managed base file, not only unchanged files. That
        // prevents a delta from overwriting or deleting a locally modified
        // updater-owned file under the guise of a valid base version.
        foreach ((string path, ManifestFile baseFile) in baseFiles)
        {
            bool becomesPlayerOwned = !postFiles.ContainsKey(path)
                && seedFiles.ContainsKey(path)
                && reofferSeedPaths.Contains(path)
                && legacyCleanup[path].Any(transitionIdentity =>
                    transitionIdentity.Size == baseFile.Size
                    && string.Equals(transitionIdentity.Sha256, baseFile.Sha256, StringComparison.OrdinalIgnoreCase));
            if (becomesPlayerOwned)
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            string target = PathSafety.CombineUnder(paths.MinecraftDirectory, path);
            PathSafety.AssertNoReparsePointsOnTargetPath(paths.MinecraftDirectory, target);
            if (!File.Exists(target) || new FileInfo(target).Length != baseFile.Size)
            {
                throw new InvalidDataException($"Local file is missing or changed from the signed delta base: {path}");
            }
            string actualHash = await PathSafety.Sha256Async(target, cancellationToken);
            if (!PathSafety.IsExpectedHash(actualHash, baseFile.Sha256))
            {
                throw new InvalidDataException($"Local file is changed from the signed delta base: {path}");
            }
        }
    }

    private static Dictionary<string, ManagedFileState> ValidateInstalledState(
        InstalledState state,
        UpdaterConfiguration configuration)
    {
        var files = new Dictionary<string, ManagedFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedFileState file in state.ManagedFiles)
        {
            if (file is null)
            {
                throw new InvalidDataException("Installed state contains an empty managed file entry.");
            }
            file.Path = PathSafety.NormalizeRelativePath(file.Path);
            if (!PathSafety.IsAllowed(file.Path, configuration.AllowedRoots)
                || file.Size < 0
                || !files.TryAdd(file.Path, file))
            {
                throw new InvalidDataException($"Installed state contains an unsafe or duplicate path: {file.Path}");
            }
            ManifestParser.ValidateHash(file.Sha256, $"installed file {file.Path}");
        }
        return files;
    }
}
