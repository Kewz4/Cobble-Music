using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CobbleMusicUpdater;

internal readonly record struct UpdateStatusLayout(
    Size ClientSize,
    Rectangle TitleBounds,
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
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
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
        _progressIndicator = new SmoothProgressIndicator
        {
            Name = "progressIndicator",
            IsIndeterminate = true
        };
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
            CreateParams parameters = base.CreateParams;
            parameters.ClassStyle |= CsDropShadow;
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
        var titleBounds = new Rectangle(outerLeft, top, contentWidth, titleHeight);

        top = titleBounds.Bottom + Scale(3);
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

        if (ExitCode == 0)
        {
            _closeTimer.Start();
        }
        else
        {
            _statusLabel.ForeColor = Color.FromArgb(255, 193, 204);
            _detailLabel.ForeColor = Color.FromArgb(223, 167, 178);
            _showCloseButton = true;
            _progressIndicator.Visible = false;
            _closeButton.Visible = true;
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
        _detailLabel.Text = update.Phase == UpdatePhase.Downloading && update.TotalBytes > 0
            ? TransferMetricsFormatter.FormatDownloadDetail(update, transferMetrics)
            : DetailFor(update);
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
        _ => ""
    };

    private void ApplyRoundedRegion()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }
        using GraphicsPath path = CreateRoundedPath(ClientRectangle, ScaleLogical(CornerRadius));
        Region = new Region(path);
    }

    private void LayoutContent()
    {
        if (!IsHandleCreated || _layingOutContent || _titleLabel is null)
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
            int insetContentWidth = Math.Max(1, contentWidth - ScaleLogical(1));
            UpdateStatusLayout layout = CalculateLayout(
                DeviceDpi,
                PreferredHeight(_titleLabel, contentWidth),
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
