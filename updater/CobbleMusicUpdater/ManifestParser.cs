using System.Text.Json;

namespace CobbleMusicUpdater;

internal static class ManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static UpdateManifest VerifyAndParse(byte[] rawManifest, byte[] rawSignature)
    {
        DetachedSignature? detachedSignature = JsonSerializer.Deserialize<DetachedSignature>(rawSignature, JsonOptions);
        if (detachedSignature is null || !ManifestSecurity.Verify(rawManifest, detachedSignature))
        {
            throw new InvalidDataException("GitHub release manifest signature is invalid. No local files were changed.");
        }

        UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(rawManifest, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidDataException("Signed release manifest is empty.");
        }
        HistoricalManifestPolicy.RegisterVerifiedManifest(manifest, rawManifest);
        return manifest;
    }

    public static void Validate(UpdateManifest manifest, UpdaterConfiguration configuration, IReadOnlyDictionary<string, Uri> assetUrls)
    {
        if (manifest.SchemaVersion is not (1 or 2)
            || !string.Equals(manifest.ModpackId, configuration.ModpackId, StringComparison.Ordinal)
            || !string.Equals(manifest.Channel, configuration.Channel, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.ReleaseTag)
            || manifest.Files is null
            || manifest.PayloadFiles is null
            || manifest.DeletedFiles is null
            || manifest.SeedFiles is null
            || manifest.ReofferSeedPaths is null
            || manifest.SeedTextReplacements is null
            || manifest.DeletePaths is null
            || manifest.LegacyCleanup is null
            || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("Signed release manifest does not match this updater or has an unsupported schema.");
        }

        if (!VersionPolicy.TryParseCanonical(manifest.Version, out Version? releaseVersion))
        {
            throw new InvalidDataException("Signed release manifest has an invalid version.");
        }
        if (!VersionPolicy.TryParseCanonical(manifest.MinimumUpdaterVersion, out Version? requiredUpdater))
        {
            throw new InvalidDataException("Signed release manifest has an invalid minimum updater version.");
        }
        if (Version.Parse(BuildInfo.Version) < requiredUpdater)
        {
            throw new InvalidDataException($"Release {manifest.Version} requires updater {manifest.MinimumUpdaterVersion}; this updater is {BuildInfo.Version}.");
        }
        if (!string.Equals(manifest.ReleaseTag, $"modpack-v{manifest.Version}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Signed release manifest has an invalid release tag/version binding.");
        }

        Dictionary<string, ManifestFile> files = ValidateFileSet(manifest.Files, configuration, "managed file");
        Dictionary<string, ManifestFile> seedFiles = ValidateSeedFileSet(manifest.SeedFiles, manifest.VerifiedRetiredSeedIdentities);
        HashSet<string> reofferSeedPaths = ValidateReofferSeedPaths(manifest.ReofferSeedPaths, seedFiles);
        ValidateSeedTextReplacements(manifest.SeedTextReplacements, seedFiles, reofferSeedPaths);
        if (seedFiles.Keys.Any(files.ContainsKey))
        {
            throw new InvalidDataException("Signed release manifest overlaps managed files and create-only defaults.");
        }

        if (manifest.SchemaVersion == 1)
        {
            if (manifest.Payload is null)
            {
                throw new InvalidDataException("Schema 1 release is missing its full payload.");
            }
            ValidatePayload(manifest.Payload, assetUrls);
            ValidateSchemaOne(manifest, configuration, files, seedFiles, reofferSeedPaths);
        }
        else
        {
            if (manifest.SeedTextReplacements.Any(replacement =>
                    replacement is not null
                    && string.Equals(replacement.Path, "options.txt", StringComparison.OrdinalIgnoreCase))
                && requiredUpdater < new Version(1, 2, 11))
            {
                throw new InvalidDataException("Signed key-collision repairs require updater 1.2.11 or newer.");
            }
            if (manifest.SeedTextReplacements.Count != 0 && requiredUpdater < new Version(1, 2, 10))
            {
                throw new InvalidDataException("Signed seed text replacements require updater 1.2.10 or newer.");
            }
            if (reofferSeedPaths.Count != 0 && requiredUpdater < new Version(1, 2, 9))
            {
                throw new InvalidDataException("Re-offered create-only defaults require updater 1.2.9 or newer.");
            }
            ValidateSchemaTwo(manifest, configuration, files, seedFiles, releaseVersion!, requiredUpdater!, assetUrls);
        }
    }

    public static IReadOnlyCollection<ManifestFile> ManagedPayloadContents(UpdateManifest manifest) =>
        manifest.SchemaVersion == 1 ? manifest.Files : manifest.PayloadFiles;

    public static IReadOnlyCollection<ManifestFile> PayloadContents(UpdateManifest manifest) =>
        ManagedPayloadContents(manifest).Concat(manifest.SeedFiles).ToArray();

    private static void ValidatePayload(UpdatePayload payload, IReadOnlyDictionary<string, Uri> assetUrls)
    {
        if (string.IsNullOrWhiteSpace(payload.ArchiveName)
            || !string.Equals(payload.ArchiveName, Path.GetFileName(payload.ArchiveName), StringComparison.Ordinal)
            || payload.ArchiveName.Contains('/')
            || payload.ArchiveName.Contains('\\')
            || payload.ArchiveName.Contains(':')
            || payload.Size <= 0
            || payload.Parts is null
            || payload.Parts.Count == 0)
        {
            throw new InvalidDataException("Release payload has an invalid archive name or size.");
        }
        ValidateHash(payload.Sha256, "payload");

        long totalPartSize = 0;
        var seenParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PayloadPart part in payload.Parts)
        {
            if (part is null
                || string.IsNullOrWhiteSpace(part.Name)
                || !string.Equals(part.Name, Path.GetFileName(part.Name), StringComparison.Ordinal)
                || part.Name.Contains('/')
                || part.Name.Contains('\\')
                || part.Name.Contains(':')
                || part.Size <= 0
                || !seenParts.Add(part.Name)
                || !assetUrls.ContainsKey(part.Name))
            {
                throw new InvalidDataException("Release payload references an invalid or unavailable asset part.");
            }
            ValidateHash(part.Sha256, $"payload part {part.Name}");
            try
            {
                totalPartSize = checked(totalPartSize + part.Size);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException("Release payload parts have an invalid total size.");
            }
        }
        if (totalPartSize != payload.Size)
        {
            throw new InvalidDataException("Release payload part sizes do not match the signed payload size.");
        }
    }

    private static Dictionary<string, ManifestFile> ValidateFileSet(
        IEnumerable<ManifestFile> entries,
        UpdaterConfiguration configuration,
        string description)
    {
        var files = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile file in entries)
        {
            if (file is null)
            {
                throw new InvalidDataException($"Release manifest contains an empty {description} entry.");
            }
            file.Path = PathSafety.NormalizeRelativePath(file.Path);
            if (!PathSafety.IsAllowed(file.Path, configuration.AllowedRoots)
                || file.Size < 0
                || !files.TryAdd(file.Path, file))
            {
                throw new InvalidDataException($"Release manifest contains an unsafe or duplicate {description} path: {file.Path}");
            }
            ValidateHash(file.Sha256, $"{description} {file.Path}");
        }
        return files;
    }

    private static Dictionary<string, ManifestFile> ValidateSeedFileSet(
        IEnumerable<ManifestFile> entries, IReadOnlySet<string> verifiedRetiredIdentities)
    {
        var files = new Dictionary<string, ManifestFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ManifestFile file in entries)
        {
            if (file is null)
            {
                throw new InvalidDataException("Release manifest contains an empty create-only default entry.");
            }
            file.Path = PathSafety.NormalizeRelativePath(file.Path);
            if ((!PathSafety.IsSeedAllowed(file.Path)
                    && !verifiedRetiredIdentities.Contains(HistoricalManifestPolicy.Identity(file)))
                || file.Size < 0
                || !files.TryAdd(file.Path, file))
            {
                throw new InvalidDataException($"Release manifest contains an unsafe or duplicate create-only default path: {file.Path}");
            }
            ValidateHash(file.Sha256, $"create-only default {file.Path}");
        }
        return files;
    }

    private static HashSet<string> ValidateReofferSeedPaths(
        IList<string> entries,
        IReadOnlyDictionary<string, ManifestFile> seedFiles)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entries.Count; index++)
        {
            string normalized = PathSafety.NormalizeRelativePath(entries[index]);
            entries[index] = normalized;
            if (!PathSafety.IsSeedAllowed(normalized)
                || !paths.Add(normalized)
                || !seedFiles.ContainsKey(normalized))
            {
                throw new InvalidDataException(
                    $"Release manifest contains an unsafe, duplicate, or undeclared re-offered default path: {normalized}");
            }
        }
        return paths;
    }

    private static void ValidateSeedTextReplacements(
        IList<SeedTextReplacement> entries,
        IReadOnlyDictionary<string, ManifestFile> seedFiles,
        IReadOnlySet<string> reofferSeedPaths)
    {
        const string optionsMigrationId = "options-contest-tracker-k-collision-v1";
        const string contestTrackerK = "key_key.companion_bonds.open_contest_tracker:key.keyboard.k";
        const string irisToggleK = "key_iris.keybind.toggleShaders:key.keyboard.k";
        const string irisToggleUnbound = "key_iris.keybind.toggleShaders:key.keyboard.unknown";
        const string fancyToastsK = "key_key.fancytoasts.config_menu:key.keyboard.k";
        const string fancyToastsUnbound = "key_key.fancytoasts.config_menu:key.keyboard.unknown";
        var identities = new HashSet<string>(StringComparer.Ordinal);
        int optionsReplacementCount = 0;
        bool hasIrisToggleReplacement = false;
        bool hasFancyToastsReplacement = false;
        foreach (SeedTextReplacement replacement in entries)
        {
            if (replacement is null)
            {
                throw new InvalidDataException("Release manifest contains an empty seed text replacement.");
            }
            replacement.Path = PathSafety.NormalizeRelativePath(replacement.Path);
            bool isIris = string.Equals(
                replacement.Path,
                "config/iris.properties",
                StringComparison.OrdinalIgnoreCase);
            bool isOptions = string.Equals(
                replacement.Path,
                "options.txt",
                StringComparison.OrdinalIgnoreCase);
            bool validText = IsSafeReplacementLine(replacement.OldText)
                && IsSafeReplacementLine(replacement.NewText)
                && !string.Equals(replacement.OldText, replacement.NewText, StringComparison.Ordinal);
            bool validIrisReplacement = isIris
                && replacement.RequiredLines is { Count: 0 }
                && string.IsNullOrEmpty(replacement.MigrationId)
                && replacement.OldText.StartsWith("shaderPack=", StringComparison.Ordinal)
                && replacement.NewText.StartsWith("shaderPack=", StringComparison.Ordinal);
            bool validOptionsReplacement = isOptions
                && replacement.RequiredLines is { Count: 1 }
                && string.Equals(replacement.MigrationId, optionsMigrationId, StringComparison.Ordinal)
                && PathSafety.IsPlayerSettingMigrationIdAllowed(replacement.MigrationId)
                && string.Equals(replacement.RequiredLines[0], contestTrackerK, StringComparison.Ordinal)
                && ((string.Equals(replacement.OldText, irisToggleK, StringComparison.Ordinal)
                        && string.Equals(replacement.NewText, irisToggleUnbound, StringComparison.Ordinal))
                    || (string.Equals(replacement.OldText, fancyToastsK, StringComparison.Ordinal)
                        && string.Equals(replacement.NewText, fancyToastsUnbound, StringComparison.Ordinal)));
            string identity = replacement.Path.ToUpperInvariant() + "\0" + replacement.OldText;
            if (!PathSafety.IsSeedTextReplacementAllowed(replacement.Path)
                || !seedFiles.ContainsKey(replacement.Path)
                || (isIris && !reofferSeedPaths.Contains(replacement.Path))
                || (isOptions && reofferSeedPaths.Contains(replacement.Path))
                || !identities.Add(identity)
                || !validText
                || !(validIrisReplacement || validOptionsReplacement))
            {
                throw new InvalidDataException(
                    $"Release manifest contains an unsafe, duplicate, or undeclared seed text replacement: {replacement.Path}");
            }
            if (validOptionsReplacement)
            {
                optionsReplacementCount++;
                hasIrisToggleReplacement |= string.Equals(replacement.OldText, irisToggleK, StringComparison.Ordinal);
                hasFancyToastsReplacement |= string.Equals(replacement.OldText, fancyToastsK, StringComparison.Ordinal);
            }
        }
        if (optionsReplacementCount is not 0
            && (optionsReplacementCount != 2
                || !hasIrisToggleReplacement
                || !hasFancyToastsReplacement))
        {
            throw new InvalidDataException(
                "The signed options migration must contain the complete reviewed Iris and Fancy Toasts repair pair.");
        }
    }

    private static bool IsSafeReplacementLine(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 4096
        && !value.Contains('\0')
        && !value.Contains('\n')
        && !value.Contains('\r');

    private static void ValidateSchemaOne(
        UpdateManifest manifest,
        UpdaterConfiguration configuration,
        IReadOnlyDictionary<string, ManifestFile> files,
        IReadOnlyDictionary<string, ManifestFile> seedFiles,
        IReadOnlySet<string> reofferSeedPaths)
    {
        if (manifest.Base is not null
            || manifest.PayloadFiles.Count != 0
            || manifest.DeletedFiles.Count != 0
            || reofferSeedPaths.Count != 0
            || manifest.SeedTextReplacements.Count != 0)
        {
            throw new InvalidDataException("Schema 1 releases cannot contain delta-only fields, re-offered defaults, or seed text replacements.");
        }

        var seenDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < manifest.DeletePaths.Count; index++)
        {
            string normalized = PathSafety.NormalizeRelativePath(manifest.DeletePaths[index]);
            manifest.DeletePaths[index] = normalized;
            if (!PathSafety.IsAllowed(normalized, configuration.AllowedRoots)
                || !seenDeletes.Add(normalized)
                || files.ContainsKey(normalized)
                || seedFiles.ContainsKey(normalized))
            {
                throw new InvalidDataException($"Release manifest contains an unsafe, duplicate, or overlapping deletion path: {normalized}");
            }
        }

        HashSet<string> legacyPaths = ValidateLegacyCleanup(manifest.LegacyCleanup, configuration);
        if (legacyPaths.Any(files.ContainsKey) || legacyPaths.Any(seedFiles.ContainsKey))
        {
            throw new InvalidDataException("Release manifest contains a legacy cleanup path that overlaps a managed file.");
        }
    }

    private static void ValidateSchemaTwo(
        UpdateManifest manifest,
        UpdaterConfiguration configuration,
        IReadOnlyDictionary<string, ManifestFile> files,
        IReadOnlyDictionary<string, ManifestFile> seedFiles,
        Version releaseVersion,
        Version requiredUpdater,
        IReadOnlyDictionary<string, Uri> assetUrls)
    {
        if (manifest.Base is null
            || !VersionPolicy.TryParseCanonical(manifest.Base.Version, out Version? baseVersion)
            || baseVersion >= releaseVersion)
        {
            throw new InvalidDataException("Schema 2 release has an invalid or non-older base version.");
        }
        ValidateHash(manifest.Base.ManifestSha256, "base manifest");
        if (manifest.DeletePaths.Count != 0)
        {
            throw new InvalidDataException("Schema 2 releases must use exact deletedFiles entries instead of path-only deletions.");
        }

        Dictionary<string, ManifestFile> payloadFiles = ValidateFileSet(manifest.PayloadFiles, configuration, "payload file");
        Dictionary<string, ManifestFile> deletedFiles = ValidateFileSet(manifest.DeletedFiles, configuration, "deleted file");
        if (payloadFiles.Count == 0 && deletedFiles.Count == 0 && seedFiles.Count == 0)
        {
            throw new InvalidDataException("Schema 2 release contains no changes.");
        }

        bool hasPayloadEntries = payloadFiles.Count != 0 || seedFiles.Count != 0;
        if (!hasPayloadEntries)
        {
            if (manifest.Payload is not null)
            {
                throw new InvalidDataException("Deletion-only schema 2 releases must not declare a payload.");
            }
        }
        else
        {
            if (manifest.Payload is null)
            {
                throw new InvalidDataException("Schema 2 release with changed/new files is missing its payload.");
            }
            ValidatePayload(manifest.Payload, assetUrls);
        }

        foreach ((string path, ManifestFile payloadFile) in payloadFiles)
        {
            if (!files.TryGetValue(path, out ManifestFile? postFile) || !SameFile(payloadFile, postFile))
            {
                throw new InvalidDataException($"Delta payload file does not exactly match the authoritative post-state: {path}");
            }
        }
        if (deletedFiles.Keys.Any(files.ContainsKey)
            || deletedFiles.Keys.Any(seedFiles.ContainsKey)
            || payloadFiles.Keys.Any(seedFiles.ContainsKey))
        {
            throw new InvalidDataException("Delta deletedFiles overlap the authoritative post-state.");
        }
        HashSet<string> legacyPaths = ValidateLegacyCleanup(manifest.LegacyCleanup, configuration);
        var refreshableSeeds = manifest.ReofferSeedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool repairsExactManagedPayload = legacyPaths.Any(payloadFiles.ContainsKey);
        if (repairsExactManagedPayload && requiredUpdater < new Version(1, 2, 13))
        {
            throw new InvalidDataException("Repairing an exact legacy identity of a changed managed file requires updater 1.2.13 or newer.");
        }
        if (legacyPaths.Any(path => seedFiles.ContainsKey(path) && refreshableSeeds.Contains(path))
            && requiredUpdater < new Version(1, 2, 10))
        {
            throw new InvalidDataException("Refreshing an exact older seed requires updater 1.2.10 or newer.");
        }
        foreach (LegacyCleanupFile legacyFile in manifest.LegacyCleanup)
        {
            if (payloadFiles.TryGetValue(legacyFile.Path, out ManifestFile? payloadFile)
                && legacyFile.Size == payloadFile.Size
                && string.Equals(legacyFile.Sha256, payloadFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"A managed repair identity cannot equal its signed replacement: {legacyFile.Path}");
            }
        }
        if (legacyPaths.Any(path => files.ContainsKey(path) && !payloadFiles.ContainsKey(path))
            || legacyPaths.Any(deletedFiles.ContainsKey)
            || legacyPaths.Any(path => seedFiles.ContainsKey(path) && !refreshableSeeds.Contains(path)))
        {
            throw new InvalidDataException("Delta legacy cleanup overlaps its signed managed file sets.");
        }
    }

    private static HashSet<string> ValidateLegacyCleanup(
        IEnumerable<LegacyCleanupFile> entries,
        UpdaterConfiguration configuration)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LegacyCleanupFile file in entries)
        {
            if (file is null)
            {
                throw new InvalidDataException("Release manifest contains an empty legacy cleanup entry.");
            }
            file.Path = PathSafety.NormalizeRelativePath(file.Path);
            if (!PathSafety.IsAllowed(file.Path, configuration.AllowedRoots)
                || file.Size < 0)
            {
                throw new InvalidDataException($"Release manifest contains an unsafe legacy cleanup entry: {file.Path}");
            }
            ValidateHash(file.Sha256, $"legacy cleanup file {file.Path}");
            string identity = $"{file.Path}\0{file.Size}\0{file.Sha256}";
            if (!identities.Add(identity))
            {
                throw new InvalidDataException($"Release manifest contains a duplicate legacy cleanup identity: {file.Path}");
            }
            paths.Add(file.Path);
        }
        return paths;
    }

    internal static bool SameFile(ManifestFile left, ManifestFile right) =>
        left.Size == right.Size
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    internal static bool SameFile(ManagedFileState left, ManifestFile right) =>
        left.Size == right.Size
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateHash(string hash, string item)
    {
        if (string.IsNullOrWhiteSpace(hash)
            || hash.Length != 64
            || hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"Release manifest contains an invalid SHA-256 for {item}.");
        }
    }
}
