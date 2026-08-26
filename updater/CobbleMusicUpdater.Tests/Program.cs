using System.Security.Cryptography;
using CobbleMusicUpdater;

internal static class Program
{
    private static readonly CancellationToken NoCancellation = CancellationToken.None;

    public static async Task<int> Main()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "cobble-updater-delta-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            TestManifestSchemaTwoValidation();
            TestSequentialReleaseChain();
            await TestExactBaselineAdoptionAsync(Path.Combine(tempRoot, "adoption"));
            await TestExactDeltaBaseValidationAsync(Path.Combine(tempRoot, "delta"));
            await TestJournalCommitBoundaryAsync(Path.Combine(tempRoot, "journal"));
            Console.WriteLine("Schema-v2 delta, release-chain, exact-baseline adoption, base-integrity, and journal commit-boundary checks passed.");
            return 0;
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static void TestManifestSchemaTwoValidation()
    {
        UpdaterConfiguration configuration = Configuration();
        UpdateManifest manifest = DeltaManifest();
        ManifestParser.Validate(manifest, configuration, AssetUrls());

        UpdateManifest unsafeManifest = DeltaManifest();
        unsafeManifest.DeletedFiles[0].Path = "../outside.jar";
        Throws<InvalidDataException>(() => ManifestParser.Validate(unsafeManifest, configuration, AssetUrls()));

        UpdateManifest overlapManifest = DeltaManifest();
        overlapManifest.DeletedFiles[0] = Copy(overlapManifest.Files[0]);
        Throws<InvalidDataException>(() => ManifestParser.Validate(overlapManifest, configuration, AssetUrls()));

        UpdateManifest nonExactPayload = DeltaManifest();
        nonExactPayload.PayloadFiles[0].Sha256 = HashText("not-the-post-file");
        Throws<InvalidDataException>(() => ManifestParser.Validate(nonExactPayload, configuration, AssetUrls()));

        UpdateManifest deletionOnly = DeltaManifest();
        deletionOnly.Payload = null;
        deletionOnly.PayloadFiles = [];
        ManifestParser.Validate(deletionOnly, configuration, new Dictionary<string, Uri>());
        deletionOnly.Payload = new UpdatePayload
        {
            ArchiveName = "empty.zip",
            Size = 1,
            Sha256 = HashText("empty"),
            Parts = [new PayloadPart { Name = "empty.part001", Size = 1, Sha256 = HashText("empty-part") }]
        };
        Throws<InvalidDataException>(() => ManifestParser.Validate(
            deletionOnly,
            configuration,
            new Dictionary<string, Uri> { ["empty.part001"] = new Uri("https://example.invalid/empty.part001") }));
    }

    private static void TestSequentialReleaseChain()
    {
        RemoteRelease baseline = Remote("1.0.4", 1, HashText("manifest-104"));
        RemoteRelease delta105 = Remote("1.0.5", 2, HashText("manifest-105"), baseline);
        RemoteRelease delta106 = Remote("1.0.6", 2, HashText("manifest-106"), delta105);
        RemoteRelease unreachable = Remote("9.0.0", 2, HashText("manifest-900"), baseVersion: "8.0.0", baseHash: HashText("missing"));
        var state = new InstalledState
        {
            Version = baseline.Manifest.Version,
            ManifestSha256 = baseline.ManifestSha256
        };

        IReadOnlyList<RemoteRelease> chain = ReleaseClient.BuildSequentialChain(
            [unreachable, delta106, baseline, delta105],
            state);
        Equal("1.0.4,1.0.5,1.0.6", string.Join(',', chain.Select(release => release.Manifest.Version)), "installed chain");

        IReadOnlyList<RemoteRelease> freshChain = ReleaseClient.BuildSequentialChain(
            [unreachable, delta106, baseline, delta105],
            new InstalledState());
        Equal("1.0.4,1.0.5,1.0.6", string.Join(',', freshChain.Select(release => release.Manifest.Version)), "fresh chain");

        IReadOnlyList<RemoteRelease> noBaseline = ReleaseClient.BuildSequentialChain([delta105, delta106], new InstalledState());
        Equal(0, noBaseline.Count, "v2-only fresh install must not jump into a delta");
    }

    private static async Task TestExactDeltaBaseValidationAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mods/unchanged.jar"] = "unchanged",
            ["mods/changed.jar"] = "old-changed",
            ["mods/deleted.jar"] = "delete-me"
        };
        foreach ((string relative, string content) in contents)
        {
            string target = PathSafety.CombineUnder(paths.MinecraftDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content);
        }

        UpdateManifest signedBase = BaselineManifest(contents);
        string baseHash = HashText("signed-base-manifest");
        InstalledState state = StateFrom(signedBase, baseHash);
        UpdateManifest delta = DeltaFromBase(signedBase, baseHash);
        await DeltaValidator.ValidateBaseAsync(delta, signedBase, baseHash, state, paths, Configuration(), NoCancellation);

        string unchangedPath = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/unchanged.jar");
        await File.WriteAllTextAsync(unchangedPath, "UNCHANGED"); // same length, different hash
        await ThrowsAsync<InvalidDataException>(() => DeltaValidator.ValidateBaseAsync(
            delta, signedBase, baseHash, state, paths, Configuration(), NoCancellation));
        await File.WriteAllTextAsync(unchangedPath, "unchanged");

        UpdateManifest redundant = DeltaFromBase(signedBase, baseHash);
        redundant.PayloadFiles.Add(Copy(signedBase.Files.Single(file => file.Path == "mods/unchanged.jar")));
        await ThrowsAsync<InvalidDataException>(() => DeltaValidator.ValidateBaseAsync(
            redundant, signedBase, baseHash, state, paths, Configuration(), NoCancellation));
    }

    private static async Task TestExactBaselineAdoptionAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        ManifestFile first = FileEntry("mods/first.jar", "first");
        ManifestFile second = FileEntry("mods/second.jar", "second");
        var baseline = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.4",
            Files = [first, second],
            DeletePaths = ["mods/obsolete.jar"],
            LegacyCleanup =
            [
                new LegacyCleanupFile
                {
                    Path = "mods/legacy.jar",
                    Size = 6,
                    Sha256 = HashText("legacy")
                }
            ]
        };
        foreach ((ManifestFile file, string content) in new[] { (first, "first"), (second, "second") })
        {
            string target = PathSafety.CombineUnder(paths.MinecraftDirectory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content);
        }

        string manifestHash = HashText("baseline-manifest");
        var engine = new UpdateEngine(paths, Configuration(), _ => { });
        bool adopted = await engine.TryAdoptExistingBaselineAsync(
            baseline, manifestHash, new InstalledState(), NoCancellation);
        Equal(true, adopted, "exact baseline adoption");
        InstalledState state = LocalStateStore.LoadState(paths);
        Equal("1.0.4", state.Version, "adopted baseline version");
        Equal(2, state.ManagedFiles.Count, "adopted full inventory");

        File.Delete(paths.StatePath);
        string legacy = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/legacy.jar");
        await File.WriteAllTextAsync(legacy, "legacy");
        bool adoptedWithCleanupPending = await engine.TryAdoptExistingBaselineAsync(
            baseline, manifestHash, new InstalledState(), NoCancellation);
        Equal(false, adoptedWithCleanupPending, "baseline with pending cleanup must download/apply");

        File.Delete(legacy);
        string secondPath = PathSafety.CombineUnder(paths.MinecraftDirectory, second.Path);
        await File.WriteAllTextAsync(secondPath, "SECOND");
        bool adoptedWithChangedFile = await engine.TryAdoptExistingBaselineAsync(
            baseline, manifestHash, new InstalledState(), NoCancellation);
        Equal(false, adoptedWithChangedFile, "changed same-size file must not be adopted");
    }

    private static async Task TestJournalCommitBoundaryAsync(string root)
    {
        UpdaterPaths rollbackPaths = Paths(Path.Combine(root, "rollback"));
        Directory.CreateDirectory(rollbackPaths.MinecraftDirectory);
        Directory.CreateDirectory(rollbackPaths.InstallationDirectory);
        string target = PathSafety.CombineUnder(rollbackPaths.MinecraftDirectory, "mods/example.jar");
        string backup = Path.Combine(rollbackPaths.LocalDataDirectory, "rollback", "tx", "files", "mods", "example.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllTextAsync(target, "new");
        await File.WriteAllTextAsync(backup, "old");
        InstalledState previous = StateFor("1.0.4", HashText("base"), "mods/example.jar", "old");
        InstalledState next = StateFor("1.0.5", HashText("next"), "mods/example.jar", "new");
        await LocalStateStore.SaveStateAsync(rollbackPaths, previous, NoCancellation);
        await TransactionStore.SaveAsync(rollbackPaths, Journal(target, backup, previous, next, "filesApplied"), NoCancellation);
        await TransactionStore.RecoverIfNeededAsync(rollbackPaths, BuildInfo.SupportedRoots, _ => { });
        Equal("old", await File.ReadAllTextAsync(target), "old state must roll files back");
        Equal("1.0.4", LocalStateStore.LoadState(rollbackPaths).Version, "rollback state");

        UpdaterPaths committedPaths = Paths(Path.Combine(root, "committed"));
        Directory.CreateDirectory(committedPaths.MinecraftDirectory);
        Directory.CreateDirectory(committedPaths.InstallationDirectory);
        target = PathSafety.CombineUnder(committedPaths.MinecraftDirectory, "mods/example.jar");
        backup = Path.Combine(committedPaths.LocalDataDirectory, "rollback", "tx", "files", "mods", "example.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllTextAsync(target, "new");
        await File.WriteAllTextAsync(backup, "old");
        await LocalStateStore.SaveStateAsync(committedPaths, next, NoCancellation);
        await TransactionStore.SaveAsync(committedPaths, Journal(target, backup, previous, next, "filesApplied"), NoCancellation);
        await TransactionStore.RecoverIfNeededAsync(committedPaths, BuildInfo.SupportedRoots, _ => { });
        Equal("new", await File.ReadAllTextAsync(target), "new state is the durable commit point");
        Equal(false, File.Exists(TransactionStore.JournalPath(committedPaths)), "committed journal cleanup");

        UpdaterPaths plannedPaths = Paths(Path.Combine(root, "planned"));
        Directory.CreateDirectory(plannedPaths.MinecraftDirectory);
        Directory.CreateDirectory(plannedPaths.InstallationDirectory);
        target = PathSafety.CombineUnder(plannedPaths.MinecraftDirectory, "mods/example.jar");
        backup = Path.Combine(plannedPaths.LocalDataDirectory, "rollback", "tx", "files", "mods", "example.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "old");
        await LocalStateStore.SaveStateAsync(plannedPaths, previous, NoCancellation);
        await TransactionStore.SaveAsync(plannedPaths, Journal(target, backup, previous, next, "applying"), NoCancellation);
        await TransactionStore.RecoverIfNeededAsync(plannedPaths, BuildInfo.SupportedRoots, _ => { });
        Equal("old", await File.ReadAllTextAsync(target), "planned operation without backup must retain original");

        UpdaterPaths corruptStatePaths = Paths(Path.Combine(root, "corrupt-state"));
        Directory.CreateDirectory(corruptStatePaths.MinecraftDirectory);
        Directory.CreateDirectory(corruptStatePaths.InstallationDirectory);
        target = PathSafety.CombineUnder(corruptStatePaths.MinecraftDirectory, "mods/example.jar");
        backup = Path.Combine(corruptStatePaths.LocalDataDirectory, "rollback", "tx", "files", "mods", "example.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllTextAsync(target, "new");
        await File.WriteAllTextAsync(backup, "old");
        await TransactionStore.SaveAsync(corruptStatePaths, Journal(target, backup, previous, next, "filesApplied"), NoCancellation);
        await File.WriteAllTextAsync(corruptStatePaths.StatePath, "{not valid json");
        await ThrowsAsync<TransactionRecoveryException>(() =>
            TransactionStore.RecoverIfNeededAsync(corruptStatePaths, BuildInfo.SupportedRoots, _ => { }));
        Equal(true, File.Exists(TransactionStore.JournalPath(corruptStatePaths)), "corrupt state must retain journal and block launch");
        Equal("new", await File.ReadAllTextAsync(target), "corrupt state must not guess rollback or commit");
    }

    private static TransactionJournal Journal(
        string target,
        string backup,
        InstalledState previous,
        InstalledState next,
        string phase) => new()
        {
            Phase = phase,
            PreviousState = previous,
            NextState = next,
            Operations =
            [
                new TransactionOperation
                {
                    Kind = "replace",
                    TargetPath = target,
                    BackupPath = backup,
                    OriginalSize = 3,
                    OriginalSha256 = HashText("old")
                }
            ]
        };

    private static UpdateManifest BaselineManifest(IReadOnlyDictionary<string, string> contents) => new()
    {
        SchemaVersion = 1,
        Version = "1.0.4",
        Files = contents.Select(pair => FileEntry(pair.Key, pair.Value)).ToList()
    };

    private static UpdateManifest DeltaFromBase(UpdateManifest signedBase, string baseHash)
    {
        ManifestFile unchanged = Copy(signedBase.Files.Single(file => file.Path == "mods/unchanged.jar"));
        ManifestFile changed = FileEntry("mods/changed.jar", "new-changed");
        ManifestFile added = FileEntry("mods/added.jar", "new-file");
        ManifestFile deleted = Copy(signedBase.Files.Single(file => file.Path == "mods/deleted.jar"));
        return new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.5",
            Base = new ManifestBase { Version = signedBase.Version, ManifestSha256 = baseHash },
            Files = [unchanged, changed, added],
            PayloadFiles = [Copy(changed), Copy(added)],
            DeletedFiles = [deleted]
        };
    }

    private static UpdateManifest DeltaManifest() => new()
    {
        SchemaVersion = 2,
        ModpackId = "cobble-music",
        Channel = "stable",
        Version = "1.0.5",
        ReleaseTag = "modpack-v1.0.5",
        MinimumUpdaterVersion = "1.2.0",
        Base = new ManifestBase { Version = "1.0.4", ManifestSha256 = HashText("base") },
        Payload = new UpdatePayload
        {
            ArchiveName = "delta.zip",
            Size = 1,
            Sha256 = HashText("payload"),
            Parts = [new PayloadPart { Name = "delta.part001", Size = 1, Sha256 = HashText("part") }]
        },
        Files = [FileEntry("mods/changed.jar", "new"), FileEntry("mods/added.jar", "added")],
        PayloadFiles = [FileEntry("mods/changed.jar", "new"), FileEntry("mods/added.jar", "added")],
        DeletedFiles = [FileEntry("mods/deleted.jar", "old")]
    };

    private static RemoteRelease Remote(
        string version,
        int schema,
        string hash,
        RemoteRelease? baseRelease = null,
        string? baseVersion = null,
        string? baseHash = null)
    {
        var manifest = new UpdateManifest
        {
            SchemaVersion = schema,
            Version = version,
            ReleaseTag = "modpack-v" + version,
            Payload = new UpdatePayload { Size = 1 }
        };
        if (schema == 2)
        {
            manifest.Base = new ManifestBase
            {
                Version = baseRelease?.Manifest.Version ?? baseVersion ?? "",
                ManifestSha256 = baseRelease?.ManifestSha256 ?? baseHash ?? ""
            };
        }
        return new RemoteRelease(
            new GitHubRelease { TagName = manifest.ReleaseTag },
            [],
            [],
            new Dictionary<string, Uri>(),
            manifest,
            hash);
    }

    private static InstalledState StateFrom(UpdateManifest manifest, string manifestHash) => new()
    {
        Version = manifest.Version,
        ManifestSha256 = manifestHash,
        ManagedFiles = manifest.Files.Select(file => new ManagedFileState
        {
            Path = file.Path,
            Size = file.Size,
            Sha256 = file.Sha256
        }).ToList()
    };

    private static InstalledState StateFor(string version, string manifestHash, string path, string content) => new()
    {
        Version = version,
        ManifestSha256 = manifestHash,
        ManagedFiles =
        [
            new ManagedFileState { Path = path, Size = content.Length, Sha256 = HashText(content) }
        ]
    };

    private static ManifestFile FileEntry(string path, string content) => new()
    {
        Path = path,
        Size = content.Length,
        Sha256 = HashText(content)
    };

    private static ManifestFile Copy(ManifestFile file) => new()
    {
        Path = file.Path,
        Size = file.Size,
        Sha256 = file.Sha256
    };

    private static UpdaterConfiguration Configuration() => new()
    {
        AllowedRoots = ["mods"]
    };

    private static Dictionary<string, Uri> AssetUrls() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["delta.part001"] = new Uri("https://example.invalid/delta.part001")
    };

    private static UpdaterPaths Paths(string root)
    {
        string instance = Path.Combine(root, "instance");
        string minecraft = Path.Combine(instance, "minecraft");
        string install = Path.Combine(minecraft, "cobble-music-updater");
        string local = Path.Combine(root, "local");
        return new UpdaterPaths(instance, minecraft, install, Path.Combine(install, "updater.json"), Path.Combine(install, "state.json"), local);
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Equal<T>(T expected, T actual, string context)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{context}: expected {expected}, got {actual}");
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
