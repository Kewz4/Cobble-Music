using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Drawing;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Forms;
using CobbleMusicUpdater;

internal static class Program
{
    private static readonly CancellationToken NoCancellation = CancellationToken.None;

    public static async Task<int> Main(string[] args)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "cobble-updater-delta-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            if (args.Length == 2 && args[0] == "--log-regressions")
            {
                TestPublishedHistoricalManifests(args[1]);
                await TestPreexistingDeltaPayloadAsync(Path.Combine(tempRoot, "existing-payload"));
                await TestHistoricalShaderSettingsChainAsync(Path.Combine(tempRoot, "shader-chain"), args[1]);
                Console.WriteLine("Log regressions passed: real signed historical manifests, exact pre-existing payload adoption, unknown-copy rejection, retired-default skipping, and old-state preservation.");
                return 0;
            }
            TestCalculatedUpdaterWindowLayoutAt120Dpi();
            TestUpdaterWindowLayout();
            TestAggregateDownloadProgressAndTransferMetrics();
            TestManifestSchemaTwoValidation();
            TestCreateOnlyDefaultManifestValidation();
            TestUpdaterChannelValidation();
            TestSequentialReleaseChain();
            TestInstanceIdentityNormalization(Path.Combine(tempRoot, "identity"));
            TestLegacyConfigurationRootMigration(Path.Combine(tempRoot, "configuration-migration"));
            TestOfflineLaunchPolicy();
            TestResumeStagingPreparation(Path.Combine(tempRoot, "staging"));
            await TestExtractAndVerifyReleasesDestinationHandleAsync(Path.Combine(tempRoot, "extract-lock"));
            await TestReusableAssembledArchiveAsync(Path.Combine(tempRoot, "assembled-retry"));
            await TestAdversarialAssetDownloadsAsync(Path.Combine(tempRoot, "downloads"));
            await TestPaginatedReleaseAssetsAsync();
            await TestExactBaselineAdoptionAsync(Path.Combine(tempRoot, "adoption"));
            await TestExactDeltaBaseValidationAsync(Path.Combine(tempRoot, "delta"));
            await TestDeltaApplyTimeValidationAsync(Path.Combine(tempRoot, "delta-toctou"));
            await TestSchemaOneSanitizedRecoveryAsync(Path.Combine(tempRoot, "sanitized-baseline"));
            await TestCreateOnlyDefaultsAsync(Path.Combine(tempRoot, "create-only-defaults"));
            await TestCorrectiveSeedRefreshAndAdoptionAsync(Path.Combine(tempRoot, "corrective-seed-refresh"));
            await TestConditionalKeyCollisionMigrationAsync(Path.Combine(tempRoot, "key-collision-migration"));
            await TestJournalCommitBoundaryAsync(Path.Combine(tempRoot, "journal"));
            await TestCrossVolumeTransactionRecoveryAsync(Path.Combine(tempRoot, "cross-volume"));
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

    private static void TestPublishedHistoricalManifests(string directory)
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFiles(directory, "cobble-music-update.json", SearchOption.AllDirectories))
        {
            UpdateManifest manifest = ManifestParser.VerifyAndParse(File.ReadAllBytes(path),
                File.ReadAllBytes(Path.ChangeExtension(path, ".sig")));
            var urls = (manifest.Payload?.Parts ?? []).ToDictionary(part => part.Name,
                part => new Uri("https://example.invalid/" + part.Name), StringComparer.OrdinalIgnoreCase);
            ManifestParser.Validate(manifest, new UpdaterConfiguration(), urls);
            if (Version.Parse(manifest.Version) >= new Version(1, 0, 7)
                && Version.Parse(manifest.Version) <= new Version(1, 0, 12))
            {
                Equal(18, manifest.VerifiedPlayerOwnedIdentities.Count, "all historical mutable shader sidecars recognized");
                ManifestFile setting = manifest.Files.First(file => HistoricalManifestPolicy.IsPlayerOwned(manifest, file));
                ManifestFile changed = Copy(setting);
                changed.Size++;
                Equal(false, HistoricalManifestPolicy.IsPlayerOwned(manifest, changed), "exception bound to exact signed identity");
                var unsigned = JsonSerializer.Deserialize<UpdateManifest>(JsonSerializer.Serialize(manifest))!;
                Equal(0, unsigned.VerifiedPlayerOwnedIdentities.Count, "unsigned JSON cannot grant ownership exceptions");
                Equal(false, manifest.Files.Any(file => file.Path.EndsWith(".zip")
                    && HistoricalManifestPolicy.IsPlayerOwned(manifest, file)), "shader archives remain integrity checked");
            }
            if (manifest.VerifiedRetiredSeedIdentities.Count > 0)
            {
                ManifestFile retired = manifest.SeedFiles.First(file => !PathSafety.IsSeedAllowed(file.Path));
                retired.Size++;
                Throws<InvalidDataException>(() => ManifestParser.Validate(manifest, new UpdaterConfiguration(), urls));
            }
            count++;
        }
        Equal(true, count >= 10, "all published historical manifests exercised");
    }

    private static async Task TestPreexistingDeltaPayloadAsync(string root)
    {
        var (paths, signedBase, delta, state, extract) = await PrepareDeltaApplyFixtureAsync(root);
        string added = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/added.jar");
        await File.WriteAllTextAsync(added, "new-file");
        state.OfferedSeedPaths.Add("config/cobbreeding/encryption");
        await LocalStateStore.SaveStateAsync(paths, state, NoCancellation);
        Equal(state.Version, LocalStateStore.LoadState(paths).Version, "old retired ledger keeps installed version");
        delta.SeedFiles.Add(FileEntry("config/cobbreeding/encryption", "never-install"));
        var engine = new UpdateEngine(paths, Configuration(), _ => { });
        await DeltaValidator.ValidateBaseAsync(delta, signedBase, delta.Base!.ManifestSha256,
            state, paths, Configuration(), NoCancellation);
        await engine.ApplyTransactionAsync(delta, HashText("delta-manifest"), extract, state, signedBase, NoCancellation);
        Equal(delta.Version, LocalStateStore.LoadState(paths).Version, "matching pre-existing new file commits");
        Equal("new-file", await File.ReadAllTextAsync(added), "matching pre-existing file retained");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, "config/cobbreeding/encryption")), "retired secret never installed");
        Equal(false, File.Exists(TransactionStore.JournalPath(paths)), "successful migration clears journal");

        var other = await PrepareDeltaApplyFixtureAsync(root + "-unknown");
        string unknown = PathSafety.CombineUnder(other.Paths.MinecraftDirectory, "mods/added.jar");
        await File.WriteAllTextAsync(unknown, "own-file");
        var otherEngine = new UpdateEngine(other.Paths, Configuration(), _ => { });
        await ThrowsAsync<InvalidDataException>(() => otherEngine.ApplyTransactionAsync(other.Delta,
            HashText("delta-manifest"), other.Extract, other.State, other.SignedBase, NoCancellation));
        Equal("own-file", await File.ReadAllTextAsync(unknown), "different same-size local copy preserved");
        Equal(other.State.Version, LocalStateStore.LoadState(other.Paths).Version, "collision rollback retains state");
    }

    private static async Task TestHistoricalShaderSettingsChainAsync(string root, string publishedDirectory)
    {
        string publishedPath = Path.Combine(publishedDirectory, "1.0.7", "cobble-music-update.json");
        UpdateManifest published = ManifestParser.VerifyAndParse(File.ReadAllBytes(publishedPath),
            File.ReadAllBytes(Path.ChangeExtension(publishedPath, ".sig")));
        string[] settings = published.Files.Where(file => HistoricalManifestPolicy.IsPlayerOwned(published, file))
            .Select(file => file.Path).ToArray();
        UpdaterPaths paths = Paths(root);
        var configuration = new UpdaterConfiguration();
        var engine = new UpdateEngine(paths, configuration, _ => { });
        var previous = new UpdateManifest { SchemaVersion = 1, Version = "1.0.6", Files = [FileEntry("mods/required.jar", "required")] };
        string previousHash = HashText("base");
        var state = StateFrom(previous, previousHash);
        Directory.CreateDirectory(Path.Combine(paths.MinecraftDirectory, "mods"));
        Directory.CreateDirectory(Path.Combine(paths.MinecraftDirectory, "shaderpacks"));
        await File.WriteAllTextAsync(Path.Combine(paths.MinecraftDirectory, "mods/required.jar"), "required");
        for (int i = 0; i < settings.Length - 1; i++)
            await File.WriteAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, settings[i]), "custom-player-setting-" + i);
        await LocalStateStore.SaveStateAsync(paths, state, NoCancellation);

        for (int version = 7; version <= 13; version++)
        {
            string extract = Path.Combine(root, "extract-" + version);
            Directory.CreateDirectory(Path.Combine(extract, "shaderpacks"));
            var next = new UpdateManifest
            {
                SchemaVersion = 2, Version = "1.0." + version,
                Base = new ManifestBase { Version = previous.Version, ManifestSha256 = previousHash },
                Files = [Copy(previous.Files[0])]
            };
            // Change settings in .7/.8, then carry them unchanged until ownership
            // is officially corrected. One sidecar is deliberately absent.
            foreach (string path in settings)
            {
                ManifestFile file = FileEntry(path, "stock-" + Math.Min(version, 8));
                if (version < 13)
                {
                    next.Files.Add(file);
                    next.VerifiedPlayerOwnedIdentities.Add(HistoricalManifestPolicy.Identity(file));
                    if (version <= 8)
                    {
                        next.PayloadFiles.Add(Copy(file));
                        await File.WriteAllTextAsync(PathSafety.CombineUnder(extract, path), "stock-" + version);
                    }
                }
                else
                {
                    next.SeedFiles.Add(file);
                    next.ReofferSeedPaths.Add(path);
                    ManifestFile old = previous.Files.Single(entry => entry.Path == path);
                    next.LegacyCleanup.Add(new LegacyCleanupFile { Path = path, Size = old.Size, Sha256 = old.Sha256 });
                    await File.WriteAllTextAsync(PathSafety.CombineUnder(extract, path), "stock-8");
                }
            }
            await DeltaValidator.ValidateBaseAsync(next, previous, previousHash, state, paths, configuration, NoCancellation);
            string nextHash = HashText("manifest-" + version);
            await engine.ApplyTransactionAsync(next, nextHash, extract, state, previous, NoCancellation);
            state = LocalStateStore.LoadState(paths);
            Equal(next.Version, state.Version, "historical settings transaction commits " + version);
            Equal(true, engine.StateMatchesManifestAndSizes(state, next), "historical settings do not trigger repair " + version);
            for (int i = 0; i < settings.Length - 1; i++)
                Equal("custom-player-setting-" + i, await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, settings[i])), "custom settings preserved " + version);
            Equal(version == 13, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, settings[^1])), "missing sidecar offered only at ownership transition");
            previous = next;
            previousHash = nextHash;
        }
        Equal(1, state.ManagedFiles.Count, "final state manages only required mod");
        Console.WriteLine("Shader settings regression passed: all 18 real sidecar paths, changed/unchanged historical deltas, missing setting, ownership transition, and restart integrity.");
    }

    private static void TestCalculatedUpdaterWindowLayoutAt120Dpi()
    {
        UpdateStatusLayout layout = UpdateStatusForm.CalculateLayout(
            dpi: 120,
            titlePreferredHeight: 35,
            subtitlePreferredHeight: 20,
            statusPreferredHeight: 23,
            detailPreferredHeight: 19,
            closePreferredSize: new Size(100, 35),
            showCloseButton: false);

        Equal(new Size(650, 218), layout.ClientSize, "120 DPI client size");
        Equal(new Rectangle(31, 28, 588, 35), layout.TitleBounds, "120 DPI title bounds");
        Equal(new Rectangle(32, 67, 587, 21), layout.SubtitleBounds, "120 DPI subtitle bounds");
        Equal(new Rectangle(31, 107, 588, 29), layout.StatusBounds, "120 DPI status bounds");
        Equal(new Rectangle(32, 137, 587, 22), layout.DetailBounds, "120 DPI detail bounds");
        Equal(new Rectangle(32, 175, 586, 10), layout.ProgressBounds, "120 DPI progress bounds");
        Equal(new Rectangle(518, 175, 100, 10), layout.CloseBounds, "120 DPI hidden close bounds");

        UpdateStatusLayout failureLayout = UpdateStatusForm.CalculateLayout(
            dpi: 120,
            titlePreferredHeight: 35,
            subtitlePreferredHeight: 20,
            statusPreferredHeight: 23,
            detailPreferredHeight: 19,
            closePreferredSize: new Size(100, 35),
            showCloseButton: true);
        Equal(new Size(650, 221), failureLayout.ClientSize, "120 DPI failure client size");
        Equal(new Rectangle(518, 175, 100, 35), failureLayout.CloseBounds, "120 DPI visible close bounds");

        UpdateStatusLayout largeButtonLayout = UpdateStatusForm.CalculateLayout(
            dpi: 120,
            titlePreferredHeight: 35,
            subtitlePreferredHeight: 20,
            statusPreferredHeight: 23,
            detailPreferredHeight: 19,
            closePreferredSize: new Size(600, 60),
            showCloseButton: true);
        Equal(new Size(664, 246), largeButtonLayout.ClientSize, "large close button grows client bounds");
        Equal(new Rectangle(32, 175, 600, 60), largeButtonLayout.CloseBounds, "large close button remains inside client bounds");
    }

    private static void TestAggregateDownloadProgressAndTransferMetrics()
    {
        const long mib = 1024L * 1024L;
        var scope = new DownloadProgressScope(500 * mib);
        scope.ExcludeSkippedPayload(100 * mib);
        scope.CompletePart(100 * mib, 80 * mib);
        UpdateProgress secondPart = scope.ForPart(new PartDownloadProgress(50 * mib, 30 * mib));
        Equal(150 * mib, secondPart.CompletedBytes, "multipart progress stays aggregate");
        Equal(400 * mib, secondPart.TotalBytes, "multipart total stays aggregate");
        Equal(110 * mib, secondPart.NetworkBytes, "multipart network bytes stay aggregate");
        scope.CompletePart(50 * mib, 30 * mib);
        UpdateProgress nextRelease = scope.ForPart(new PartDownloadProgress(25 * mib, 20 * mib));
        Equal(175 * mib, nextRelease.CompletedBytes, "multi-release progress does not reset");
        Equal(400 * mib, nextRelease.TotalBytes, "multi-release total does not reset");
        Equal(130 * mib, nextRelease.NetworkBytes, "multi-release network byte count does not reset");

        var tracker = new TransferMetricsTracker(timestampFrequency: 1_000);
        UpdateProgress start = secondPart with
        {
            CompletedBytes = 128 * mib,
            NetworkBytes = 64 * mib
        };
        Equal(false, tracker.Observe(start, timestamp: 1_000).HasEstimate, "first transfer sample waits for timing data");

        UpdateProgress later = secondPart with
        {
            CompletedBytes = 192 * mib,
            NetworkBytes = 128 * mib
        };
        TransferMetrics metrics = tracker.Observe(later, timestamp: 3_000);
        Equal(true, metrics.HasEstimate, "later transfer sample has speed and ETA");
        Equal(32D * mib, metrics.BytesPerSecond, "aggregate transfer speed");
        Equal(TimeSpan.FromSeconds(6.5), metrics.EstimatedTimeRemaining!.Value, "aggregate transfer ETA");
        Equal(
            "192.0 MiB / 400.0 MiB • 32.0 MiB/s • ETA 7s",
            TransferMetricsFormatter.FormatDownloadDetail(later, metrics),
            "concise transfer detail formatting");

        tracker.Reset();
        Equal(
            false,
            tracker.Observe(start with { NetworkBytes = 0 }, timestamp: 10_000).HasEstimate,
            "reset transfer tracker starts a fresh estimate");

        var resumedTracker = new TransferMetricsTracker(timestampFrequency: 1_000);
        Equal(false, resumedTracker.Observe(start, timestamp: 20_000).HasEstimate, "resumed transfer seeds network baseline");
        Equal(
            false,
            resumedTracker.Observe(start with { CompletedBytes = 192 * mib }, timestamp: 22_000).HasEstimate,
            "existing-byte jump does not inflate download speed");
        TransferMetrics resumedMetrics = resumedTracker.Observe(
            start with { CompletedBytes = 224 * mib, NetworkBytes = 96 * mib },
            timestamp: 23_000);
        Equal(32D * mib, resumedMetrics.BytesPerSecond, "resumed transfer counts only new network bytes");
        Equal(
            "128.0 MiB / 400.0 MiB • Calculating speed and ETA…",
            TransferMetricsFormatter.FormatDownloadDetail(start, default),
            "initial transfer detail includes total size");
        Equal("1h 01m", TransferMetricsFormatter.FormatDuration(TimeSpan.FromSeconds(3660)), "long ETA formatting");
    }

    private static void TestUpdaterWindowLayout()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (!Application.SetHighDpiMode(HighDpiMode.PerMonitorV2))
                {
                    throw new InvalidOperationException("Could not enable PerMonitorV2 for the isolated updater UI test.");
                }
                using UpdateStatusForm form = CreateStatusForm();
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-10_000, -10_000);
                _ = form.Handle;

                float appliedFontScale = 1F;
                int scaleStep = 0;
                foreach (float fontScale in new[] { 1F, 1.25F, 1.5F, 2F, 1F })
                {
                    scaleStep++;
                    ScaleControlFonts(form, fontScale / appliedFontScale);
                    appliedFontScale = fontScale;
                    form.PerformLayout();
                    AssertStatusLayout(form, $"{fontScale:0.##}x font scale at step {scaleStep}");
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("The isolated updater UI layout test did not finish within 15 seconds.");
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static UpdateStatusForm CreateStatusForm() => new(
        new CommandLine("test-instance", "test-minecraft", PrismPrelaunch: true, CheckOnly: false, NoUi: false),
        (_, _) => Task.FromResult(0));

    private static void ScaleControlFonts(Control parent, float factor)
    {
        foreach (Control control in parent.Controls)
        {
            control.Font = new Font(
                control.Font.FontFamily,
                control.Font.SizeInPoints * factor,
                control.Font.Style,
                GraphicsUnit.Point);
        }
    }

    private static void AssertStatusLayout(UpdateStatusForm form, string context)
    {
        Equal(AutoScaleMode.Dpi, form.AutoScaleMode, $"{context}: DPI autoscaling mode");
        int minimumDpiWidth = (int)Math.Round(520 * form.DeviceDpi / 96D);
        Equal(true, form.ClientSize.Width >= minimumDpiWidth, $"{context}: DPI-scaled minimum width");

        Control title = FindControl(form, "titleLabel");
        Control subtitle = FindControl(form, "subtitleLabel");
        Control status = FindControl(form, "statusLabel");
        Control detail = FindControl(form, "detailLabel");
        Control progress = FindControl(form, "progressIndicator");
        Control close = FindControl(form, "closeButton");

        Size closePreferredSize = close.GetPreferredSize(Size.Empty);
        UpdateStatusLayout expected = UpdateStatusForm.CalculateLayout(
            form.DeviceDpi,
            title.GetPreferredSize(new Size(title.Width, 0)).Height,
            subtitle.GetPreferredSize(new Size(subtitle.Width, 0)).Height,
            status.GetPreferredSize(new Size(status.Width, 0)).Height,
            detail.GetPreferredSize(new Size(detail.Width, 0)).Height,
            closePreferredSize,
            showCloseButton: false);
        Equal(expected.ClientSize, form.ClientSize, $"{context}: exact client size");
        Equal(expected.TitleBounds, title.Bounds, $"{context}: exact title bounds");
        Equal(expected.SubtitleBounds, subtitle.Bounds, $"{context}: exact subtitle bounds");
        Equal(expected.StatusBounds, status.Bounds, $"{context}: exact status bounds");
        Equal(expected.DetailBounds, detail.Bounds, $"{context}: exact detail bounds");
        Equal(expected.ProgressBounds, progress.Bounds, $"{context}: exact progress bounds");
        Equal(expected.CloseBounds, close.Bounds, $"{context}: exact close bounds");

        foreach (Control control in new[] { title, subtitle, status, detail, progress, close })
        {
            Equal(true, form.ClientRectangle.Contains(control.Bounds), $"{context}: {control.Name} inside client bounds");
        }

        Equal(true, title.Bottom <= subtitle.Top, $"{context}: title/subtitle separation");
        Equal(true, subtitle.Bottom <= status.Top, $"{context}: subtitle/status separation");
        Equal(true, status.Bottom <= detail.Top, $"{context}: status/detail separation");
        Equal(true, detail.Bottom <= progress.Top, $"{context}: detail/progress separation");
        Equal(true, detail.Bottom <= close.Top, $"{context}: detail/close separation");

        foreach (Control label in new[] { title, subtitle, status, detail })
        {
            int preferredHeight = label.GetPreferredSize(new Size(label.Width, 0)).Height;
            Equal(true, label.Height >= preferredHeight, $"{context}: {label.Name} text height");
        }
        UpdateStatusLayout failureLayout = UpdateStatusForm.CalculateLayout(
            form.DeviceDpi,
            title.GetPreferredSize(new Size(title.Width, 0)).Height,
            subtitle.GetPreferredSize(new Size(subtitle.Width, 0)).Height,
            status.GetPreferredSize(new Size(status.Width, 0)).Height,
            detail.GetPreferredSize(new Size(detail.Width, 0)).Height,
            closePreferredSize,
            showCloseButton: true);
        Equal(true, failureLayout.CloseBounds.Width >= closePreferredSize.Width, $"{context}: visible close button text width");
        Equal(true, failureLayout.CloseBounds.Height >= closePreferredSize.Height, $"{context}: visible close button text height");
        Equal(
            true,
            new Rectangle(Point.Empty, failureLayout.ClientSize).Contains(failureLayout.CloseBounds),
            $"{context}: visible close button inside failure client bounds");

        using var regionBitmap = new Bitmap(Math.Max(1, form.ClientSize.Width), Math.Max(1, form.ClientSize.Height));
        using Graphics graphics = Graphics.FromImage(regionBitmap);
        RectangleF regionBounds = form.Region?.GetBounds(graphics)
            ?? throw new InvalidOperationException($"{context}: rounded form region is missing.");
        Equal(true, Math.Abs(regionBounds.Width - form.ClientSize.Width) <= 1F, $"{context}: region width tracks client width");
        Equal(true, Math.Abs(regionBounds.Height - form.ClientSize.Height) <= 1F, $"{context}: region height tracks client height");
    }

    private static Control FindControl(Control parent, string name)
    {
        Control[] matches = parent.Controls.Find(name, searchAllChildren: true);
        if (matches.Length != 1)
        {
            throw new InvalidOperationException($"Expected one updater UI control named {name}, found {matches.Length}.");
        }
        return matches[0];
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

        UpdateManifest leadingZeroRelease = DeltaManifest();
        leadingZeroRelease.Version = "01.0.5";
        leadingZeroRelease.ReleaseTag = "modpack-v01.0.5";
        Throws<InvalidDataException>(() => ManifestParser.Validate(leadingZeroRelease, configuration, AssetUrls()));

        UpdateManifest fourPartRelease = DeltaManifest();
        fourPartRelease.Version = "1.0.5.0";
        fourPartRelease.ReleaseTag = "modpack-v1.0.5.0";
        Throws<InvalidDataException>(() => ManifestParser.Validate(fourPartRelease, configuration, AssetUrls()));

        UpdateManifest leadingZeroBase = DeltaManifest();
        leadingZeroBase.Base!.Version = "1.00.4";
        Throws<InvalidDataException>(() => ManifestParser.Validate(leadingZeroBase, configuration, AssetUrls()));

        UpdateManifest leadingZeroMinimum = DeltaManifest();
        leadingZeroMinimum.MinimumUpdaterVersion = "1.02.0";
        Throws<InvalidDataException>(() => ManifestParser.Validate(leadingZeroMinimum, configuration, AssetUrls()));

        Equal(true, VersionPolicy.TryParseCanonical("0.0.0", out Version? zeroVersion), "canonical zero version");
        Equal(new Version(0, 0, 0), zeroVersion, "canonical zero parse result");
        Equal(false, VersionPolicy.TryParseCanonical("1.2", out _), "two-component version rejected");
        Equal(false, VersionPolicy.TryParseCanonical("1.2.3.4", out _), "four-component version rejected");
        Equal(false, VersionPolicy.TryParseCanonical("1.02.3", out _), "leading zero version rejected");
    }

    private static void TestCreateOnlyDefaultManifestValidation()
    {
        UpdaterConfiguration configuration = Configuration();
        UpdateManifest valid = DeltaManifest();
        valid.MinimumUpdaterVersion = BuildInfo.Version;
        valid.SeedFiles =
        [
            FileEntry("options.txt", "video-and-keybind-defaults"),
            FileEntry("config/musicnotification.json", "notification-defaults"),
            FileEntry("mods/Axiom-5.4.2-for-MC1.21.1.jar", "optional-builder-mod")
        ];
        valid.ReofferSeedPaths = ["config/musicnotification.json"];
        ManifestParser.Validate(valid, configuration, AssetUrls());
        Equal(5, ManifestParser.PayloadContents(valid).Count, "managed payload plus create-only defaults");
        Equal(2, ManifestParser.ManagedPayloadContents(valid).Count, "managed payload excludes defaults");

        var shaderConfiguration = new UpdaterConfiguration { AllowedRoots = ["mods", "shaderpacks"] };
        UpdateManifest shaderRelease = DeltaManifest();
        ManifestFile shaderFile = FileEntry("shaderpacks/High Quality/shaders/program.glsl", "shader-source");
        shaderRelease.Files.Add(shaderFile);
        shaderRelease.PayloadFiles.Add(Copy(shaderFile));
        ManifestParser.Validate(shaderRelease, shaderConfiguration, AssetUrls());
        Equal(true, BuildInfo.SupportedRoots.Contains("shaderpacks"), "shaderpacks compiled update root");

        var correctiveConfiguration = new UpdaterConfiguration { AllowedRoots = ["mods", "shaderpacks", "config"] };
        UpdateManifest corrective = DeltaManifest();
        corrective.MinimumUpdaterVersion = BuildInfo.Version;
        corrective.SeedFiles =
        [
            FileEntry("shaderpacks/Max Quality.zip.txt", "new-shader-options"),
            FileEntry("config/iris.properties", "shaderPack=Max Quality.zip\n")
        ];
        corrective.ReofferSeedPaths = corrective.SeedFiles.Select(file => file.Path).ToList();
        corrective.SeedTextReplacements =
        [
            new SeedTextReplacement
            {
                Path = "config/iris.properties",
                OldText = "shaderPack=Max Quality",
                NewText = "shaderPack=Max Quality.zip"
            }
        ];
        corrective.LegacyCleanup =
        [
            new LegacyCleanupFile { Path = "shaderpacks/Max Quality.zip.txt", Size = 10, Sha256 = HashText("old-a") },
            new LegacyCleanupFile { Path = "shaderpacks/Max Quality.zip.txt", Size = 11, Sha256 = HashText("old-b") }
        ];
        ManifestParser.Validate(corrective, correctiveConfiguration, AssetUrls());

        UpdateManifest duplicateCleanupIdentity = CloneManifest(corrective);
        duplicateCleanupIdentity.LegacyCleanup.Add(new LegacyCleanupFile
        {
            Path = corrective.LegacyCleanup[0].Path,
            Size = corrective.LegacyCleanup[0].Size,
            Sha256 = corrective.LegacyCleanup[0].Sha256
        });
        Throws<InvalidDataException>(() => ManifestParser.Validate(duplicateCleanupIdentity, correctiveConfiguration, AssetUrls()));

        UpdateManifest oldUpdaterTextMigration = CloneManifest(corrective);
        oldUpdaterTextMigration.MinimumUpdaterVersion = "1.2.9";
        Throws<InvalidDataException>(() => ManifestParser.Validate(oldUpdaterTextMigration, correctiveConfiguration, AssetUrls()));

        UpdateManifest undeclaredTextMigration = CloneManifest(corrective);
        undeclaredTextMigration.SeedTextReplacements[0].Path = "config/not-a-seed.properties";
        Throws<InvalidDataException>(() => ManifestParser.Validate(undeclaredTextMigration, correctiveConfiguration, AssetUrls()));

        const string contestTrackerK = "key_key.companion_bonds.open_contest_tracker:key.keyboard.k";
        const string optionsMigrationId = "options-contest-tracker-k-collision-v1";
        UpdateManifest optionsTextMigration = DeltaManifest();
        optionsTextMigration.MinimumUpdaterVersion = BuildInfo.Version;
        optionsTextMigration.SeedFiles = [FileEntry("options.txt", "player-options")];
        optionsTextMigration.SeedTextReplacements =
        [
            new SeedTextReplacement
            {
                Path = "options.txt",
                OldText = "key_iris.keybind.toggleShaders:key.keyboard.k",
                NewText = "key_iris.keybind.toggleShaders:key.keyboard.unknown",
                MigrationId = optionsMigrationId,
                RequiredLines = [contestTrackerK]
            },
            new SeedTextReplacement
            {
                Path = "options.txt",
                OldText = "key_key.fancytoasts.config_menu:key.keyboard.k",
                NewText = "key_key.fancytoasts.config_menu:key.keyboard.unknown",
                MigrationId = optionsMigrationId,
                RequiredLines = [contestTrackerK]
            }
        ];
        ManifestParser.Validate(optionsTextMigration, correctiveConfiguration, AssetUrls());

        UpdateManifest oldUpdaterOptionsMigration = CloneManifest(optionsTextMigration);
        oldUpdaterOptionsMigration.MinimumUpdaterVersion = "1.2.10";
        Throws<InvalidDataException>(() => ManifestParser.Validate(oldUpdaterOptionsMigration, correctiveConfiguration, AssetUrls()));

        UpdateManifest unconditionalOptionsMigration = CloneManifest(optionsTextMigration);
        unconditionalOptionsMigration.SeedTextReplacements[0].RequiredLines = [];
        Throws<InvalidDataException>(() => ManifestParser.Validate(unconditionalOptionsMigration, correctiveConfiguration, AssetUrls()));

        UpdateManifest partialOptionsMigration = CloneManifest(optionsTextMigration);
        partialOptionsMigration.SeedTextReplacements.RemoveAt(1);
        Throws<InvalidDataException>(() => ManifestParser.Validate(partialOptionsMigration, correctiveConfiguration, AssetUrls()));

        UpdateManifest replayableOptionsMigration = CloneManifest(optionsTextMigration);
        replayableOptionsMigration.SeedTextReplacements[0].MigrationId = "";
        Throws<InvalidDataException>(() => ManifestParser.Validate(replayableOptionsMigration, correctiveConfiguration, AssetUrls()));

        UpdateManifest reofferedOptionsMigration = CloneManifest(optionsTextMigration);
        reofferedOptionsMigration.ReofferSeedPaths = ["options.txt"];
        Throws<InvalidDataException>(() => ManifestParser.Validate(reofferedOptionsMigration, correctiveConfiguration, AssetUrls()));

        UpdateManifest arbitraryOptionsMigration = CloneManifest(optionsTextMigration);
        arbitraryOptionsMigration.SeedTextReplacements[0].OldText = "renderDistance:8";
        arbitraryOptionsMigration.SeedTextReplacements[0].NewText = "renderDistance:4";
        Throws<InvalidDataException>(() => ManifestParser.Validate(arbitraryOptionsMigration, correctiveConfiguration, AssetUrls()));

        UpdateManifest nonOptionalMod = DeltaManifest();
        nonOptionalMod.SeedFiles = [FileEntry("mods/required-mod.jar", "not-optional")];
        Throws<InvalidDataException>(() => ManifestParser.Validate(nonOptionalMod, configuration, AssetUrls()));

        UpdateManifest unsafeRoot = DeltaManifest();
        unsafeRoot.SeedFiles = [FileEntry("servers.dat", "private-server-list")];
        Throws<InvalidDataException>(() => ManifestParser.Validate(unsafeRoot, configuration, AssetUrls()));

        UpdateManifest browserState = DeltaManifest();
        browserState.SeedFiles = [FileEntry("config/MCBrowser/tabs.json", "private-browser-state")];
        Throws<InvalidDataException>(() => ManifestParser.Validate(browserState, configuration, AssetUrls()));

        UpdateManifest credentialState = DeltaManifest();
        credentialState.SeedFiles = [FileEntry("config/dreamdisplays/config.toml", "private-service-credential")];
        Throws<InvalidDataException>(() => ManifestParser.Validate(credentialState, configuration, AssetUrls()));

        UpdateManifest encryptionState = DeltaManifest();
        encryptionState.SeedFiles = [FileEntry("config/cobbreeding/encryption", "generated-aes-key")];
        Throws<InvalidDataException>(() => ManifestParser.Validate(encryptionState, configuration, AssetUrls()));

        UpdateManifest usernameCacheState = DeltaManifest();
        usernameCacheState.SeedFiles = [FileEntry("config/jade/usernamecache.json", "cached-usernames")];
        Throws<InvalidDataException>(() => ManifestParser.Validate(usernameCacheState, configuration, AssetUrls()));

        foreach (string generatedPath in new[]
        {
            "config/example.toml.bak1",
            "config/example.toml.old1",
            "config/inventory-particles/cache/generated.bin",
            "config/zoomify.json",
            "config/dreamdisplays/config.yml",
            "config/packed_packs/__version.json",
            "config/etf_warnings.json",
            "config/sodium-fingerprint.json",
            "config/spark/activity.json",
            "config/spark/tmp/about.txt",
            "config/spark/tmp-client/about.txt"
        })
        {
            UpdateManifest generatedState = DeltaManifest();
            generatedState.SeedFiles = [FileEntry(generatedPath, "generated-runtime-state")];
            Throws<InvalidDataException>(() => ManifestParser.Validate(generatedState, configuration, AssetUrls()));
        }

        UpdateManifest overlap = DeltaManifest();
        overlap.SeedFiles = [Copy(overlap.Files[0])];
        Throws<InvalidDataException>(() => ManifestParser.Validate(overlap, configuration, AssetUrls()));

        UpdateManifest deletedOverlap = DeltaManifest();
        deletedOverlap.SeedFiles = [Copy(deletedOverlap.DeletedFiles[0])];
        Throws<InvalidDataException>(() => ManifestParser.Validate(deletedOverlap, configuration, AssetUrls()));

        UpdateManifest undeclaredReoffer = DeltaManifest();
        undeclaredReoffer.ReofferSeedPaths = ["config/missing-default.json"];
        Throws<InvalidDataException>(() => ManifestParser.Validate(undeclaredReoffer, configuration, AssetUrls()));

        UpdateManifest duplicateReoffer = DeltaManifest();
        duplicateReoffer.SeedFiles = [FileEntry("config/profile.json", "profile")];
        duplicateReoffer.ReofferSeedPaths = ["config/profile.json", "CONFIG/PROFILE.JSON"];
        Throws<InvalidDataException>(() => ManifestParser.Validate(duplicateReoffer, configuration, AssetUrls()));

        UpdateManifest oldUpdaterReoffer = DeltaManifest();
        oldUpdaterReoffer.MinimumUpdaterVersion = "1.2.8";
        oldUpdaterReoffer.SeedFiles = [FileEntry("config/profile.json", "profile")];
        oldUpdaterReoffer.ReofferSeedPaths = ["config/profile.json"];
        Throws<InvalidDataException>(() => ManifestParser.Validate(oldUpdaterReoffer, configuration, AssetUrls()));
    }

    private static void TestUpdaterChannelValidation()
    {
        UpdaterChannelDescriptor valid = ChannelDescriptor();
        UpdaterChannelParser.Validate(valid);
        string canonical = System.Text.Encoding.UTF8.GetString(UpdaterChannelParser.SerializeCanonical(valid));
        Equal(
            "{\"schemaVersion\":1,\"productId\":\"cobble-music-updater\",\"repository\":\"Kewz4/Cobble-Music\",\"channel\":\"stable\",\"updaterVersion\":\"1.2.7\",\"releaseTag\":\"updater-v1.2.7\",\"updater\":{\"name\":\"CobbleMusicUpdater.exe\",\"size\":1048576,\"sha256\":\"" + new string('a', 64) + "\"}}",
            canonical,
            "canonical updater channel serialization");

        UpdaterChannelDescriptor leadingZero = ChannelDescriptor();
        leadingZero.UpdaterVersion = "1.02.7";
        leadingZero.ReleaseTag = "updater-v1.02.7";
        Throws<InvalidDataException>(() => UpdaterChannelParser.Validate(leadingZero));

        UpdaterChannelDescriptor mismatchedTag = ChannelDescriptor();
        mismatchedTag.ReleaseTag = "updater-v1.2.8";
        Throws<InvalidDataException>(() => UpdaterChannelParser.Validate(mismatchedTag));

        UpdaterChannelDescriptor arbitraryRepository = ChannelDescriptor();
        arbitraryRepository.Repository = "attacker/repository";
        Throws<InvalidDataException>(() => UpdaterChannelParser.Validate(arbitraryRepository));

        UpdaterChannelDescriptor arbitraryAsset = ChannelDescriptor();
        arbitraryAsset.Updater!.Name = "different.exe";
        Throws<InvalidDataException>(() => UpdaterChannelParser.Validate(arbitraryAsset));

        UpdaterChannelDescriptor uppercaseHash = ChannelDescriptor();
        uppercaseHash.Updater!.Sha256 = new string('A', 64);
        Throws<InvalidDataException>(() => UpdaterChannelParser.Validate(uppercaseHash));
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

        RemoteRelease longBaseline105 = Remote("1.0.5", 1, HashText("long-manifest-105"));
        RemoteRelease longDelta106 = Remote("1.0.6", 2, HashText("long-manifest-106"), longBaseline105);
        RemoteRelease longDelta107 = Remote("1.0.7", 2, HashText("long-manifest-107"), longDelta106);
        RemoteRelease longDelta108 = Remote("1.0.8", 2, HashText("long-manifest-108"), longDelta107);
        var pristine101 = new InstalledState { Version = "1.0.1" };
        IReadOnlyList<RemoteRelease> catchUpFrom101 = ReleaseClient.BuildSequentialChain(
            [longDelta108, longDelta106, longBaseline105, longDelta107],
            pristine101);
        Equal(
            "1.0.5,1.0.6,1.0.7,1.0.8",
            string.Join(',', catchUpFrom101.Select(release => release.Manifest.Version)),
            "1.0.1 player catches up through the full baseline and every exact delta");

        IReadOnlyList<RemoteRelease> catchUpFrom106 = ReleaseClient.BuildSequentialChain(
            [longDelta108, longDelta106, longBaseline105, longDelta107],
            new InstalledState
            {
                Version = longDelta106.Manifest.Version,
                ManifestSha256 = longDelta106.ManifestSha256
            });
        Equal(
            "1.0.6,1.0.7,1.0.8",
            string.Join(',', catchUpFrom106.Select(release => release.Manifest.Version)),
            "managed 1.0.6 player resumes from its exact signed anchor");

        RemoteRelease periodicFull108 = Remote("1.0.8", 1, HashText("full-manifest-108"));
        IReadOnlyList<RemoteRelease> directPeriodicBaseline = ReleaseClient.BuildSequentialChain(
            [longBaseline105, longDelta106, longDelta107, periodicFull108],
            pristine101);
        Equal(
            "1.0.8",
            string.Join(',', directPeriodicBaseline.Select(release => release.Manifest.Version)),
            "newer periodic full baseline lets an old player jump directly to latest");
    }

    private static void TestInstanceIdentityNormalization(string root)
    {
        string instance = Path.Combine(root, "MixedCaseInstance");
        string minecraft = Path.Combine(instance, "minecraft");
        Directory.CreateDirectory(minecraft);
        UpdaterPaths canonical = LocalStateStore.ResolvePaths(instance, minecraft);
        UpdaterPaths caseAndSeparatorVariant = LocalStateStore.ResolvePaths(
            instance.ToUpperInvariant() + Path.DirectorySeparatorChar,
            minecraft.ToUpperInvariant() + Path.DirectorySeparatorChar);
        Equal(canonical.LocalDataDirectory, caseAndSeparatorVariant.LocalDataDirectory, "case-insensitive instance identity");
        Equal(
            Path.TrimEndingDirectorySeparator(canonical.InstanceDirectory),
            canonical.InstanceDirectory,
            "resolved instance path must not retain a trailing separator");
    }

    private static void TestLegacyConfigurationRootMigration(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.InstallationDirectory);
        string legacy = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modpackId = BuildInfo.DefaultModpackId,
            repository = BuildInfo.DefaultRepository,
            channel = "stable",
            manifestAsset = "cobble-music-update.json",
            signatureAsset = "cobble-music-update.sig",
            networkTimeoutSeconds = 30,
            allowOfflineLaunch = true,
            allowedRoots = new[] { "mods", "resourcepacks", "config", "defaultconfigs", "kubejs", "scripts" }
        });
        File.WriteAllText(paths.ConfigurationPath, legacy);
        UpdaterConfiguration migrated = LocalStateStore.LoadConfiguration(paths);
        Equal(true, migrated.AllowedRoots.Contains("shaderpacks"), "immutable 1.2.7 bootstrap config gains shaderpacks in memory");

        string narrowed = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modpackId = BuildInfo.DefaultModpackId,
            repository = BuildInfo.DefaultRepository,
            channel = "stable",
            manifestAsset = "cobble-music-update.json",
            signatureAsset = "cobble-music-update.sig",
            networkTimeoutSeconds = 30,
            allowOfflineLaunch = true,
            allowedRoots = new[] { "mods" }
        });
        File.WriteAllText(paths.ConfigurationPath, narrowed);
        UpdaterConfiguration custom = LocalStateStore.LoadConfiguration(paths);
        Equal(false, custom.AllowedRoots.Contains("shaderpacks"), "deliberately narrowed custom config remains narrow");

        string oldState = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "1.0.6",
            manifestSha256 = HashText("legacy-state"),
            managedFiles = new[]
            {
                new { path = "mods/required.jar", size = 8, sha256 = HashText("required") }
            }
        });
        File.WriteAllText(paths.StatePath, oldState);
        InstalledState migratedState = LocalStateStore.LoadState(paths);
        Equal("1.0.6", migratedState.Version, "pre-ledger installed state remains valid");
        Equal(0, migratedState.OfferedSeedPaths.Count, "missing old ledger deserializes as empty");
        Equal(0, migratedState.AppliedPlayerSettingMigrationIds.Count, "missing old migration ledger deserializes as empty");

        string nullLedgerState = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "1.0.6",
            manifestSha256 = HashText("legacy-state"),
            managedFiles = Array.Empty<object>(),
            offeredSeedPaths = (string[]?)null
        });
        File.WriteAllText(paths.StatePath, nullLedgerState);
        Equal("", LocalStateStore.LoadState(paths).Version, "explicit-null seed ledger is rejected safely");

        string nullMigrationLedgerState = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "1.0.6",
            manifestSha256 = HashText("legacy-state"),
            managedFiles = Array.Empty<object>(),
            offeredSeedPaths = Array.Empty<string>(),
            appliedPlayerSettingMigrationIds = (string[]?)null
        });
        File.WriteAllText(paths.StatePath, nullMigrationLedgerState);
        Equal("", LocalStateStore.LoadState(paths).Version, "explicit-null player-setting migration ledger is rejected safely");
    }

    private static void TestOfflineLaunchPolicy()
    {
        var configuration = Configuration();
        configuration.AllowOfflineLaunch = true;
        Equal(0, CobbleMusicUpdater.Program.NetworkFailureExitCode(configuration, new HttpRequestException("offline")), "allowed offline launch");
        configuration.AllowOfflineLaunch = false;
        Equal(1, CobbleMusicUpdater.Program.NetworkFailureExitCode(configuration, new TimeoutException("offline")), "blocked offline launch");
        Throws<ArgumentException>(() => CobbleMusicUpdater.Program.NetworkFailureExitCode(configuration, new InvalidDataException("not network")));
    }

    private static void TestResumeStagingPreparation(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        UpdateManifest manifest = DeltaManifest();
        manifest.Payload = new UpdatePayload
        {
            ArchiveName = "resume.zip",
            Size = 19,
            Sha256 = HashText("resume-payload"),
            Parts =
            [
                new PayloadPart { Name = "keep.part001", Size = 10, Sha256 = HashText("keep") },
                new PayloadPart { Name = "full.part002", Size = 5, Sha256 = HashText("full") },
                new PayloadPart { Name = "oversized.part003", Size = 4, Sha256 = HashText("oversized") }
            ]
        };
        string manifestHash = HashText("resume-manifest");
        string work = Path.Combine(paths.LocalDataDirectory, "staging", manifestHash);
        string parts = Path.Combine(work, "parts");
        Directory.CreateDirectory(parts);
        File.WriteAllText(Path.Combine(parts, "keep.part001"), "abc");
        File.WriteAllText(Path.Combine(parts, "full.part002"), "12345");
        File.WriteAllText(Path.Combine(parts, "oversized.part003"), "12345");
        File.WriteAllText(Path.Combine(parts, "unexpected.part"), "junk");
        Directory.CreateDirectory(Path.Combine(parts, "unexpected-dir"));
        File.WriteAllText(Path.Combine(parts, "unexpected-dir", "junk"), "junk");
        File.WriteAllText(Path.Combine(work, "payload.zip"), "stale archive");
        Directory.CreateDirectory(Path.Combine(work, "extracted"));
        File.WriteAllText(Path.Combine(work, "extracted", "stale"), "stale");
        File.WriteAllText(Path.Combine(work, "unexpected-root"), "stale");

        var engine = new UpdateEngine(paths, Configuration(), _ => { });
        StagingPreparation preparation = engine.PrepareStagingAndEnsureSufficientDiskSpace(
            manifest,
            manifestHash,
            new InstalledState());
        Equal(8L, preparation.ReusablePartBytes, "retained partial/full resume bytes");
        Equal(
            preparation.TotalLocalRequiredBytes - preparation.ReusablePartBytes,
            preparation.AdditionalLocalRequiredBytes,
            "resume bytes must reduce additional disk requirement");
        Equal(true, File.Exists(Path.Combine(parts, "keep.part001")), "partial expected part retained");
        Equal(true, File.Exists(Path.Combine(parts, "full.part002")), "full expected part retained");
        Equal(false, File.Exists(Path.Combine(parts, "oversized.part003")), "oversized expected part removed");
        Equal(false, File.Exists(Path.Combine(parts, "unexpected.part")), "unexpected part removed");
        Equal(false, Directory.Exists(Path.Combine(parts, "unexpected-dir")), "unexpected part directory removed");
        Equal(false, File.Exists(Path.Combine(work, "payload.zip")), "stale archive removed");
        Equal(false, Directory.Exists(Path.Combine(work, "extracted")), "stale extraction removed");
        Equal(false, File.Exists(Path.Combine(work, "unexpected-root")), "unexpected staging entry removed");
    }

    private static async Task TestExtractAndVerifyReleasesDestinationHandleAsync(string root)
    {
        Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, "payload.zip");
        string extractDirectory = Path.Combine(root, "extracted");
        const string relativePath = "config/nested/music.json";
        byte[] content = System.Text.Encoding.UTF8.GetBytes("{\"track\":\"Lavender Town\"}");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry(relativePath, CompressionLevel.NoCompression);
            await using Stream entryOutput = entry.Open();
            await entryOutput.WriteAsync(content, NoCancellation);
        }

        var expected = new ManifestFile
        {
            Path = relativePath,
            Size = content.LongLength,
            Sha256 = HashBytes(content)
        };
        await UpdateEngine.ExtractAndVerifyAsync(
            archivePath,
            extractDirectory,
            [expected],
            NoCancellation);

        string extractedPath = PathSafety.CombineUnder(extractDirectory, relativePath);
        Equal(HashBytes(content), await PathSafety.Sha256Async(extractedPath, NoCancellation), "real ZIP extraction verifies written bytes after closing its destination handle");
        using var exclusive = new FileStream(extractedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Equal(content.LongLength, exclusive.Length, "real ZIP extraction leaves no destination handle open");
    }

    private static async Task TestReusableAssembledArchiveAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        UpdateManifest manifest = DeltaManifest();
        string manifestHash = HashText("assembled-retry-manifest");
        string work = Path.Combine(paths.LocalDataDirectory, "staging", manifestHash);
        Directory.CreateDirectory(work);
        string archivePath = Path.Combine(work, "payload.zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("mods/example.jar", CompressionLevel.NoCompression);
            await using Stream entryOutput = entry.Open();
            await entryOutput.WriteAsync("verified payload"u8.ToArray(), NoCancellation);
        }
        byte[] archiveBytes = await File.ReadAllBytesAsync(archivePath, NoCancellation);
        manifest.Payload = new UpdatePayload
        {
            ArchiveName = "cobble-music-payload.zip",
            Size = archiveBytes.LongLength,
            Sha256 = HashBytes(archiveBytes),
            Parts =
            [
                new PayloadPart
                {
                    Name = "payload.part001",
                    Size = archiveBytes.LongLength,
                    Sha256 = HashBytes(archiveBytes)
                }
            ]
        };

        var engine = new UpdateEngine(paths, Configuration(), _ => { });
        StagingPreparation preparation = engine.PrepareStagingAndEnsureSufficientDiskSpace(
            manifest,
            manifestHash,
            new InstalledState());
        Equal(archiveBytes.LongLength, preparation.ReusableArchiveBytes, "matching-size assembled archive is retained for signed verification");
        Equal(true, File.Exists(archivePath), "assembled archive survives retry staging cleanup");
        Equal(
            true,
            await UpdateEngine.TryReuseVerifiedArchiveAsync(archivePath, manifest.Payload, NoCancellation),
            "exact signed assembled archive is reusable without downloading parts again");

        byte[] corruptBytes = new byte[archiveBytes.Length];
        await File.WriteAllBytesAsync(archivePath, corruptBytes, NoCancellation);
        preparation = engine.PrepareStagingAndEnsureSufficientDiskSpace(
            manifest,
            manifestHash,
            new InstalledState());
        Equal(archiveBytes.LongLength, preparation.ReusableArchiveBytes, "same-size archive remains only as an untrusted verification candidate");
        Equal(
            false,
            await UpdateEngine.TryReuseVerifiedArchiveAsync(archivePath, manifest.Payload, NoCancellation),
            "wrong-hash assembled archive is rejected");
        Equal(false, File.Exists(archivePath), "wrong-hash assembled archive is deleted before part download");
    }

    private static async Task TestAdversarialAssetDownloadsAsync(string root)
    {
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "asset.part");
        var overflowHandler = new StubHttpHandler(request =>
        {
            Equal("identity", request.Headers.AcceptEncoding.Single().Value, "raw asset Accept-Encoding");
            var content = new StreamContent(new MemoryStream("abcdef"u8.ToArray()));
            content.Headers.ContentLength = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using (var client = new ReleaseClient(overflowHandler, TimeSpan.FromSeconds(5)))
        {
            await ThrowsAsync<InvalidDataException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 4, null, NoCancellation));
        }
        Equal(false, File.Exists(destination), "oversized chunked response partial must be deleted");

        await File.WriteAllTextAsync(destination, "abc");
        var badRangeHandler = new StubHttpHandler(request =>
        {
            Equal(3L, request.Headers.Range!.Ranges.Single().From!.Value, "resume request offset");
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("def"u8.ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 5, 6);
            return response;
        });
        using (var client = new ReleaseClient(badRangeHandler, TimeSpan.FromSeconds(5)))
        {
            await ThrowsAsync<InvalidDataException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation));
        }
        Equal(false, File.Exists(destination), "invalid Content-Range partial must be deleted");

        await File.WriteAllTextAsync(destination, "abc");
        var validRangeHandler = new StubHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("def"u8.ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        });
        using (var client = new ReleaseClient(validRangeHandler, TimeSpan.FromSeconds(5)))
        {
            await client.DownloadFileAsync(new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation);
        }
        Equal("abcdef", await File.ReadAllTextAsync(destination), "valid ranged resume");

        await File.WriteAllTextAsync(destination, "abc");
        var restartHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("ABCDEF"u8.ToArray())
        });
        using (var client = new ReleaseClient(restartHandler, TimeSpan.FromSeconds(5)))
        {
            await client.DownloadFileAsync(new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation);
        }
        Equal("ABCDEF", await File.ReadAllTextAsync(destination), "200 response restarts an ignored resume range");

        File.Delete(destination);
        var unsolicitedRangeHandler = new StubHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("abcdef"u8.ToArray())
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 5, 6);
            return response;
        });
        using (var client = new ReleaseClient(unsolicitedRangeHandler, TimeSpan.FromSeconds(5)))
        {
            await ThrowsAsync<InvalidDataException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation));
        }
        Equal(false, File.Exists(destination), "unsolicited partial response rejected");

        var wrongLengthHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("short"u8.ToArray())
        });
        using (var client = new ReleaseClient(wrongLengthHandler, TimeSpan.FromSeconds(5)))
        {
            await ThrowsAsync<InvalidDataException>(() => client.DownloadFileAsync(
                new Uri("https://example.invalid/asset"), destination, 6, null, NoCancellation));
        }
        Equal(false, File.Exists(destination), "wrong full Content-Length rejected");

        UpdateManifest signedManifest = DeltaManifest();
        PayloadPart signedPart = signedManifest.Payload!.Parts[0];
        var wrongAssets = new Dictionary<string, GitHubAsset>(StringComparer.OrdinalIgnoreCase)
        {
            [signedPart.Name] = new GitHubAsset
            {
                Name = signedPart.Name,
                Size = signedPart.Size + 1,
                BrowserDownloadUrl = "https://example.invalid/part"
            }
        };
        Throws<InvalidDataException>(() => ReleaseClient.BindSignedPartAssets(signedManifest, wrongAssets));
        wrongAssets[signedPart.Name].Size = signedPart.Size;
        Equal(1, ReleaseClient.BindSignedPartAssets(signedManifest, wrongAssets).Count, "exact signed part asset binding");
        var metadataAsset = new GitHubAsset { Name = "manifest", Size = 0 };
        Throws<InvalidDataException>(() => ReleaseClient.ValidateBoundedAssetMetadata(metadataAsset, 8, "manifest"));
        metadataAsset.Size = 9;
        Throws<InvalidDataException>(() => ReleaseClient.ValidateBoundedAssetMetadata(metadataAsset, 8, "signature"));
        metadataAsset.Size = 8;
        ReleaseClient.ValidateBoundedAssetMetadata(metadataAsset, 8, "manifest");
    }

    private static async Task TestPaginatedReleaseAssetsAsync()
    {
        UpdaterConfiguration configuration = Configuration();
        configuration.Repository = "owner/repository";
        const long releaseId = 4242;
        List<GitHubAsset> firstPage = Enumerable.Range(0, 100)
            .Select(index => Asset($"filler-{index:D3}.bin", 1))
            .ToList();
        List<GitHubAsset> secondPage =
        [
            Asset(configuration.ManifestAsset, 128),
            Asset(configuration.SignatureAsset, 64),
            Asset("late.part001", 7)
        ];
        int assetPageRequests = 0;
        var pagedHandler = new StubHttpHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases", StringComparison.Ordinal))
            {
                return JsonResponse(new[]
                {
                    new GitHubRelease
                    {
                        Id = releaseId,
                        TagName = "modpack-v1.0.5",
                        // Deliberately empty: the nested assets list is not a
                        // trusted complete inventory.
                        Assets = []
                    }
                });
            }
            if (path.EndsWith($"/releases/{releaseId}/assets", StringComparison.Ordinal))
            {
                assetPageRequests++;
                string query = request.RequestUri.Query;
                return query.EndsWith("page=1", StringComparison.Ordinal)
                    ? JsonResponse(firstPage)
                    : JsonResponse(secondPage);
            }
            throw new InvalidOperationException($"Unexpected test URL: {request.RequestUri}");
        });
        using (var client = new ReleaseClient(pagedHandler, TimeSpan.FromSeconds(5)))
        {
            List<GitHubRelease> releases = await client.GetPublishedModpackReleasesAsync(
                configuration,
                NoCancellation);
            Equal(1, releases.Count, "release retained after explicit asset paging");
            Equal(103, releases[0].Assets.Count, "all release asset pages collected");
            Equal(2, assetPageRequests, "asset endpoint pagination count");
            UpdateManifest signed = DeltaManifest();
            signed.Payload!.Size = 7;
            signed.Payload.Parts =
            [
                new PayloadPart { Name = "late.part001", Size = 7, Sha256 = HashText("late") }
            ];
            IReadOnlyDictionary<string, Uri> bound = ReleaseClient.BindSignedPartAssets(
                signed,
                releases[0].Assets.ToDictionary(asset => asset.Name, StringComparer.OrdinalIgnoreCase));
            Equal(true, bound.ContainsKey("late.part001"), "signed part found only on later asset page");
        }

        int cappedRequests = 0;
        var cappedHandler = new StubHttpHandler(_ =>
        {
            cappedRequests++;
            return JsonResponse(firstPage);
        });
        using (var client = new ReleaseClient(cappedHandler, TimeSpan.FromSeconds(5)))
        {
            await ThrowsAsync<InvalidDataException>(() => client.GetAllReleaseAssetsAsync(
                configuration,
                releaseId,
                NoCancellation));
        }
        Equal(10, cappedRequests, "asset pagination hard cap");
    }

    private static GitHubAsset Asset(string name, long size) => new()
    {
        Name = name,
        Size = size,
        BrowserDownloadUrl = $"https://example.invalid/{name}"
    };

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value))
    };

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

    private static async Task TestDeltaApplyTimeValidationAsync(string root)
    {
        await TestChangedTargetMutationBeforeApplyAsync(Path.Combine(root, "changed"));
        await TestUnchangedTargetMutationBeforeCommitAsync(Path.Combine(root, "unchanged"));
    }

    private static async Task TestChangedTargetMutationBeforeApplyAsync(string root)
    {
        (UpdaterPaths paths, UpdateManifest signedBase, UpdateManifest delta, InstalledState state, string extract) =
            await PrepareDeltaApplyFixtureAsync(root);
        string changed = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/changed.jar");
        await File.WriteAllTextAsync(changed, "OLD-CHANGED"); // exact length, wrong base hash
        var engine = new UpdateEngine(paths, Configuration(), _ => { });
        await ThrowsAsync<InvalidDataException>(() => engine.ApplyTransactionAsync(
            delta,
            HashText("delta-manifest"),
            extract,
            state,
            signedBase,
            NoCancellation));
        Equal("OLD-CHANGED", await File.ReadAllTextAsync(changed), "concurrent changed-target edit preserved");
        Equal(state.Version, LocalStateStore.LoadState(paths).Version, "pre-mutation mismatch keeps old state");
        Equal(false, File.Exists(TransactionStore.JournalPath(paths)), "pre-mutation mismatch rolls journal back");
    }

    private static async Task TestUnchangedTargetMutationBeforeCommitAsync(string root)
    {
        (UpdaterPaths paths, UpdateManifest signedBase, UpdateManifest delta, InstalledState state, string extract) =
            await PrepareDeltaApplyFixtureAsync(root);
        string unchanged = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/unchanged.jar");
        await File.WriteAllTextAsync(unchanged, "UNCHANGED"); // exact length, concurrent edit
        var engine = new UpdateEngine(paths, Configuration(), _ => { });
        await ThrowsAsync<InvalidDataException>(() => engine.ApplyTransactionAsync(
            delta,
            HashText("delta-manifest"),
            extract,
            state,
            signedBase,
            NoCancellation));
        Equal("UNCHANGED", await File.ReadAllTextAsync(unchanged), "concurrent unchanged edit preserved");
        Equal(
            "old-changed",
            await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/changed.jar")),
            "changed payload rolled back after final base validation failure");
        Equal(
            "delete-me",
            await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/deleted.jar")),
            "deleted base file rolled back after final validation failure");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/added.jar")), "new payload rolled back");
        Equal(state.Version, LocalStateStore.LoadState(paths).Version, "final mismatch keeps old state");
    }

    private static async Task<(UpdaterPaths Paths, UpdateManifest SignedBase, UpdateManifest Delta, InstalledState State, string Extract)>
        PrepareDeltaApplyFixtureAsync(string root)
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
        await LocalStateStore.SaveStateAsync(paths, state, NoCancellation);
        UpdateManifest delta = DeltaFromBase(signedBase, baseHash);
        string extract = Path.Combine(root, "extract");
        foreach ((string relative, string content) in new Dictionary<string, string>
        {
            ["mods/changed.jar"] = "new-changed",
            ["mods/added.jar"] = "new-file"
        })
        {
            string source = PathSafety.CombineUnder(extract, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, content);
        }
        return (paths, signedBase, delta, state, extract);
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
            SeedFiles = [FileEntry("options.txt", "starter-options")],
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
        await File.WriteAllTextAsync(
            PathSafety.CombineUnder(paths.MinecraftDirectory, "options.txt"),
            "starter-options");

        string manifestHash = HashText("baseline-manifest");
        var engine = new UpdateEngine(paths, Configuration(), _ => { });
        bool adopted = await engine.TryAdoptExistingBaselineAsync(
            baseline, manifestHash, new InstalledState(), NoCancellation);
        Equal(true, adopted, "exact baseline adoption");
        InstalledState state = LocalStateStore.LoadState(paths);
        Equal("1.0.4", state.Version, "adopted baseline version");
        Equal(2, state.ManagedFiles.Count, "adopted full inventory");
        Equal("options.txt", state.OfferedSeedPaths.Single(), "adoption records player-owned defaults as offered");

        File.Delete(paths.StatePath);
        var exactDelta = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.5",
            Files = [first, second],
            SeedFiles = [FileEntry("options.txt", "starter-options")],
            DeletedFiles = [FileEntry("mods/removed.jar", "removed")]
        };
        bool adoptedDelta = await engine.TryAdoptExistingBaselineAsync(
            exactDelta, HashText("delta-manifest"), new InstalledState(), NoCancellation);
        Equal(true, adoptedDelta, "exact delta release adoption supports a fresh current MRPACK without downloading its base");
        Equal("1.0.5", LocalStateStore.LoadState(paths).Version, "adopted exact delta version");

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

    private static async Task TestSchemaOneSanitizedRecoveryAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);

        const string managedCachePath = "mods/mcef-cache/Network/Cookies";
        const string unmanagedCachePath = "mods/mcef-cache/Network/UnmanagedCookies";
        const string exactLegacyPath = "mods/timm-legacy.jar";
        const string changedLegacyPath = "mods/infinite-music-legacy.jar";
        const string currentPath = "mods/current.jar";
        await WriteRelativeTextAsync(paths.MinecraftDirectory, managedCachePath, "changed-private-cache");
        await WriteRelativeTextAsync(paths.MinecraftDirectory, unmanagedCachePath, "unmanaged-private-cache");
        await WriteRelativeTextAsync(paths.MinecraftDirectory, exactLegacyPath, "legacy-timm");
        await WriteRelativeTextAsync(paths.MinecraftDirectory, changedLegacyPath, "custom-infinite");

        var previousState = new InstalledState
        {
            Version = "1.0.4",
            ManifestSha256 = HashText("unsafe-baseline"),
            ManagedFiles =
            [
                new ManagedFileState
                {
                    Path = managedCachePath,
                    Size = "original-private-cache".Length,
                    Sha256 = HashText("original-private-cache")
                }
            ]
        };
        await LocalStateStore.SaveStateAsync(paths, previousState, NoCancellation);

        ManifestFile current = FileEntry(currentPath, "current-content");
        var sanitizedBaseline = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.5",
            Files = [current],
            LegacyCleanup =
            [
                new LegacyCleanupFile
                {
                    Path = exactLegacyPath,
                    Size = "legacy-timm".Length,
                    Sha256 = HashText("legacy-timm")
                },
                new LegacyCleanupFile
                {
                    Path = changedLegacyPath,
                    Size = "legacy-infinite".Length,
                    Sha256 = HashText("legacy-infinite")
                }
            ]
        };
        string extract = Path.Combine(root, "extract");
        await WriteRelativeTextAsync(extract, currentPath, "current-content");
        var logs = new List<string>();
        var engine = new UpdateEngine(paths, Configuration(), logs.Add);
        await engine.ApplyTransactionAsync(
            sanitizedBaseline,
            HashText("sanitized-baseline"),
            extract,
            previousState,
            signedBase: null,
            NoCancellation);

        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, managedCachePath)), "drifted prior managed cache removed by full baseline");
        Equal(true, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, unmanagedCachePath)), "similarly named unmanaged cache preserved");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, exactLegacyPath)), "exact legacy music jar removed");
        Equal("custom-infinite", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, changedLegacyPath)), "changed legacy jar preserved");
        Equal(true, logs.Any(line => line.Contains("Keeping changed legacy file", StringComparison.Ordinal)), "changed legacy preservation logged");
        Equal("1.0.5", LocalStateStore.LoadState(paths).Version, "sanitized baseline state committed");
        Equal(false, File.Exists(TransactionStore.JournalPath(paths)), "sanitized baseline left no transaction journal");
    }

    private static async Task TestCreateOnlyDefaultsAsync(string root)
    {
        UpdaterPaths paths = Paths(Path.Combine(root, "apply"));
        Directory.CreateDirectory(paths.MinecraftDirectory);
        const string mutableConfigPath = "config/musicnotification.json";
        const string optionsPath = "options.txt";
        const string axiomPath = "mods/Axiom-5.4.2-for-MC1.21.1.jar";
        const string oldAxiomPath = "mods/Axiom-5.3.0-for-MC1.21.1.jar";
        const string managedPath = "mods/current.jar";
        const string playerConfig = "player-custom-notification-settings";
        await WriteRelativeTextAsync(paths.MinecraftDirectory, mutableConfigPath, playerConfig);
        await WriteRelativeTextAsync(paths.MinecraftDirectory, oldAxiomPath, "player-kept-or-replaced-axiom");

        var previousState = new InstalledState
        {
            Version = "1.0.5",
            ManifestSha256 = HashText("old-baseline"),
            ManagedFiles =
            [
                new ManagedFileState
                {
                    Path = mutableConfigPath,
                    Size = "old-managed-default".Length,
                    Sha256 = HashText("old-managed-default")
                },
                new ManagedFileState
                {
                    Path = oldAxiomPath,
                    Size = "old-managed-axiom".Length,
                    Sha256 = HashText("old-managed-axiom")
                }
            ]
        };
        await LocalStateStore.SaveStateAsync(paths, previousState, NoCancellation);

        ManifestFile managed = FileEntry(managedPath, "managed-content");
        ManifestFile configSeed = FileEntry(mutableConfigPath, "new-friend-default");
        ManifestFile optionsSeed = FileEntry(optionsPath, "keybind-and-video-defaults");
        ManifestFile axiomSeed = FileEntry(axiomPath, "optional-builder-mod");
        var baseline = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.6",
            Files = [managed],
            SeedFiles = [configSeed, optionsSeed, axiomSeed]
        };
        string extract = Path.Combine(root, "extract");
        await WriteRelativeTextAsync(extract, managedPath, "managed-content");
        await WriteRelativeTextAsync(extract, mutableConfigPath, "new-friend-default");
        await WriteRelativeTextAsync(extract, optionsPath, "keybind-and-video-defaults");
        await WriteRelativeTextAsync(extract, axiomPath, "optional-builder-mod");

        var logs = new List<string>();
        var engine = new UpdateEngine(paths, Configuration(), logs.Add);
        await engine.ApplyTransactionAsync(
            baseline,
            HashText("new-baseline"),
            extract,
            previousState,
            signedBase: null,
            NoCancellation);

        Equal(playerConfig, await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, mutableConfigPath)), "existing player config preserved during ownership transfer");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, optionsPath)), "an existing install's missing options remain player-owned absence");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, axiomPath)), "an existing install's removed optional Axiom remains absent");
        Equal("player-kept-or-replaced-axiom", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, oldAxiomPath)), "an older or locally replaced Axiom is preserved while leaving updater ownership");
        InstalledState installed = LocalStateStore.LoadState(paths);
        Equal(1, installed.ManagedFiles.Count, "create-only defaults absent from managed state");
        Equal(managedPath, installed.ManagedFiles.Single().Path, "only immutable file remains managed");
        Equal(3, installed.OfferedSeedPaths.Count, "schema-one migration records every old default as already offered");
        Equal(true, logs.Any(line => line.Contains("Keeping player-owned setting", StringComparison.Ordinal)), "player-owned preservation logged");
        Equal(true, logs.Any(line => line.Contains("Keeping player-owned absence", StringComparison.Ordinal)), "player-owned absence logged");

        await WriteRelativeTextAsync(paths.MinecraftDirectory, optionsPath, "player-changed-keybinds-and-video");
        await File.WriteAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, mutableConfigPath), "player-moved-toast");
        await WriteRelativeTextAsync(extract, managedPath, "managed-content");
        await WriteRelativeTextAsync(extract, mutableConfigPath, "new-friend-default");
        await WriteRelativeTextAsync(extract, optionsPath, "keybind-and-video-defaults");
        await WriteRelativeTextAsync(extract, axiomPath, "optional-builder-mod");
        await engine.ApplyTransactionAsync(
            baseline,
            HashText("same-defaults-reapplied-for-test"),
            extract,
            installed,
            signedBase: null,
            NoCancellation);
        Equal("player-changed-keybinds-and-video", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, optionsPath)), "later options edits survive another release application");
        Equal("player-moved-toast", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, mutableConfigPath)), "later mod config edits survive another release application");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, axiomPath)), "removed optional Axiom stays absent on another release application");

        const string fancyMenuSeedPath = "config/fancymenu/customization/title_screen_layout.txt";
        ManifestFile fancyMenuSeed = FileEntry(fancyMenuSeedPath, "friend-pack-title-layout");
        var correctiveDelta = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.7",
            Files = [managed],
            SeedFiles = [configSeed, optionsSeed, axiomSeed, fancyMenuSeed]
        };
        await WriteRelativeTextAsync(extract, fancyMenuSeedPath, "friend-pack-title-layout");
        InstalledState preCorrective = LocalStateStore.LoadState(paths);
        await engine.ApplyTransactionAsync(
            correctiveDelta,
            HashText("corrective-delta"),
            extract,
            preCorrective,
            signedBase: baseline,
            NoCancellation);
        Equal("friend-pack-title-layout", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, fancyMenuSeedPath)), "new 1.0.7 config default reaches an existing 1.0.6 player once");
        Equal("player-moved-toast", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, mutableConfigPath)), "an old seed inferred from the signed base remains player-owned");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, axiomPath)), "signed-base seed inference does not reinstall removed Axiom");
        InstalledState corrected = LocalStateStore.LoadState(paths);
        Equal(4, corrected.OfferedSeedPaths.Count, "corrective update persists its expanded one-time seed ledger");

        File.Delete(PathSafety.CombineUnder(paths.MinecraftDirectory, fancyMenuSeedPath));
        var laterDelta = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.8",
            Files = [managed],
            SeedFiles = [configSeed, optionsSeed, axiomSeed, fancyMenuSeed]
        };
        await engine.ApplyTransactionAsync(
            laterDelta,
            HashText("later-delta"),
            extract,
            corrected,
            signedBase: correctiveDelta,
            NoCancellation);
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, fancyMenuSeedPath)), "deleting a once-offered config remains respected by later releases");

        var repairDelta = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.9",
            Files = [managed],
            SeedFiles = [configSeed, optionsSeed, axiomSeed, fancyMenuSeed],
            ReofferSeedPaths = [fancyMenuSeedPath]
        };
        await WriteRelativeTextAsync(extract, fancyMenuSeedPath, "friend-pack-title-layout");
        InstalledState preRepair = LocalStateStore.LoadState(paths);
        await engine.ApplyTransactionAsync(
            repairDelta,
            HashText("repair-delta"),
            extract,
            preRepair,
            signedBase: laterDelta,
            NoCancellation);
        Equal("friend-pack-title-layout", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, fancyMenuSeedPath)), "signed corrective re-offer restores a missing seed once");
        Equal("player-moved-toast", await File.ReadAllTextAsync(PathSafety.CombineUnder(paths.MinecraftDirectory, mutableConfigPath)), "corrective re-offer does not overwrite existing player config");
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, axiomPath)), "corrective re-offer does not reinstall unrelated optional seeds");

        File.Delete(PathSafety.CombineUnder(paths.MinecraftDirectory, fancyMenuSeedPath));
        var postRepairDelta = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.10",
            Files = [managed],
            SeedFiles = [configSeed, optionsSeed, axiomSeed, fancyMenuSeed]
        };
        await engine.ApplyTransactionAsync(
            postRepairDelta,
            HashText("post-repair-delta"),
            extract,
            LocalStateStore.LoadState(paths),
            signedBase: repairDelta,
            NoCancellation);
        Equal(false, File.Exists(PathSafety.CombineUnder(paths.MinecraftDirectory, fancyMenuSeedPath)), "a later release without a re-offer respects player-owned absence again");

        UpdaterPaths freshPaths = Paths(Path.Combine(root, "fresh"));
        Directory.CreateDirectory(freshPaths.MinecraftDirectory);
        string freshExtract = Path.Combine(root, "fresh-extract");
        await WriteRelativeTextAsync(freshExtract, managedPath, "managed-content");
        await WriteRelativeTextAsync(freshExtract, mutableConfigPath, "new-friend-default");
        await WriteRelativeTextAsync(freshExtract, optionsPath, "keybind-and-video-defaults");
        await WriteRelativeTextAsync(freshExtract, axiomPath, "optional-builder-mod");
        var freshEngine = new UpdateEngine(freshPaths, Configuration(), _ => { });
        await freshEngine.ApplyTransactionAsync(
            baseline,
            HashText("fresh-baseline"),
            freshExtract,
            new InstalledState(),
            signedBase: null,
            NoCancellation);
        Equal("keybind-and-video-defaults", await File.ReadAllTextAsync(PathSafety.CombineUnder(freshPaths.MinecraftDirectory, optionsPath)), "fresh install receives keybind and video defaults");
        Equal("new-friend-default", await File.ReadAllTextAsync(PathSafety.CombineUnder(freshPaths.MinecraftDirectory, mutableConfigPath)), "fresh install receives mutable config defaults");
        Equal("optional-builder-mod", await File.ReadAllTextAsync(PathSafety.CombineUnder(freshPaths.MinecraftDirectory, axiomPath)), "fresh install receives optional Axiom when supplied");
        InstalledState freshState = LocalStateStore.LoadState(freshPaths);
        Equal(1, freshState.ManagedFiles.Count, "fresh create-only defaults are not recorded as managed");
        Equal(3, freshState.OfferedSeedPaths.Count, "fresh defaults are recorded only in the offer ledger");

        File.Delete(PathSafety.CombineUnder(freshPaths.MinecraftDirectory, axiomPath));
        await WriteRelativeTextAsync(freshExtract, managedPath, "managed-content");
        await WriteRelativeTextAsync(freshExtract, mutableConfigPath, "new-friend-default");
        await WriteRelativeTextAsync(freshExtract, optionsPath, "keybind-and-video-defaults");
        await WriteRelativeTextAsync(freshExtract, axiomPath, "optional-builder-mod");
        await freshEngine.ApplyTransactionAsync(
            baseline,
            HashText("fresh-baseline-reapplied"),
            freshExtract,
            freshState,
            signedBase: null,
            NoCancellation);
        Equal(false, File.Exists(PathSafety.CombineUnder(freshPaths.MinecraftDirectory, axiomPath)), "optional Axiom is never re-seeded after removal");

        UpdaterPaths integrityPaths = Paths(Path.Combine(root, "optional-integrity"));
        await WriteRelativeTextAsync(integrityPaths.MinecraftDirectory, managedPath, "managed-content");
        var oldBaseline = new UpdateManifest
        {
            SchemaVersion = 1,
            Version = "1.0.5",
            Files = [managed, FileEntry(oldAxiomPath, "old-managed-axiom")]
        };
        InstalledState oldState = StateFrom(oldBaseline, HashText("old-axiom-baseline"));
        var integrityEngine = new UpdateEngine(integrityPaths, Configuration(), _ => { });
        Equal(true, integrityEngine.StateMatchesManifestAndSizes(oldState, oldBaseline), "missing formerly managed Axiom does not trigger baseline repair");
        await WriteRelativeTextAsync(integrityPaths.MinecraftDirectory, oldAxiomPath, "a-different-local-axiom-build");
        Equal(true, integrityEngine.StateMatchesManifestAndSizes(oldState, oldBaseline), "replaced formerly managed Axiom does not trigger baseline repair");
        await File.WriteAllTextAsync(PathSafety.CombineUnder(integrityPaths.MinecraftDirectory, managedPath), "changed-required-content");
        Equal(false, integrityEngine.StateMatchesManifestAndSizes(oldState, oldBaseline), "a changed required mod still triggers baseline repair");

        UpdaterPaths adoptionPaths = Paths(Path.Combine(root, "adoption"));
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, managedPath, "managed-content");
        var adoptionEngine = new UpdateEngine(adoptionPaths, Configuration(), _ => { });
        Equal(false, await adoptionEngine.TryAdoptExistingBaselineAsync(
            baseline, HashText("adoption"), new InstalledState(), NoCancellation), "baseline adoption cannot mark missing first-run defaults as already offered");

        UpdaterPaths repairAdoptionPaths = Paths(Path.Combine(root, "adoption-reoffer"));
        await WriteRelativeTextAsync(repairAdoptionPaths.MinecraftDirectory, managedPath, "managed-content");
        var repairAdoptionEngine = new UpdateEngine(repairAdoptionPaths, Configuration(), _ => { });
        var repairAdoptionManifest = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.9",
            Files = [managed],
            SeedFiles = [optionsSeed],
            ReofferSeedPaths = [optionsPath]
        };
        Equal(false, await repairAdoptionEngine.TryAdoptExistingBaselineAsync(
            repairAdoptionManifest, HashText("repair-adoption"), new InstalledState(), NoCancellation), "baseline adoption cannot skip a missing corrective seed offer");
        await WriteRelativeTextAsync(repairAdoptionPaths.MinecraftDirectory, optionsPath, "player-existing-options");
        Equal(true, await repairAdoptionEngine.TryAdoptExistingBaselineAsync(
            repairAdoptionManifest, HashText("repair-adoption"), new InstalledState(), NoCancellation), "baseline adoption accepts an existing corrective seed without comparing player content");
        adoptionPaths = Paths(Path.Combine(root, "adoption-with-defaults"));
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, managedPath, "managed-content");
        adoptionEngine = new UpdateEngine(adoptionPaths, Configuration(), _ => { });
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, optionsPath, "custom-existing-options");
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, mutableConfigPath, "custom-existing-config");
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, axiomPath, "custom-existing-axiom");
        Equal(true, await adoptionEngine.TryAdoptExistingBaselineAsync(
            baseline, HashText("adoption"), new InstalledState(), NoCancellation), "baseline adoption accepts existing player-owned defaults without comparing content");
        Equal(1, LocalStateStore.LoadState(adoptionPaths).ManagedFiles.Count, "adoption excludes create-only defaults from state");
        Equal(3, LocalStateStore.LoadState(adoptionPaths).OfferedSeedPaths.Count, "adoption marks defaults offered without managing them");

        UpdaterPaths rollbackPaths = Paths(Path.Combine(root, "rollback"));
        Directory.CreateDirectory(rollbackPaths.MinecraftDirectory);
        string rollbackTarget = PathSafety.CombineUnder(rollbackPaths.MinecraftDirectory, optionsPath);
        await File.WriteAllTextAsync(rollbackTarget, "keybind-and-video-defaults");
        var rollbackPrevious = new InstalledState();
        var rollbackNext = new InstalledState
        {
            Version = "1.0.6",
            ManifestSha256 = HashText("rollback-next")
        };
        await LocalStateStore.SaveStateAsync(rollbackPaths, rollbackPrevious, NoCancellation);
        await TransactionStore.SaveAsync(rollbackPaths, new TransactionJournal
        {
            PreviousState = rollbackPrevious,
            NextState = rollbackNext,
            SeedFiles =
            [
                new ManagedFileState
                {
                    Path = optionsPath,
                    Size = "keybind-and-video-defaults".Length,
                    Sha256 = HashText("keybind-and-video-defaults")
                }
            ],
            Operations =
            [
                new TransactionOperation
                {
                    Kind = "create",
                    TargetPath = rollbackTarget,
                    TargetTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(rollbackTarget)
                }
            ]
        }, NoCancellation);
        await TransactionStore.RecoverIfNeededAsync(rollbackPaths, BuildInfo.SupportedRoots, _ => { });
        Equal(false, File.Exists(rollbackTarget), "interrupted first-run default creation rolls back safely");
    }

    private static async Task TestCorrectiveSeedRefreshAndAdoptionAsync(string root)
    {
        var testConfiguration = new UpdaterConfiguration
        {
            AllowedRoots = ["mods", "shaderpacks", "config"]
        };
        const string managedPath = "mods/current.jar";
        const string defaultProfilePath = "config/packed_packs/profiles/resourcepacks/Default.profile.json";
        const string realisticProfilePath = "config/packed_packs/profiles/resourcepacks/Realistic.profile.json";
        const string exactShaderOptionsPath = "shaderpacks/Max Quality.zip.txt";
        const string customShaderOptionsPath = "shaderpacks/Balanced.zip.txt";
        const string irisPath = "config/iris.properties";
        const string oldSelector = "shaderPack=Max Quality (Iteration RP - Path Traced)";
        const string newSelector = "shaderPack=Max Quality (Iteration RP - Path Traced).zip";

        ManifestFile managed = FileEntry(managedPath, "managed");
        ManifestFile exactShaderBase = FileEntry(exactShaderOptionsPath, "official-old-options");
        ManifestFile customShaderBase = FileEntry(customShaderOptionsPath, "official-balanced-options");
        ManifestFile oldDefault = FileEntry(defaultProfilePath, "official-old-default");
        ManifestFile oldestDefault = FileEntry(defaultProfilePath, "official-even-older-default");
        ManifestFile oldRealistic = FileEntry(realisticProfilePath, "official-old-realistic");
        ManifestFile irisSeed = FileEntry(irisPath, "enableShaders=false\n" + newSelector + "\n");
        var signedBase = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.12",
            Files = [managed, exactShaderBase, customShaderBase],
            SeedFiles = [oldDefault, oldRealistic, irisSeed]
        };
        string baseHash = HashText("seed-refresh-base");
        InstalledState previousState = StateFrom(signedBase, baseHash);
        previousState.OfferedSeedPaths = [defaultProfilePath, realisticProfilePath, irisPath];

        UpdaterPaths paths = Paths(Path.Combine(root, "upgrade"));
        await WriteRelativeTextAsync(paths.MinecraftDirectory, managedPath, "managed");
        await WriteRelativeTextAsync(paths.MinecraftDirectory, exactShaderOptionsPath, "official-old-options");
        await WriteRelativeTextAsync(paths.MinecraftDirectory, customShaderOptionsPath, "player-custom-balanced-options");
        await WriteRelativeTextAsync(paths.MinecraftDirectory, defaultProfilePath, "official-old-default");
        await WriteRelativeTextAsync(paths.MinecraftDirectory, realisticProfilePath, "player-custom-realistic");
        await WriteRelativeTextAsync(
            paths.MinecraftDirectory,
            irisPath,
            "#Iris runtime timestamp\nenableShaders=true\nmaxShadowRenderDistance=5\n" + oldSelector + "\n");
        await LocalStateStore.SaveStateAsync(paths, previousState, NoCancellation);

        ManifestFile newDefault = FileEntry(defaultProfilePath, "official-new-default");
        ManifestFile newRealistic = FileEntry(realisticProfilePath, "official-new-realistic");
        ManifestFile newExactShader = FileEntry(exactShaderOptionsPath, "official-new-options");
        ManifestFile newCustomShader = FileEntry(customShaderOptionsPath, "official-new-balanced-options");
        var delta = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.13",
            Base = new ManifestBase { Version = signedBase.Version, ManifestSha256 = baseHash },
            Files = [managed],
            SeedFiles = [newDefault, newRealistic, newExactShader, newCustomShader, irisSeed],
            ReofferSeedPaths = [defaultProfilePath, realisticProfilePath, exactShaderOptionsPath, customShaderOptionsPath, irisPath],
            SeedTextReplacements =
            [
                new SeedTextReplacement { Path = irisPath, OldText = oldSelector, NewText = newSelector }
            ],
            LegacyCleanup =
            [
                new LegacyCleanupFile { Path = defaultProfilePath, Size = oldDefault.Size, Sha256 = oldDefault.Sha256 },
                new LegacyCleanupFile { Path = defaultProfilePath, Size = oldestDefault.Size, Sha256 = oldestDefault.Sha256 },
                new LegacyCleanupFile { Path = realisticProfilePath, Size = oldRealistic.Size, Sha256 = oldRealistic.Sha256 },
                new LegacyCleanupFile { Path = exactShaderOptionsPath, Size = exactShaderBase.Size, Sha256 = exactShaderBase.Sha256 },
                new LegacyCleanupFile { Path = customShaderOptionsPath, Size = customShaderBase.Size, Sha256 = customShaderBase.Sha256 }
            ]
        };
        string extract = Path.Combine(root, "upgrade-extract");
        foreach ((string path, string content) in new Dictionary<string, string>
        {
            [defaultProfilePath] = "official-new-default",
            [realisticProfilePath] = "official-new-realistic",
            [exactShaderOptionsPath] = "official-new-options",
            [customShaderOptionsPath] = "official-new-balanced-options",
            [irisPath] = "enableShaders=false\n" + newSelector + "\n"
        })
        {
            await WriteRelativeTextAsync(extract, path, content);
        }

        var engine = new UpdateEngine(paths, testConfiguration, _ => { });
        await DeltaValidator.ValidateBaseAsync(
            delta,
            signedBase,
            baseHash,
            previousState,
            paths,
            testConfiguration,
            NoCancellation);
        await engine.ApplyTransactionAsync(
            delta,
            HashText("seed-refresh-delta"),
            extract,
            previousState,
            signedBase,
            NoCancellation);

        Equal("official-new-default", await ReadRelativeTextAsync(paths.MinecraftDirectory, defaultProfilePath), "known old Packed Packs profile refreshed");
        Equal("player-custom-realistic", await ReadRelativeTextAsync(paths.MinecraftDirectory, realisticProfilePath), "custom Packed Packs profile preserved");
        Equal("official-new-options", await ReadRelativeTextAsync(paths.MinecraftDirectory, exactShaderOptionsPath), "exact managed shader settings became refreshed player-owned seed");
        Equal("player-custom-balanced-options", await ReadRelativeTextAsync(paths.MinecraftDirectory, customShaderOptionsPath), "modified managed shader settings survived ownership transition");
        string migratedIris = await ReadRelativeTextAsync(paths.MinecraftDirectory, irisPath);
        Equal(true, migratedIris.Contains("enableShaders=true", StringComparison.Ordinal), "Iris shader enablement preserved");
        Equal(true, migratedIris.Contains("maxShadowRenderDistance=5", StringComparison.Ordinal), "unrelated Iris setting preserved");
        Equal(true, migratedIris.Contains(newSelector, StringComparison.Ordinal), "legacy extensionless shader selector migrated to ZIP identity");
        Equal(false, migratedIris.Contains(oldSelector + "\n", StringComparison.Ordinal), "obsolete Iris selector removed");
        InstalledState upgradedState = LocalStateStore.LoadState(paths);
        Equal(1, upgradedState.ManagedFiles.Count, "shader settings no longer participate in managed integrity");

        UpdaterPaths adoptionPaths = Paths(Path.Combine(root, "adoption"));
        const string retiredModPath = "mods/toughasnails-legacy.jar";
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, managedPath, "managed");
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, retiredModPath, "retired-mod");
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, defaultProfilePath, "official-even-older-default");
        await WriteRelativeTextAsync(adoptionPaths.MinecraftDirectory, irisPath, "enableShaders=false\n" + oldSelector + "\n");
        var adoptionManifest = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.13",
            Files = [managed],
            SeedFiles = [newDefault, irisSeed],
            ReofferSeedPaths = [defaultProfilePath, irisPath],
            SeedTextReplacements =
            [
                new SeedTextReplacement { Path = irisPath, OldText = oldSelector, NewText = newSelector }
            ],
            LegacyCleanup =
            [
                new LegacyCleanupFile { Path = defaultProfilePath, Size = oldDefault.Size, Sha256 = oldDefault.Sha256 },
                new LegacyCleanupFile { Path = defaultProfilePath, Size = oldestDefault.Size, Sha256 = oldestDefault.Sha256 },
                new LegacyCleanupFile { Path = retiredModPath, Size = "retired-mod".Length, Sha256 = HashText("retired-mod") }
            ]
        };
        string adoptionExtract = Path.Combine(root, "adoption-extract");
        await WriteRelativeTextAsync(adoptionExtract, defaultProfilePath, "official-new-default");
        await WriteRelativeTextAsync(adoptionExtract, irisPath, "enableShaders=false\n" + newSelector + "\n");
        var adoptionEngine = new UpdateEngine(adoptionPaths, testConfiguration, _ => { });
        Equal(false, await adoptionEngine.TryAdoptExistingBaselineAsync(
            adoptionManifest, HashText("adoption-manifest"), new InstalledState(), NoCancellation), "pending corrective work prevents silent exact-state adoption");
        Equal(true, await adoptionEngine.CanRepairAndAdoptExistingTargetAsync(
            adoptionManifest, new InstalledState(), NoCancellation), "exact managed post-state permits small corrective adoption transaction");
        await adoptionEngine.ApplyTransactionAsync(
            adoptionManifest,
            HashText("adoption-manifest"),
            adoptionExtract,
            new InstalledState(),
            signedBase: null,
            NoCancellation,
            adoptExistingPostState: true);
        Equal(false, File.Exists(PathSafety.CombineUnder(adoptionPaths.MinecraftDirectory, retiredModPath)), "known retired unmanaged mod removed during adoption repair");
        Equal("official-new-default", await ReadRelativeTextAsync(adoptionPaths.MinecraftDirectory, defaultProfilePath), "adoption repair refreshed old profile without full baseline");
        Equal(true, (await ReadRelativeTextAsync(adoptionPaths.MinecraftDirectory, irisPath)).Contains(newSelector, StringComparison.Ordinal), "adoption repair migrated Iris selector");
        InstalledState adoptedState = LocalStateStore.LoadState(adoptionPaths);
        Equal("1.0.13", adoptedState.Version, "adoption repair committed target identity");
        Equal(false, await adoptionEngine.HasPendingCorrectiveWorkAsync(
            adoptionManifest,
            adoptedState,
            NoCancellation), "current target seed identities do not create a perpetual corrective loop");

        UpdaterPaths currentPaths = Paths(Path.Combine(root, "already-current"));
        await WriteRelativeTextAsync(currentPaths.MinecraftDirectory, managedPath, "managed");
        await WriteRelativeTextAsync(currentPaths.MinecraftDirectory, retiredModPath, "retired-mod");
        await WriteRelativeTextAsync(currentPaths.MinecraftDirectory, defaultProfilePath, "official-old-default");
        await WriteRelativeTextAsync(currentPaths.MinecraftDirectory, irisPath, "enableShaders=true\n" + oldSelector + "\n");
        string adoptionHash = HashText("adoption-manifest");
        InstalledState currentState = StateFrom(adoptionManifest, adoptionHash);
        currentState.OfferedSeedPaths = adoptionManifest.SeedFiles.Select(file => file.Path).ToList();
        await LocalStateStore.SaveStateAsync(currentPaths, currentState, NoCancellation);
        var currentEngine = new UpdateEngine(currentPaths, testConfiguration, _ => { });
        Equal(true, await currentEngine.HasPendingCorrectiveWorkAsync(
            adoptionManifest,
            currentState,
            NoCancellation), "already-current state still detects signed stale cleanup and selector work");
        Equal(true, await currentEngine.CanRepairAndAdoptExistingTargetAsync(
            adoptionManifest,
            currentState,
            NoCancellation), "already-current state can enter corrective adoption transaction");
    }

    private static async Task WriteRelativeTextAsync(string root, string relativePath, string content)
    {
        string path = PathSafety.CombineUnder(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private static Task<string> ReadRelativeTextAsync(string root, string relativePath) =>
        File.ReadAllTextAsync(PathSafety.CombineUnder(root, relativePath));

    private static async Task TestConditionalKeyCollisionMigrationAsync(string root)
    {
        const string managedPath = "mods/current.jar";
        const string optionsPath = "options.txt";
        const string contestTrackerK = "key_key.companion_bonds.open_contest_tracker:key.keyboard.k";
        const string irisK = "key_iris.keybind.toggleShaders:key.keyboard.k";
        const string irisUnbound = "key_iris.keybind.toggleShaders:key.keyboard.unknown";
        const string fancyK = "key_key.fancytoasts.config_menu:key.keyboard.k";
        const string fancyUnbound = "key_key.fancytoasts.config_menu:key.keyboard.unknown";
        const string migrationId = "options-contest-tracker-k-collision-v1";
        const string officialOldOptions = "renderDistance:8\n" + contestTrackerK + "\n" + irisK + "\n" + fancyK + "\n";
        const string officialNewOptions = "renderDistance:8\n" + contestTrackerK + "\n" + irisUnbound + "\n" + fancyUnbound + "\n";

        ManifestFile managed = FileEntry(managedPath, "managed");
        ManifestFile oldOptions = FileEntry(optionsPath, officialOldOptions);
        ManifestFile newOptions = FileEntry(optionsPath, officialNewOptions);
        var signedBase = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.13",
            Files = [managed],
            SeedFiles = [oldOptions]
        };
        string baseHash = HashText("key-collision-base");
        var delta = new UpdateManifest
        {
            SchemaVersion = 2,
            Version = "1.0.14",
            Files = [managed],
            SeedFiles = [newOptions],
            SeedTextReplacements =
            [
                new SeedTextReplacement
                {
                    Path = optionsPath,
                    OldText = irisK,
                    NewText = irisUnbound,
                    MigrationId = migrationId,
                    RequiredLines = [contestTrackerK]
                },
                new SeedTextReplacement
                {
                    Path = optionsPath,
                    OldText = fancyK,
                    NewText = fancyUnbound,
                    MigrationId = migrationId,
                    RequiredLines = [contestTrackerK]
                }
            ]
        };
        string extract = Path.Combine(root, "extract");
        await WriteRelativeTextAsync(extract, optionsPath, officialNewOptions);

        UpdaterPaths affectedPaths = Paths(Path.Combine(root, "affected"));
        await WriteRelativeTextAsync(affectedPaths.MinecraftDirectory, managedPath, "managed");
        string customAffected = "renderDistance:23\r\nsoundCategory_music:0.42\r\n"
            + contestTrackerK + "\r\n" + irisK + "\r\n" + fancyK
            + "\r\nkey_key.jump:key.keyboard.space\r\n";
        await WriteRelativeTextAsync(affectedPaths.MinecraftDirectory, optionsPath, customAffected);
        InstalledState affectedState = StateFrom(signedBase, baseHash);
        affectedState.OfferedSeedPaths = [optionsPath];
        var affectedEngine = new UpdateEngine(affectedPaths, new UpdaterConfiguration { AllowedRoots = ["mods"] }, _ => { });
        Equal(true, await affectedEngine.HasPendingCorrectiveWorkAsync(
            delta, affectedState, NoCancellation), "exact K collision is pending corrective work");
        await affectedEngine.ApplyTransactionAsync(
            delta,
            HashText("key-collision-delta"),
            extract,
            affectedState,
            signedBase,
            NoCancellation);
        string migrated = await ReadRelativeTextAsync(affectedPaths.MinecraftDirectory, optionsPath);
        string expected = customAffected.Replace(irisK, irisUnbound, StringComparison.Ordinal)
            .Replace(fancyK, fancyUnbound, StringComparison.Ordinal);
        Equal(expected, migrated, "only colliding K bindings changed while CRLF and unrelated settings stayed byte-stable");
        Equal(true, migrated.Contains("renderDistance:23\r\n", StringComparison.Ordinal), "custom video setting preserved");
        Equal(true, migrated.Contains("soundCategory_music:0.42\r\n", StringComparison.Ordinal), "custom sound setting preserved");
        Equal(true, migrated.Contains(contestTrackerK, StringComparison.Ordinal), "contest tracker remains bound to K");
        InstalledState migratedState = LocalStateStore.LoadState(affectedPaths);
        Equal(migrationId, migratedState.AppliedPlayerSettingMigrationIds.Single(), "one-time options migration is committed to local state");

        string laterPlayerChoice = migrated.Replace(irisUnbound, irisK, StringComparison.Ordinal);
        await WriteRelativeTextAsync(affectedPaths.MinecraftDirectory, optionsPath, laterPlayerChoice);
        Equal(false, await affectedEngine.HasPendingCorrectiveWorkAsync(
            delta, migratedState, NoCancellation), "later intentional Iris K binding is never undone by the completed migration");

        UpdaterPaths customPaths = Paths(Path.Combine(root, "custom-no-contest"));
        await WriteRelativeTextAsync(customPaths.MinecraftDirectory, managedPath, "managed");
        string contestElsewhere = "renderDistance:31\nkey_key.companion_bonds.open_contest_tracker:key.keyboard.o\n"
            + irisK + "\n" + fancyK + "\n";
        await WriteRelativeTextAsync(customPaths.MinecraftDirectory, optionsPath, contestElsewhere);
        InstalledState customState = StateFrom(signedBase, baseHash);
        customState.OfferedSeedPaths = [optionsPath];
        var customEngine = new UpdateEngine(customPaths, new UpdaterConfiguration { AllowedRoots = ["mods"] }, _ => { });
        Equal(true, await customEngine.HasPendingCorrectiveWorkAsync(
            delta, customState, NoCancellation), "unprocessed migration is inspected once even for a custom non-colliding layout");
        await customEngine.ApplyTransactionAsync(
            delta,
            HashText("key-collision-custom-delta"),
            extract,
            customState,
            signedBase,
            NoCancellation);
        Equal(contestElsewhere, await ReadRelativeTextAsync(customPaths.MinecraftDirectory, optionsPath),
            "player key choices survive when Contest Tracker is not on K");
        InstalledState customInstalledState = LocalStateStore.LoadState(customPaths);
        Equal(migrationId, customInstalledState.AppliedPlayerSettingMigrationIds.Single(), "custom layout records migration inspection once");
        Equal(false, await customEngine.HasPendingCorrectiveWorkAsync(
            delta, customInstalledState, NoCancellation), "custom layout is not reinspected after its ledger commit");

        UpdaterPaths duplicatePaths = Paths(Path.Combine(root, "duplicate-key"));
        await WriteRelativeTextAsync(duplicatePaths.MinecraftDirectory, managedPath, "managed");
        string duplicateKey = contestTrackerK + "\n" + irisK + "\n" + irisK + "\n" + fancyK + "\n";
        await WriteRelativeTextAsync(duplicatePaths.MinecraftDirectory, optionsPath, duplicateKey);
        InstalledState duplicateState = StateFrom(signedBase, baseHash);
        duplicateState.OfferedSeedPaths = [optionsPath];
        var duplicateEngine = new UpdateEngine(duplicatePaths, new UpdaterConfiguration { AllowedRoots = ["mods"] }, _ => { });
        await duplicateEngine.ApplyTransactionAsync(
            delta,
            HashText("key-collision-duplicate-delta"),
            extract,
            duplicateState,
            signedBase,
            NoCancellation);
        Equal(duplicateKey, await ReadRelativeTextAsync(duplicatePaths.MinecraftDirectory, optionsPath),
            "ambiguous duplicate bindings make the whole migration preserve the file");
        Equal(migrationId, LocalStateStore.LoadState(duplicatePaths).AppliedPlayerSettingMigrationIds.Single(),
            "ambiguous file is still marked inspected so it cannot loop");
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
        next.OfferedSeedPaths = ["options.txt"];
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

        UpdaterPaths refreshedSeedPaths = Paths(Path.Combine(root, "committed-refreshed-seed"));
        Directory.CreateDirectory(refreshedSeedPaths.MinecraftDirectory);
        Directory.CreateDirectory(refreshedSeedPaths.InstallationDirectory);
        const string refreshedSeedRelative = "config/packed_packs/profiles/resourcepacks/Default.profile.json";
        string refreshedSeedTarget = PathSafety.CombineUnder(refreshedSeedPaths.MinecraftDirectory, refreshedSeedRelative);
        string refreshedSeedBackup = Path.Combine(
            refreshedSeedPaths.LocalDataDirectory,
            "rollback",
            "tx",
            "files",
            refreshedSeedRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(refreshedSeedTarget)!);
        Directory.CreateDirectory(Path.GetDirectoryName(refreshedSeedBackup)!);
        await File.WriteAllTextAsync(refreshedSeedTarget, "new-profile");
        await File.WriteAllTextAsync(refreshedSeedBackup, "old-profile");
        var refreshedPrevious = new InstalledState { Version = "1.0.12", ManifestSha256 = HashText("old-release") };
        var refreshedNext = new InstalledState
        {
            Version = "1.0.13",
            ManifestSha256 = HashText("new-release"),
            OfferedSeedPaths = [refreshedSeedRelative]
        };
        var refreshedJournal = new TransactionJournal
        {
            Phase = "filesApplied",
            PreviousState = refreshedPrevious,
            NextState = refreshedNext,
            SeedFiles =
            [
                new ManagedFileState
                {
                    Path = refreshedSeedRelative,
                    Size = "new-profile".Length,
                    Sha256 = HashText("new-profile")
                }
            ],
            Operations =
            [
                new TransactionOperation
                {
                    Kind = "delete",
                    TargetPath = refreshedSeedTarget,
                    BackupPath = refreshedSeedBackup,
                    OriginalSize = "old-profile".Length,
                    OriginalSha256 = HashText("old-profile")
                },
                new TransactionOperation
                {
                    Kind = "create",
                    TargetPath = refreshedSeedTarget
                }
            ]
        };
        await LocalStateStore.SaveStateAsync(refreshedSeedPaths, refreshedNext, NoCancellation);
        await TransactionStore.SaveAsync(refreshedSeedPaths, refreshedJournal, NoCancellation);
        await TransactionStore.RecoverIfNeededAsync(refreshedSeedPaths, BuildInfo.SupportedRoots, _ => { });
        Equal("new-profile", await File.ReadAllTextAsync(refreshedSeedTarget), "committed exact seed refresh survives recovery");
        Equal(false, File.Exists(TransactionStore.JournalPath(refreshedSeedPaths)), "committed seed refresh journal cleanup");

        await TestOptionsMigrationJournalRecoveryAsync(Path.Combine(root, "options-migration-recovery"));

        UpdaterPaths mismatchedCommitPaths = Paths(Path.Combine(root, "committed-mismatch"));
        Directory.CreateDirectory(mismatchedCommitPaths.MinecraftDirectory);
        Directory.CreateDirectory(mismatchedCommitPaths.InstallationDirectory);
        target = PathSafety.CombineUnder(mismatchedCommitPaths.MinecraftDirectory, "mods/example.jar");
        backup = Path.Combine(mismatchedCommitPaths.LocalDataDirectory, "rollback", "tx", "files", "mods", "example.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await File.WriteAllTextAsync(target, "BAD"); // same size as next state, wrong hash
        await File.WriteAllTextAsync(backup, "old");
        await LocalStateStore.SaveStateAsync(mismatchedCommitPaths, next, NoCancellation);
        await TransactionStore.SaveAsync(
            mismatchedCommitPaths,
            Journal(target, backup, previous, next, "filesApplied"),
            NoCancellation);
        await ThrowsAsync<TransactionRecoveryException>(() => TransactionStore.RecoverIfNeededAsync(
            mismatchedCommitPaths,
            BuildInfo.SupportedRoots,
            _ => { }));
        Equal("BAD", await File.ReadAllTextAsync(target), "forward recovery mismatch must not guess or overwrite target");
        Equal(true, File.Exists(backup), "forward recovery mismatch must retain rollback backup");
        Equal(true, File.Exists(TransactionStore.JournalPath(mismatchedCommitPaths)), "forward recovery mismatch must retain journal");

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

    private static async Task TestOptionsMigrationJournalRecoveryAsync(string root)
    {
        const string optionsRelative = "options.txt";
        const string oldOptions = "renderDistance:23\r\nkey_iris.keybind.toggleShaders:key.keyboard.k\r\n";
        const string newOptions = "renderDistance:23\r\nkey_iris.keybind.toggleShaders:key.keyboard.unknown\r\n";
        const string migrationId = "options-contest-tracker-k-collision-v1";

        static TransactionJournal OptionsJournal(
            string target,
            string backup,
            InstalledState previous,
            InstalledState next) => new()
            {
                Phase = "filesApplied",
                PreviousState = previous,
                NextState = next,
                SeedFiles =
                [
                    new ManagedFileState
                    {
                        Path = optionsRelative,
                        Size = newOptions.Length,
                        Sha256 = HashText(newOptions)
                    }
                ],
                Operations =
                [
                    new TransactionOperation
                    {
                        Kind = "replace",
                        TargetPath = target,
                        BackupPath = backup,
                        OriginalSize = oldOptions.Length,
                        OriginalSha256 = HashText(oldOptions)
                    }
                ]
            };

        static (InstalledState Previous, InstalledState Next) States()
        {
            var previous = new InstalledState
            {
                Version = "1.0.13",
                ManifestSha256 = HashText("options-base"),
                OfferedSeedPaths = [optionsRelative]
            };
            var next = new InstalledState
            {
                Version = "1.0.14",
                ManifestSha256 = HashText("options-next"),
                OfferedSeedPaths = [optionsRelative],
                AppliedPlayerSettingMigrationIds = [migrationId]
            };
            return (previous, next);
        }

        foreach ((string scenario, bool replacementVisible) in new[]
        {
            ("after-backup", false),
            ("after-replacement", true)
        })
        {
            UpdaterPaths paths = Paths(Path.Combine(root, scenario));
            Directory.CreateDirectory(paths.MinecraftDirectory);
            Directory.CreateDirectory(paths.InstallationDirectory);
            string target = PathSafety.CombineUnder(paths.MinecraftDirectory, optionsRelative);
            string backup = Path.Combine(paths.LocalDataDirectory, "rollback", "tx", "files", optionsRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await File.WriteAllTextAsync(backup, oldOptions);
            if (replacementVisible)
            {
                await File.WriteAllTextAsync(target, newOptions);
            }
            (InstalledState previous, InstalledState next) = States();
            await LocalStateStore.SaveStateAsync(paths, previous, NoCancellation);
            await TransactionStore.SaveAsync(paths, OptionsJournal(target, backup, previous, next), NoCancellation);

            await TransactionStore.RecoverIfNeededAsync(paths, BuildInfo.SupportedRoots, _ => { });

            Equal(oldOptions, await File.ReadAllTextAsync(target), $"{scenario} restores exact player options bytes");
            Equal(false, LocalStateStore.LoadState(paths).AppliedPlayerSettingMigrationIds.Contains(migrationId),
                $"{scenario} restores the previous migration ledger");
            Equal(false, File.Exists(TransactionStore.JournalPath(paths)), $"{scenario} clears the recovered journal");
        }

        UpdaterPaths committedPaths = Paths(Path.Combine(root, "after-state-commit"));
        Directory.CreateDirectory(committedPaths.MinecraftDirectory);
        Directory.CreateDirectory(committedPaths.InstallationDirectory);
        string committedTarget = PathSafety.CombineUnder(committedPaths.MinecraftDirectory, optionsRelative);
        string committedBackup = Path.Combine(committedPaths.LocalDataDirectory, "rollback", "tx", "files", optionsRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(committedBackup)!);
        await File.WriteAllTextAsync(committedTarget, newOptions);
        await File.WriteAllTextAsync(committedBackup, oldOptions);
        (InstalledState committedPrevious, InstalledState committedNext) = States();
        await LocalStateStore.SaveStateAsync(committedPaths, committedNext, NoCancellation);
        await TransactionStore.SaveAsync(
            committedPaths,
            OptionsJournal(committedTarget, committedBackup, committedPrevious, committedNext),
            NoCancellation);

        await TransactionStore.RecoverIfNeededAsync(committedPaths, BuildInfo.SupportedRoots, _ => { });

        Equal(newOptions, await File.ReadAllTextAsync(committedTarget), "committed options migration keeps exact migrated bytes");
        Equal(migrationId, LocalStateStore.LoadState(committedPaths).AppliedPlayerSettingMigrationIds.Single(),
            "committed options migration keeps its one-time ledger entry");
        Equal(false, File.Exists(TransactionStore.JournalPath(committedPaths)), "committed options migration clears the journal");
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

    private static async Task TestCrossVolumeTransactionRecoveryAsync(string root)
    {
        TransactionStore.ForceCrossVolumeCopyForTests = true;
        try
        {
            await TestInterruptedCrossVolumeCreateAsync(Path.Combine(root, "create"));
            await TestInterruptedCrossVolumeReplaceAsync(Path.Combine(root, "replace"));
            await TestInterruptedCrossVolumeRollbackAsync(Path.Combine(root, "rollback"));
        }
        finally
        {
            TransactionStore.CopyStageHookForTests = null;
            TransactionStore.ForceCrossVolumeCopyForTests = false;
        }
    }

    private static async Task TestInterruptedCrossVolumeCreateAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        Directory.CreateDirectory(paths.InstallationDirectory);
        string target = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/create.jar");
        string source = Path.Combine(root, "source", "create.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        byte[] payload = PayloadBytes(700_000, 17);
        await File.WriteAllBytesAsync(source, payload);
        var previous = new InstalledState();
        var next = StateForBytes("1.0.5", HashText("create-next"), "mods/create.jar", payload);
        var operation = new TransactionOperation
        {
            Kind = "create",
            TargetPath = target,
            TargetTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(target)
        };
        await LocalStateStore.SaveStateAsync(paths, previous, NoCancellation);
        await TransactionStore.SaveAsync(paths, new TransactionJournal
        {
            PreviousState = previous,
            NextState = next,
            Operations = [operation]
        }, NoCancellation);

        TransactionStore.CopyStageHookForTests = ThrowOnFirstCopyChunk();
        await ThrowsAsync<IOException>(() => TransactionStore.MoveOrCopyNewAsync(
            source,
            target,
            operation.TargetTemporaryPath,
            payload.LongLength,
            HashBytes(payload),
            NoCancellation));
        TransactionStore.CopyStageHookForTests = null;
        Equal(false, File.Exists(target), "interrupted create never exposes a partial final target");
        Equal(true, File.Exists(operation.TargetTemporaryPath), "interrupted create retains its journaled temporary artifact");
        Equal(true, new FileInfo(operation.TargetTemporaryPath).Length < payload.LongLength, "interrupted create temp is partial");

        await TransactionStore.RecoverIfNeededAsync(paths, BuildInfo.SupportedRoots, _ => { });
        Equal(false, File.Exists(target), "create rollback leaves final target absent");
        Equal(false, File.Exists(operation.TargetTemporaryPath), "create rollback removes transaction temp");
        Equal(true, File.Exists(source), "interrupted create retains verified source");
        Equal(false, File.Exists(TransactionStore.JournalPath(paths)), "create rollback clears journal");
    }

    private static async Task TestInterruptedCrossVolumeReplaceAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        Directory.CreateDirectory(paths.InstallationDirectory);
        string target = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/replace.jar");
        string backup = Path.Combine(paths.LocalDataDirectory, "rollback", "tx", "files", "mods", "replace.jar");
        string source = Path.Combine(root, "source", "replace.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        byte[] oldPayload = PayloadBytes(600_000, 31);
        byte[] newPayload = PayloadBytes(700_000, 47);
        await File.WriteAllBytesAsync(backup, oldPayload);
        await File.WriteAllBytesAsync(source, newPayload);
        InstalledState previous = StateForBytes("1.0.4", HashText("replace-base"), "mods/replace.jar", oldPayload);
        InstalledState next = StateForBytes("1.0.5", HashText("replace-next"), "mods/replace.jar", newPayload);
        TransactionOperation operation = ReplacementOperation(target, backup, oldPayload);
        await LocalStateStore.SaveStateAsync(paths, previous, NoCancellation);
        await TransactionStore.SaveAsync(paths, new TransactionJournal
        {
            PreviousState = previous,
            NextState = next,
            Operations = [operation]
        }, NoCancellation);

        TransactionStore.CopyStageHookForTests = ThrowOnFirstCopyChunk();
        await ThrowsAsync<IOException>(() => TransactionStore.MoveOrCopyNewAsync(
            source,
            target,
            operation.TargetTemporaryPath,
            newPayload.LongLength,
            HashBytes(newPayload),
            NoCancellation));
        TransactionStore.CopyStageHookForTests = null;
        Equal(false, File.Exists(target), "interrupted replacement never exposes a partial final target");

        await TransactionStore.RecoverIfNeededAsync(paths, BuildInfo.SupportedRoots, _ => { });
        Equal(HashBytes(oldPayload), await PathSafety.Sha256Async(target, NoCancellation), "replacement interruption restores exact old file");
        Equal(false, File.Exists(operation.TargetTemporaryPath), "replacement recovery removes transaction temp");
        Equal(false, File.Exists(backup), "replacement recovery consumes rollback backup");
        Equal(true, File.Exists(source), "replacement interruption retains verified payload source");
    }

    private static async Task TestInterruptedCrossVolumeRollbackAsync(string root)
    {
        UpdaterPaths paths = Paths(root);
        Directory.CreateDirectory(paths.MinecraftDirectory);
        Directory.CreateDirectory(paths.InstallationDirectory);
        string target = PathSafety.CombineUnder(paths.MinecraftDirectory, "mods/rollback.jar");
        string backup = Path.Combine(paths.LocalDataDirectory, "rollback", "tx", "files", "mods", "rollback.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] oldPayload = PayloadBytes(700_000, 59);
        byte[] newPayload = PayloadBytes(700_000, 71);
        await File.WriteAllBytesAsync(target, newPayload);
        await File.WriteAllBytesAsync(backup, oldPayload);
        InstalledState previous = StateForBytes("1.0.4", HashText("rollback-base"), "mods/rollback.jar", oldPayload);
        InstalledState next = StateForBytes("1.0.5", HashText("rollback-next"), "mods/rollback.jar", newPayload);
        TransactionOperation operation = ReplacementOperation(target, backup, oldPayload);
        await LocalStateStore.SaveStateAsync(paths, previous, NoCancellation);
        await TransactionStore.SaveAsync(paths, new TransactionJournal
        {
            PreviousState = previous,
            NextState = next,
            Operations = [operation]
        }, NoCancellation);

        TransactionStore.CopyStageHookForTests = ThrowOnFirstCopyChunk();
        await ThrowsAsync<TransactionRecoveryException>(() => TransactionStore.RecoverIfNeededAsync(
            paths,
            BuildInfo.SupportedRoots,
            _ => { }));
        TransactionStore.CopyStageHookForTests = null;
        Equal(false, File.Exists(target), "interrupted rollback never leaves a partial final target");
        Equal(true, File.Exists(backup), "interrupted rollback retains exact backup source");
        Equal(true, File.Exists(operation.TargetTemporaryPath), "interrupted rollback retains journaled partial temp");
        Equal(true, File.Exists(TransactionStore.JournalPath(paths)), "interrupted rollback retains journal");

        await TransactionStore.RecoverIfNeededAsync(paths, BuildInfo.SupportedRoots, _ => { });
        Equal(HashBytes(oldPayload), await PathSafety.Sha256Async(target, NoCancellation), "second recovery restores exact rollback file");
        Equal(false, File.Exists(operation.TargetTemporaryPath), "second recovery cleans partial rollback temp");
        Equal(false, File.Exists(backup), "second recovery consumes rollback backup");
        Equal(false, File.Exists(TransactionStore.JournalPath(paths)), "second recovery clears journal");
    }

    private static TransactionOperation ReplacementOperation(string target, string backup, byte[] oldPayload) => new()
    {
        Kind = "replace",
        TargetPath = target,
        BackupPath = backup,
        OriginalSize = oldPayload.LongLength,
        OriginalSha256 = HashBytes(oldPayload),
        TargetTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(target),
        BackupTemporaryPath = TransactionStore.CreateSiblingTemporaryPath(backup)
    };

    private static Action<TransactionCopyStage> ThrowOnFirstCopyChunk()
    {
        bool thrown = false;
        return stage =>
        {
            if (!thrown && stage == TransactionCopyStage.Copying)
            {
                thrown = true;
                throw new IOException("Simulated interruption during cross-volume copy.");
            }
        };
    }

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

    private static InstalledState StateForBytes(string version, string manifestHash, string path, byte[] content) => new()
    {
        Version = version,
        ManifestSha256 = manifestHash,
        ManagedFiles =
        [
            new ManagedFileState { Path = path, Size = content.LongLength, Sha256 = HashBytes(content) }
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

    private static UpdateManifest CloneManifest(UpdateManifest manifest) =>
        JsonSerializer.Deserialize<UpdateManifest>(JsonSerializer.Serialize(manifest))
        ?? throw new InvalidOperationException("Could not clone test manifest.");

    private static UpdaterChannelDescriptor ChannelDescriptor() => new()
    {
        SchemaVersion = 1,
        ProductId = UpdaterChannelParser.ProductId,
        Repository = BuildInfo.DefaultRepository,
        Channel = UpdaterChannelParser.StableChannel,
        UpdaterVersion = "1.2.7",
        ReleaseTag = "updater-v1.2.7",
        Updater = new UpdaterChannelAsset
        {
            Name = UpdaterChannelParser.UpdaterAssetName,
            Size = UpdaterChannelParser.MinimumUpdaterBytes,
            Sha256 = new string('a', 64)
        }
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

    private static string HashBytes(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] PayloadBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

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

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_response(request));
    }
}
