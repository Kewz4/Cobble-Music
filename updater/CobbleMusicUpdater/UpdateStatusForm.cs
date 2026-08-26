using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CobbleMusicUpdater;

internal sealed class UpdateStatusForm : Form
{
    private const int CornerRadius = 18;

    private readonly CommandLine _options;
    private readonly Func<CommandLine, IProgress<UpdateProgress>?, Task<int>> _runUpdater;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly SmoothProgressIndicator _progressIndicator;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _closeTimer;
    private bool _canClose;

    public int ExitCode { get; private set; } = 1;

    private UpdateStatusForm(
        CommandLine options,
        Func<CommandLine, IProgress<UpdateProgress>?, Task<int>> runUpdater)
    {
        _options = options;
        _runUpdater = runUpdater;

        Text = "Kewz's Cobblemon";
        ClientSize = new Size(454, 174);
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
        ApplyRoundedRegion();
        SizeChanged += (_, _) => ApplyRoundedRegion();

        var titleLabel = new Label
        {
            AutoSize = true,
            Location = new Point(25, 22),
            Font = new Font("Segoe UI", 15F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(239, 230, 255),
            Text = "Kewz's Cobblemon"
        };
        var subtitleLabel = new Label
        {
            AutoSize = true,
            Location = new Point(27, 50),
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(160, 153, 181),
            Text = "Preparing your adventure"
        };
        _statusLabel = new Label
        {
            AutoEllipsis = true,
            Location = new Point(25, 82),
            Size = new Size(404, 23),
            Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(251, 249, 255),
            Text = "Checking for updates…"
        };
        _detailLabel = new Label
        {
            AutoEllipsis = true,
            Location = new Point(26, 106),
            Size = new Size(402, 18),
            Font = new Font("Segoe UI", 8.25F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Color.FromArgb(166, 160, 185),
            Text = "Securely checking the latest release"
        };
        _progressIndicator = new SmoothProgressIndicator
        {
            Location = new Point(26, 137),
            Size = new Size(402, 8),
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            IsIndeterminate = true
        };
        _closeButton = new Button
        {
            Text = "Close",
            Location = new Point(348, 137),
            Size = new Size(80, 28),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
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

        Controls.Add(titleLabel);
        Controls.Add(subtitleLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_detailLabel);
        Controls.Add(_progressIndicator);
        Controls.Add(_closeButton);
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

    protected override void OnShown(EventArgs eventArgs)
    {
        base.OnShown(eventArgs);
        _ = StartUpdateAsync();
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
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using GraphicsPath path = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius);
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
            _progressIndicator.Visible = false;
            _closeButton.Visible = true;
        }
    }

    private void DisplayProgress(UpdateProgress update)
    {
        if (IsDisposed)
        {
            return;
        }

        _statusLabel.Text = Describe(update);
        _detailLabel.Text = DetailFor(update);
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
        if (Width <= 0 || Height <= 0)
        {
            return;
        }
        using GraphicsPath path = CreateRoundedPath(ClientRectangle, CornerRadius);
        Region = new Region(path);
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        int diameter = radius * 2;
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
