using System.Text.Json;

namespace CobbleMusicUpdater;

internal static class LocalStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static UpdaterPaths ResolvePaths(string instanceDirectory, string minecraftDirectory)
    {
        string legacyInstanceIdentity = Path.GetFullPath(instanceDirectory);
        string instance = Path.TrimEndingDirectorySeparator(legacyInstanceIdentity);
        string minecraft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(minecraftDirectory));
        if (!Directory.Exists(instance))
        {
            throw new DirectoryNotFoundException($"Prism instance directory was not found: {instance}");
        }
        if (!Directory.Exists(minecraft))
        {
            throw new DirectoryNotFoundException($"Minecraft directory was not found: {minecraft}");
        }

        string installDirectory = Path.Combine(minecraft, "cobble-music-updater");
        string localBase = ResolveLocalDataDirectory(instance, legacyInstanceIdentity);

        return new UpdaterPaths(
            instance,
            minecraft,
            installDirectory,
            Path.Combine(installDirectory, "updater.json"),
            Path.Combine(installDirectory, "state.json"),
            localBase);
    }

    internal static string NormalizeInstanceIdentity(string instanceDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(instanceDirectory)).ToUpperInvariant();

    private static string ResolveLocalDataDirectory(string instanceDirectory, string legacyInstanceIdentity)
    {
        string localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CobbleMusicUpdater");
        string normalizedDirectory = Path.Combine(localRoot, IdentityHash(NormalizeInstanceIdentity(instanceDirectory)));
        string legacyDirectory = Path.Combine(localRoot, IdentityHash(legacyInstanceIdentity));
        if (string.Equals(normalizedDirectory, legacyDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedDirectory;
        }

        string mappingPath = normalizedDirectory + ".legacy-location";
        if (File.Exists(mappingPath))
        {
            string mappedHash = File.ReadAllText(mappingPath).Trim();
            if (mappedHash.Length != 16 || mappedHash.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new TransactionRecoveryException("The normalized updater identity mapping is invalid.");
            }
            string mappedDirectory = Path.Combine(localRoot, mappedHash.ToLowerInvariant());
            if (Directory.Exists(mappedDirectory))
            {
                if ((File.GetAttributes(mappedDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new TransactionRecoveryException("The mapped legacy updater state is a junction or symbolic link.");
                }
                return mappedDirectory;
            }
        }

        if (Directory.Exists(normalizedDirectory))
        {
            if (File.Exists(Path.Combine(legacyDirectory, "transaction.json")))
            {
                throw new TransactionRecoveryException("Both legacy and normalized updater state contain transaction data; automatic identity migration is unsafe.");
            }
            return normalizedDirectory;
        }
        if (!Directory.Exists(legacyDirectory))
        {
            return normalizedDirectory;
        }
        if ((File.GetAttributes(legacyDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TransactionRecoveryException("The legacy updater state directory is a junction or symbolic link; automatic identity migration is unsafe.");
        }

        // Older builds hashed Prism's path exactly as supplied. A tiny mapping
        // preserves that directory (and its absolute journal backup paths)
        // while making every future case/trailing-separator spelling share one
        // normalized lock identity.
        Directory.CreateDirectory(localRoot);
        string temporary = mappingPath + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, Path.GetFileName(legacyDirectory), new System.Text.UTF8Encoding(false));
            try
            {
                File.Move(temporary, mappingPath);
            }
            catch (IOException) when (File.Exists(mappingPath))
            {
                // Another launch established the same normalized mapping.
            }
            string establishedHash = File.ReadAllText(mappingPath).Trim();
            if (!string.Equals(establishedHash, Path.GetFileName(legacyDirectory), StringComparison.OrdinalIgnoreCase))
            {
                throw new TransactionRecoveryException("Concurrent launches disagreed about the legacy updater identity mapping.");
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return legacyDirectory;
    }

    private static string IdentityHash(string identity) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];

    public static UpdaterConfiguration LoadConfiguration(UpdaterPaths paths)
    {
        if (!File.Exists(paths.ConfigurationPath))
        {
            throw new FileNotFoundException(
                "The Kewz's Cobblemon updater configuration is missing. Run Install-CobbleMusicUpdater.ps1 once for this instance.",
                paths.ConfigurationPath);
        }

        UpdaterConfiguration? config = JsonSerializer.Deserialize<UpdaterConfiguration>(File.ReadAllBytes(paths.ConfigurationPath), JsonOptions);
        if (config is null
            || config.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(config.Repository)
            || config.NetworkTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidDataException($"Invalid updater configuration: {paths.ConfigurationPath}");
        }
        if (config.AllowedRoots is null)
        {
            throw new InvalidDataException("Updater configuration has no permitted update roots.");
        }
        config.AllowedRoots = config.AllowedRoots
            .Select(PathSafety.NormalizeRelativePath)
            .Where(path => !path.Contains('/', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (config.AllowedRoots.Count == 0 || config.AllowedRoots.Any(root => !BuildInfo.SupportedRoots.Contains(root)))
        {
            throw new InvalidDataException("Updater configuration contains no permitted roots or attempts to expand the compiled updater allowlist.");
        }
        return config;
    }

    public static InstalledState LoadState(UpdaterPaths paths)
    {
        if (!File.Exists(paths.StatePath))
        {
            return new InstalledState();
        }

        InstalledState? state = JsonSerializer.Deserialize<InstalledState>(File.ReadAllBytes(paths.StatePath), JsonOptions);
        if (state is null || state.SchemaVersion != 1 || state.ManagedFiles is null)
        {
            return new InstalledState();
        }
        bool hasIdentity = !string.IsNullOrWhiteSpace(state.Version) || !string.IsNullOrWhiteSpace(state.ManifestSha256);
        if (hasIdentity && (!VersionPolicy.TryParseCanonical(state.Version, out _)
            || string.IsNullOrWhiteSpace(state.ManifestSha256)
            || state.ManifestSha256.Length != 64
            || state.ManifestSha256.Any(character => !Uri.IsHexDigit(character))))
        {
            return new InstalledState();
        }
        if (!hasIdentity && state.ManagedFiles.Count != 0)
        {
            return new InstalledState();
        }
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedFileState file in state.ManagedFiles)
        {
            if (file is null)
            {
                return new InstalledState();
            }
            try
            {
                file.Path = PathSafety.NormalizeRelativePath(file.Path);
            }
            catch (InvalidDataException)
            {
                return new InstalledState();
            }
            if (!PathSafety.IsAllowed(file.Path, BuildInfo.SupportedRoots)
                || file.Size < 0
                || string.IsNullOrWhiteSpace(file.Sha256)
                || file.Sha256.Length != 64
                || file.Sha256.Any(character => !Uri.IsHexDigit(character))
                || !seenPaths.Add(file.Path))
            {
                return new InstalledState();
            }
        }
        return state;
    }

    public static async Task SaveStateAsync(UpdaterPaths paths, InstalledState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.InstallationDirectory);
        string temporary = paths.StatePath + ".new";
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        await using (var output = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(content, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }
        File.Move(temporary, paths.StatePath, overwrite: true);
    }

    public static void AssertWritable(UpdaterPaths paths)
    {
        Directory.CreateDirectory(paths.InstallationDirectory);
        AssertWritableDirectory(paths.MinecraftDirectory);
        AssertWritableDirectory(paths.InstallationDirectory);
    }

    public static FileStream AcquireOperationLock(UpdaterPaths paths)
    {
        Directory.CreateDirectory(paths.LocalDataDirectory);
        string lockPath = Path.Combine(paths.LocalDataDirectory, "update.lock");
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new UpdaterBusyException("Another Kewz's Cobblemon update check is already running for this Prism instance.", exception);
        }
    }

    private static void AssertWritableDirectory(string directory)
    {
        string probe = Path.Combine(directory, ".write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        finally
        {
            if (File.Exists(probe))
            {
                File.Delete(probe);
            }
        }
    }
}

internal sealed class UpdaterBusyException : IOException
{
    public UpdaterBusyException(string message, Exception innerException) : base(message, innerException) { }
}
