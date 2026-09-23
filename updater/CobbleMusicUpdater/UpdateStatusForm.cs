using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CobbleMusicUpdater;

internal readonly record struct UpdateStatusLayout(
    Size ClientSize,
    Rectangle TitleBounds,
    Rectangle MinimizeBounds,
    Rectangle ForceCloseBounds,
    Rectangle SubtitleBounds,
    Rectangle StatusBounds,
    Rectangle DetailBounds,
    Rectangle ProgressBounds,
    Rectangle CloseBounds);

internal sealed class UpdateStatusForm : Form
{
    private const int DesignDpi = 96;
    private const int DesignWidth = 520;
    private const int DesignHeight = 174;
    private const int CornerRadius = 18;

    private readonly CommandLine _options;
    private readonly Func<CommandLine, IProgress<UpdateProgress>?, Task<int>> _runUpdater;
    private readonly Label _titleLabel;
    private readonly Label _subtitleLabel;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly SmoothProgressIndicator _progressIndicator;
    private readonly Button _minimizeButton;
    private readonly Button _forceCloseButton;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _closeTimer;
    private readonly TransferMetricsTracker _transferMetrics = new(Stopwatch.Frequency);
    private bool _canClose;
    private bool _layingOutContent;
    private bool _showCloseButton;

    public int ExitCode { get; private set; } = 1;

    internal UpdateStatusForm(
        CommandLine options,
        Func<CommandLine, IProgress<UpdateProgress>?, Task<int>> runUpdater)
    {
        _options = options;
        _runUpdater = runUpdater;

        SuspendLayout();
        Text = "Kewz's Cobblemon";
        ClientSize = new Size(DesignWidth, DesignHeight);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = true;
        ControlBox = false;
        ShowInTaskbar = true;
        TopMost = false;
        BackColor = Color.FromArgb(22, 21, 31);
        ForeColor = Color.FromArgb(247, 245, 255);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        DoubleBuffered = true;

        _titleLabel = new Label
        {
            Name = "titleLabel",
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 15F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(239, 230, 255),
            Text = "Kewz's Cobblemon"
        };
        _subtitleLabel = new Label
        {
            Name = "subtitleLabel",
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(160, 153, 181),
            Text = "Preparing your adventure"
        };
        _statusLabel = new Label
        {
            Name = "statusLabel",
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(251, 249, 255),
            Text = "Checking for updates…"
        };
        _detailLabel = new Label
        {
            Name = "detailLabel",
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(166, 160, 185),
            Text = "Securely checking the latest release"
        };
        // Kewz's report: the borderless card could not be moved. Dragging any passive
        // surface starts the native window drag; the buttons keep their own clicks.
        foreach (Control dragSurface in new Control[] { this, _titleLabel, _subtitleLabel, _statusLabel, _detailLabel })
        {
            dragSurface.MouseDown += BeginWindowDrag;
        }
        _progressIndicator = new SmoothProgressIndicator
        {
            Name = "progressIndicator",
            IsIndeterminate = true
        };
        _minimizeButton = new Button
        {
            Name = "minimizeButton",
            Text = "−",
            AccessibleName = "Minimize",
            AccessibleDescription = "Minimize to the taskbar while the update continues.",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(57, 53, 73),
            ForeColor = Color.FromArgb(239, 230, 255),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
            TabIndex = 0
        };
        _minimizeButton.FlatAppearance.BorderSize = 0;
        _minimizeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(84, 72, 113);
        _minimizeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(43, 38, 58);
        _minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        // Kewz's request: an always-available X. [lock-v2] It now stops the run
        // safely: outside a transaction it cancels and exits within 5 s (downloads
        // resume by Range); during Applying/Recovering it rolls back first, and a
        // second X asks before a hard exit (see OnForceCloseClicked).
        _forceCloseButton = new Button
        {
            Name = "forceCloseButton",
            Text = "×",
            AccessibleName = "Close updater",
            AccessibleDescription = "Stops the update safely and closes the updater; a download resumes on the next launch.",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(57, 53, 73),
            ForeColor = Color.FromArgb(239, 230, 255),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
            TabIndex = 1
        };
        _forceCloseButton.FlatAppearance.BorderSize = 0;
        _forceCloseButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(150, 68, 88);
        _forceCloseButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(110, 46, 62);
        _forceCloseButton.Click += (_, _) => OnForceCloseClicked(); // [lock-v2]
        _closeButton = new Button
        {
            Name = "closeButton",
            Text = "Close",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(101, 72, 154),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold, GraphicsUnit.Point),
            Visible = false,
            TabStop = false
        };
        _closeButton.FlatAppearance.BorderSize = 0;
        _closeButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(124, 90, 185);
        _closeButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(77, 53, 120);
        _closeButton.Click += (_, _) =>
        {
            // [lock-v2] While another updater holds the lock, this button is "Stop it and continue".
            if (_offeringTakeover)
            {
                AcceptTakeoverOffer();
                return;
            }
            _canClose = true;
            Close();
        };
        // A short dwell makes the normal result visible without turning every
        // Prism launch into a noticeable delay.
        _closeTimer = new System.Windows.Forms.Timer { Interval = 1100 };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            _canClose = true;
            Close();
        };

        Controls.Add(_titleLabel);
        Controls.Add(_subtitleLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_detailLabel);
        Controls.Add(_progressIndicator);
        Controls.Add(_minimizeButton);
        Controls.Add(_forceCloseButton);
        Controls.Add(_closeButton);
        AutoScaleDimensions = new SizeF(DesignDpi, DesignDpi);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(performLayout: false);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int CsDropShadow = 0x00020000;
            const int WsSysMenu = 0x00080000;
            const int WsMinimizeBox = 0x00020000;
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
            // Borderless forms omit these styles even when MinimizeBox is true.
            // Keep native taskbar minimize/restore support without a title bar.
            parameters.Style |= WsSysMenu | WsMinimizeBox;
            return parameters;
        }
    }

    public static int Run(
        CommandLine options,
        Func<CommandLine, IProgress<UpdateProgress>?, Task<int>> runUpdater)
    {
        ApplicationConfiguration.Initialize();
        using var form = new UpdateStatusForm(options, runUpdater);
        Application.Run(form);
        return form.ExitCode;
    }

    internal static UpdateStatusLayout CalculateLayout(
        int dpi,
        int titlePreferredHeight,
        int subtitlePreferredHeight,
        int statusPreferredHeight,
        int detailPreferredHeight,
        Size closePreferredSize,
        bool showCloseButton)
    {
        if (dpi <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpi));
        }

        int Scale(int value) => ScaleLogical(value, dpi);

        int outerLeft = Scale(25);
        int outerRight = Scale(25);
        int insetLeft = Scale(1);
        int progressLeft = outerLeft + insetLeft;
        int progressRight = outerRight + insetLeft;
        int activeClosePreferredWidth = showCloseButton ? closePreferredSize.Width : 0;
        int activeClosePreferredHeight = showCloseButton ? closePreferredSize.Height : 0;
        int progressHeight = Math.Max(2, Scale(8));
        int closeWidth = Math.Max(Scale(80), activeClosePreferredWidth);
        int closeHeight = showCloseButton
            ? Math.Max(Scale(28), activeClosePreferredHeight)
            : progressHeight;
        int clientWidth = CalculateClientWidth(dpi, activeClosePreferredWidth);
        int contentWidth = Math.Max(1, clientWidth - outerLeft - outerRight);

        int top = Scale(22);
        int titleHeight = Math.Max(Scale(25), titlePreferredHeight);
        int minimizeWidth = Scale(36);
        int forceCloseWidth = Scale(36);
        int buttonHeight = Math.Max(Scale(28), titleHeight);
        var forceCloseBounds = new Rectangle(
            clientWidth - outerRight - forceCloseWidth,
            top,
            forceCloseWidth,
            buttonHeight);
        var minimizeBounds = new Rectangle(
            forceCloseBounds.Left - minimizeWidth,
            top,
            minimizeWidth,
            buttonHeight);
        var titleBounds = new Rectangle(
            outerLeft,
            top,
            Math.Max(1, contentWidth - minimizeWidth - forceCloseWidth - Scale(8)),
            titleHeight);

        top = Math.Max(titleBounds.Bottom, minimizeBounds.Bottom) + Scale(3);
        int subtitleHeight = Math.Max(Scale(17), subtitlePreferredHeight);
        var subtitleBounds = new Rectangle(
            outerLeft + insetLeft,
            top,
            Math.Max(1, contentWidth - insetLeft),
            subtitleHeight);

        top = subtitleBounds.Bottom + Scale(15);
        int statusHeight = Math.Max(Scale(23), statusPreferredHeight);
        var statusBounds = new Rectangle(outerLeft, top, contentWidth, statusHeight);

        top = statusBounds.Bottom + Scale(1);
        int detailHeight = Math.Max(Scale(18), detailPreferredHeight);
        var detailBounds = new Rectangle(
            outerLeft + insetLeft,
            top,
            Math.Max(1, contentWidth - insetLeft),
            detailHeight);

        int footerTop = detailBounds.Bottom + Scale(13);
        int progressWidth = Math.Max(1, clientWidth - progressLeft - progressRight);
        var progressBounds = new Rectangle(
            progressLeft,
            footerTop,
            progressWidth,
            progressHeight);
        var closeBounds = new Rectangle(
            clientWidth - progressRight - closeWidth,
            footerTop,
            closeWidth,
            closeHeight);

        int footerBottom = showCloseButton ? closeBounds.Bottom : progressBounds.Bottom;
        int requiredHeight = footerBottom + Scale(9);
        int clientHeight = Math.Max(Scale(DesignHeight), requiredHeight);
        return new UpdateStatusLayout(
            new Size(clientWidth, clientHeight),
            titleBounds,
            minimizeBounds,
            forceCloseBounds,
            subtitleBounds,
            statusBounds,
            detailBounds,
            progressBounds,
            closeBounds);
    }

    private static int CalculateClientWidth(int dpi, int closePreferredWidth)
    {
        int horizontalFooterMargins = 2 * (ScaleLogical(25, dpi) + ScaleLogical(1, dpi));
        int closeWidth = Math.Max(ScaleLogical(80, dpi), closePreferredWidth);
        return Math.Max(ScaleLogical(DesignWidth, dpi), horizontalFooterMargins + closeWidth);
    }

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        _ = StartUpdateAsync();
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        PerformLayout();
        ApplyRoundedRegion();
    }

    protected override void OnLayout(LayoutEventArgs layoutEventArgs)
    {
        base.OnLayout(layoutEventArgs);
        LayoutContent();
    }

    protected override void OnSizeChanged(EventArgs eventArgs)
    {
        base.OnSizeChanged(eventArgs);
        // Reapply deferred progress/error layout when restored from the taskbar.
        PerformLayout();
        ApplyRoundedRegion();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs eventArgs)
    {
        base.OnDpiChanged(eventArgs);
        PerformLayout();
        ApplyRoundedRegion();
    }

    protected override void OnFormClosing(FormClosingEventArgs eventArgs)
    {
        if (!_canClose)
        {
            eventArgs.Cancel = true;
            return;
        }
        base.OnFormClosing(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1)
        {
            return;
        }
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = CreateRoundedPath(
            new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1),
            ScaleLogical(CornerRadius));
        using var border = new Pen(Color.FromArgb(84, 72, 113), 1F);
        eventArgs.Graphics.DrawPath(border, path);
    }

    private async Task StartUpdateAsync()
    {
        var progress = new Progress<UpdateProgress>(DisplayProgress);
        try
        {
            ExitCode = await Task.Run(async () => await _runUpdater(_options, progress));
        }
        catch (Exception exception)
        {
            ExitCode = 1;
            DisplayProgress(new UpdateProgress(UpdatePhase.Blocked, $"Updater failed: {exception.Message}"));
        }
        // [lock-v2] The X (or a later launch's stop event) asked to close: close as soon as the run has ended.
        _runFinished = true;
        _runCompletion.TrySetResult();
        if (_closeRequested || RunControl.Current?.Reason == CancelReason.PeerStop)
        {
            _canClose = true;
            Close();
            return;
        }
        // [/lock-v2]

        // 1.2.18 jvm track: exit code 3 means the launch was stopped on purpose to restart Prism; close like a success.
        if (ExitCode == 0 || ExitCode == JvmSettingsCoordinator.RestartExitCode)
        {
            _closeTimer.Start();
        }
        else
        {
            _statusLabel.ForeColor = Color.FromArgb(255, 193, 204);
            _detailLabel.ForeColor = Color.FromArgb(223, 167, 178);
            _offeringTakeover = false; // [lock-v2]
            _showCloseButton = true;
            _progressIndicator.Visible = false;
            _closeButton.Visible = true;
            StartFailureAutoClose(); // [lock-v2]
            PerformLayout();
        }
    }

    private void DisplayProgress(UpdateProgress update)
    {
        if (IsDisposed)
        {
            return;
        }

        _statusLabel.Text = Describe(update);
        TransferMetrics transferMetrics = _transferMetrics.Observe(update, Stopwatch.GetTimestamp());
        _detailLabel.Text = update.Detail ?? (update.Phase == UpdatePhase.Downloading && update.TotalBytes > 0 // [lock-v2] Detail
            ? TransferMetricsFormatter.FormatDownloadDetail(update, transferMetrics)
            : DetailFor(update));
        ShowTakeoverOffer(update.Phase == UpdatePhase.WaitingCanStop); // [lock-v2]
        switch (update.Phase)
        {
            case UpdatePhase.Downloading when update.TotalBytes > 0:
                _progressIndicator.SetValue((int)Math.Clamp(Math.Round(update.CompletedBytes * 100d / update.TotalBytes), 0, 100));
                break;
            case UpdatePhase.Applying when update.TotalItems > 0:
                _progressIndicator.SetValue((int)Math.Clamp(Math.Round(update.CurrentItem * 100d / update.TotalItems), 0, 100));
                break;
            case UpdatePhase.Complete:
                _progressIndicator.SetValue(100);
                break;
            case UpdatePhase.UpdateAvailable:
                _progressIndicator.SetValue(0);
                break;
            default:
                _progressIndicator.IsIndeterminate = true;
                break;
        }
    }

    private static string Describe(UpdateProgress update)
    {
        if (update.Phase == UpdatePhase.Downloading && update.TotalBytes > 0)
        {
            int percent = (int)Math.Clamp(Math.Round(update.CompletedBytes * 100d / update.TotalBytes), 0, 100);
            return $"Downloading update — {percent}%";
        }
        if (update.Phase == UpdatePhase.Applying && update.TotalItems > 0)
        {
            return $"Installing update — {Math.Min(update.CurrentItem, update.TotalItems)}/{update.TotalItems}";
        }
        return update.Message;
    }

    private static string DetailFor(UpdateProgress update) => update.Phase switch
    {
        UpdatePhase.Checking => "Securely checking the latest release",
        UpdatePhase.VerifyingRelease => "Making sure this update is trusted",
        UpdatePhase.UpdateAvailable => "Verified signed update found",
        UpdatePhase.Downloading => "Keeping your current setup safe while it downloads",
        UpdatePhase.Reassembling => "Putting verified update files together",
        UpdatePhase.Validating => "Validating update files before installation",
        UpdatePhase.Applying => "Applying a recoverable local update",
        UpdatePhase.Complete => "Launching Minecraft…",
        UpdatePhase.Fallback => "Your local pack was left unchanged",
        UpdatePhase.Blocked => "Minecraft will wait until this is resolved",
        UpdatePhase.Waiting or UpdatePhase.WaitingCanStop => "Waiting for the other update to finish", // [lock-v2]


        UpdatePhase.MemorySettings => JvmSettingsText.DetailFor(update.Message), // 1.2.18 jvm track
        _ => ""
    };

    private void ApplyRoundedRegion()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized
            || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }
        using GraphicsPath path = CreateRoundedPath(ClientRectangle, ScaleLogical(CornerRadius));
        Region = new Region(path);
    }

    private void LayoutContent()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized
            || _layingOutContent || _titleLabel is null)
        {
            return;
        }

        _layingOutContent = true;
        try
        {
            Size closePreferredSize = _closeButton.GetPreferredSize(Size.Empty);
            int activeClosePreferredWidth = _showCloseButton ? closePreferredSize.Width : 0;
            int expectedWidth = CalculateClientWidth(DeviceDpi, activeClosePreferredWidth);
            int contentWidth = Math.Max(1, expectedWidth - ScaleLogical(25) - ScaleLogical(25));
            int titleWidth = Math.Max(1, contentWidth - ScaleLogical(36) - ScaleLogical(8));
            int insetContentWidth = Math.Max(1, contentWidth - ScaleLogical(1));
            UpdateStatusLayout layout = CalculateLayout(
                DeviceDpi,
                PreferredHeight(_titleLabel, titleWidth),
                PreferredHeight(_subtitleLabel, insetContentWidth),
                PreferredHeight(_statusLabel, contentWidth),
                PreferredHeight(_detailLabel, insetContentWidth),
                closePreferredSize,
                _showCloseButton);

            if (ClientSize != layout.ClientSize)
            {
                ClientSize = layout.ClientSize;
            }
            _titleLabel.Bounds = layout.TitleBounds;
            _minimizeButton.Bounds = layout.MinimizeBounds;
            _forceCloseButton.Bounds = layout.ForceCloseBounds;
            _subtitleLabel.Bounds = layout.SubtitleBounds;
            _statusLabel.Bounds = layout.StatusBounds;
            _detailLabel.Bounds = layout.DetailBounds;
            _progressIndicator.Bounds = layout.ProgressBounds;
            _closeButton.Bounds = layout.CloseBounds;
        }
        finally
        {
            _layingOutContent = false;
        }
    }

    private static int PreferredHeight(Label label, int width) =>
        label.GetPreferredSize(new Size(Math.Max(1, width), 0)).Height;

    private int ScaleLogical(int value) => ScaleLogical(value, DeviceDpi);

    private static int ScaleLogical(int value, int dpi) =>
        Math.Max(1, (int)Math.Round(value * dpi / (double)DesignDpi));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Dispose();
            _failureCloseTimer?.Dispose(); // [lock-v2]
        }
        base.Dispose(disposing);
    }

    // ---- [lock-v2] X button, takeover offer and failure auto-close ----------------------------------------------

    /// <summary>Lock-lifecycle §7.5: the failure card closes itself after this long, with a countdown on its button.</summary>
    internal const int FailureAutoCloseSeconds = 20;

    private readonly TaskCompletionSource _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private System.Windows.Forms.Timer? _failureCloseTimer;
    private int _failureCloseRemaining;
    private bool _runFinished;
    private bool _closeRequested;
    private bool _cancelRequested;
    private bool _offeringTakeover;

    private async void OnForceCloseClicked()
    {
        RunControl? run = RunControl.Current;
        ForceCloseAction action = ForceClosePolicy.Decide(
            _runFinished,
            run?.OwnTransactionMayBePending ?? false,
            _cancelRequested);
        switch (action)
        {
            case ForceCloseAction.ExitNow:
                if (_runFinished)
                {
                    _canClose = true;
                    Close();
                }
                else
                {
                    Environment.Exit(ExitCode);
                }
                return;
            case ForceCloseAction.CancelThenExit:
                _cancelRequested = true;
                _closeRequested = true;
                run?.RequestCancel(CancelReason.UserClose);
                ShowStatus("Stopping the update…", "Closing in a moment — a download resumes on the next launch");
                await Task.WhenAny(_runCompletion.Task, Task.Delay(ForceClosePolicy.CooperativeExitWait));
                if (_runFinished)
                {
                    return; // StartUpdateAsync closes the card now that the run has ended.
                }
                if (run?.OwnTransactionMayBePending == true)
                {
                    // An install began just as the X was pressed: never walk away from a pending transaction.
                    ShowFinishingSafely();
                    return;
                }
                Environment.Exit(ExitCode);
                return;
            case ForceCloseAction.FinishSafely:
                _cancelRequested = true;
                _closeRequested = true;
                run?.RequestCancel(CancelReason.UserClose);
                ShowFinishingSafely();
                return; // StartUpdateAsync closes the card once the rollback has finished.
            case ForceCloseAction.ConfirmHardExit:
                DialogResult answer = MessageBox.Show(
                    this,
                    "Your pack is being put back the way it was. Closing now can leave it half-installed: "
                    + "this launch will not start Minecraft, and the next launch will repair the pack.\n\nClose anyway?",
                    "Kewz's Cobblemon",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes || _runFinished)
                {
                    return;
                }
                run?.Log("The player force-closed the updater during an install; stopping this Prism launch.");
                LaunchGate.FailPreLaunch(_options.PrismPrelaunch, run?.Log ?? (_ => { }));
                Environment.Exit(1);
                return;
        }
    }

    private void ShowFinishingSafely() =>
        ShowStatus("Finishing safely — undoing the unfinished install", "Closing as soon as your pack is back to how it was");

    private void ShowStatus(string status, string detail)
    {
        if (IsDisposed)
        {
            return;
        }
        _statusLabel.Text = status;
        _detailLabel.Text = detail;
        _progressIndicator.IsIndeterminate = true;
    }

    /// <summary>Shows or withdraws "Stop it and continue" (the footer button) while another updater holds the lock.</summary>
    private void ShowTakeoverOffer(bool offer)
    {
        if (offer == _offeringTakeover || _runFinished)
        {
            return;
        }
        _offeringTakeover = offer;
        _closeButton.Text = offer ? "Stop it and continue" : "Close";
        _closeButton.Visible = offer;
        _showCloseButton = offer;
        _progressIndicator.Visible = !offer;
        PerformLayout();
    }

    private void AcceptTakeoverOffer()
    {
        RunControl.Current?.AcceptTakeover();
        ShowTakeoverOffer(false);
        ShowStatus("Stopping the other update…", "Your download continues where it stopped");
    }

    private void StartFailureAutoClose()
    {
        _failureCloseRemaining = FailureAutoCloseSeconds;
        _closeButton.Text = FailureCloseText(_failureCloseRemaining);
        _failureCloseTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _failureCloseTimer.Tick += (_, _) =>
        {
            _failureCloseRemaining--;
            if (_failureCloseRemaining <= 0)
            {
                _failureCloseTimer.Stop();
                _canClose = true;
                Close();
                return;
            }
            _closeButton.Text = FailureCloseText(_failureCloseRemaining);
        };
        _failureCloseTimer.Start();
    }

    internal static string FailureCloseText(int secondsRemaining) => $"Close ({secondsRemaining})";

    // ---- [/lock-v2] --------------------------------------------------------------------------------------------

    private const int WindowMessageNonClientLeftButtonDown = 0x00A1;
    private const int HitTestCaption = 2;

    // Kewz's report: the borderless card could not be moved. Synthesizing the native
    // non-client drag moves the whole window exactly like a title bar.
    private void BeginWindowDrag(object? sender, MouseEventArgs eventArgs)
    {
        if (eventArgs.Button != MouseButtons.Left || !IsHandleCreated)
        {
            return;
        }
        ReleaseCapture();
        _ = SendMessage(Handle, WindowMessageNonClientLeftButtonDown, new IntPtr(HitTestCaption), IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam);

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class SmoothProgressIndicator : Control
{
    private readonly System.Windows.Forms.Timer _animationTimer;
    private bool _isIndeterminate;
    private float _marqueePosition = -0.3F;
    private int _value;

    public SmoothProgressIndicator()
    {
        DoubleBuffered = true;
        _animationTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _animationTimer.Tick += (_, _) =>
        {
            _marqueePosition += 0.018F;
            if (_marqueePosition > 1.3F)
            {
                _marqueePosition = -0.3F;
            }
            Invalidate();
        };
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IsIndeterminate
    {
        get => _isIndeterminate;
        set
        {
            if (_isIndeterminate == value)
            {
                return;
            }
            _isIndeterminate = value;
            _animationTimer.Enabled = value;
            Invalidate();
        }
    }

    internal void SetValue(int value)
    {
        _value = Math.Clamp(value, 0, 100);
        IsIndeterminate = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle bounds = new(0, 0, Width - 1, Height - 1);
        int radius = Math.Max(1, bounds.Height / 2);
        using GraphicsPath trackPath = CreateRoundedPath(bounds, radius);
        using var trackBrush = new SolidBrush(Color.FromArgb(57, 53, 73));
        eventArgs.Graphics.FillPath(trackBrush, trackPath);

        Rectangle fillBounds;
        if (_isIndeterminate)
        {
            int width = Math.Max(56, bounds.Width / 3);
            int left = (int)Math.Round((bounds.Width + width) * _marqueePosition) - width;
            fillBounds = new Rectangle(left, 0, width, bounds.Height);
        }
        else
        {
            int width = (int)Math.Round(bounds.Width * (_value / 100D));
            if (width <= 0)
            {
                return;
            }
            fillBounds = new Rectangle(0, 0, width, bounds.Height);
        }

        eventArgs.Graphics.SetClip(trackPath);
        using var fillBrush = new LinearGradientBrush(
            fillBounds,
            Color.FromArgb(121, 86, 219),
            Color.FromArgb(204, 126, 255),
            LinearGradientMode.Horizontal);
        eventArgs.Graphics.FillRectangle(fillBrush, fillBounds);
        eventArgs.Graphics.ResetClip();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
