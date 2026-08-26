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

    public static async Task<int> Main()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "cobble-updater-delta-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            TestCalculatedUpdaterWindowLayoutAt120Dpi();
            TestUpdaterWindowLayout();
            TestAggregateDownloadProgressAndTransferMetrics();
            TestManifestSchemaTwoValidation();
            TestSequentialReleaseChain();
            TestInstanceIdentityNormalization(Path.Combine(tempRoot, "identity"));
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

    private static async Task WriteRelativeTextAsync(string root, string relativePath, string content)
    {
        string path = PathSafety.CombineUnder(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
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
