using System.Text.Json;

namespace CobbleMusicUpdater;

internal static class LocalStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static UpdaterPaths ResolvePaths(string instanceDirectory, string minecraftDirectory)
    {
        string instance = Path.GetFullPath(instanceDirectory);
        string minecraft = Path.GetFullPath(minecraftDirectory);
        if (!Directory.Exists(instance))
        {
            throw new DirectoryNotFoundException($"Prism instance directory was not found: {instance}");
        }
        if (!Directory.Exists(minecraft))
        {
            throw new DirectoryNotFoundException($"Minecraft directory was not found: {minecraft}");
        }

        string installDirectory = Path.Combine(minecraft, "cobble-music-updater");
        string localBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CobbleMusicUpdater",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(instance))).ToLowerInvariant()[..16]);

        return new UpdaterPaths(
            instance,
            minecraft,
            installDirectory,
            Path.Combine(installDirectory, "updater.json"),
            Path.Combine(installDirectory, "state.json"),
            localBase);
    }

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
        if (hasIdentity && (!Version.TryParse(state.Version, out _)
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
