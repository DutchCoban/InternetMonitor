using InternetMonitor.App;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Windows;

/// <summary>
/// A small borderless window - the seven steps as dots, the verdict in one line, and the last
/// fifteen minutes of public-internet latency as a strip - for a corner of a second screen. Off by
/// default and entirely the user's choice: shown from the tray menu or Settings, dragged wherever
/// it should sit, on top of other windows or not, and remembered between runs.
///
/// It paints from the same snapshot the Status page does and subscribes only while it is shown,
/// so a hidden widget costs nothing.
/// </summary>
public sealed class WidgetWindow : Form
{
    private const int WidthDp = 320;
    private const int HeightDp = 118;

    private readonly AppServices _services;
    private readonly SynchronizationContext _ui;
    private readonly MetricChart _chart;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _topMostItem;
    private StepSnapshot? _snapshot;
    private Point? _dragStart;
    private bool _subscribed;

    public WidgetWindow(AppServices services, SynchronizationContext ui)
    {
        _services = services;
        _ui = ui;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        TopMost = services.Settings.WidgetTopMost;
        BackColor = Theme.Current.Bg;
        Text = "Internet Monitor";

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        ClientSize = new Size(Dp(WidthDp), Dp(HeightDp));

        _chart = new MetricChart { Compact = true };
        Controls.Add(_chart);

        _topMostItem = new ToolStripMenuItem { CheckOnClick = true, Checked = TopMost };
        _topMostItem.CheckedChanged += (_, _) =>
        {
            TopMost = _topMostItem.Checked;
            _services.Settings.WidgetTopMost = TopMost;
            _services.Settings.Save();
        };

        var open = new ToolStripMenuItem();
        open.Click += (_, _) => OpenRequested?.Invoke();
        var hide = new ToolStripMenuItem();
        hide.Click += (_, _) => HideRequested?.Invoke();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(open);
        _menu.Items.Add(_topMostItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(hide);
        _menu.Opening += (_, _) =>
        {
            LocalizationManager loc = LocalizationManager.Instance;
            open.Text = loc.Get("menu.open");
            _topMostItem.Text = loc.Get("widget.topMost");
            hide.Text = loc.Get("widget.hide");
        };
        ContextMenuStrip = _menu;

        MouseDown += OnDragStart;
        MouseMove += OnDrag;
        MouseUp += OnDragEnd;
        DoubleClick += (_, _) => OpenRequested?.Invoke();

        Theme.Changed += OnThemeChanged;
        Restore();
    }

    /// <summary>Double-click, or "Open" in the menu: bring the main window up.</summary>
    public event Action? OpenRequested;

    /// <summary>"Hide" in the menu: the host turns the setting off.</summary>
    public event Action? HideRequested;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            // A tool window: no taskbar button and no entry in Alt+Tab, like a gadget rather than an app.
            const int WsExToolWindow = 0x80;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExToolWindow;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Dwm.ApplyWindowStyling(this, mica: false);
        Dwm.ApplyTitleBarTheme(this, Theme.IsDark);
        Subscribe();
        LayoutChart();
        Refresh(_services.Pipeline.LatestSnapshot);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            Subscribe();
        }
        else
        {
            Unsubscribe();
        }
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        _services.Pipeline.CycleCompleted += OnCycleCompleted;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        _services.Pipeline.CycleCompleted -= OnCycleCompleted;
    }

    private void OnCycleCompleted(StepSnapshot snapshot) => _ui.Post(_ => Refresh(snapshot), null);

    private void Refresh(StepSnapshot? snapshot)
    {
        if (IsDisposed)
        {
            return;
        }

        _snapshot = snapshot;
        string? series = SeriesNames.For(StepId.PublicIp);
        if (series is not null)
        {
            _chart.SetSeries(_services.History.Get(series, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15)), "ms", false, _services.Targets.LatencyWarningMs, _services.Targets.LatencyErrorMs);
            _chart.SetRange(TimeSpan.FromMinutes(15));
        }

        Invalidate();
    }

    private void LayoutChart()
    {
        int left = Sp(3);
        _chart.Bounds = new Rectangle(left, Dp(66), ClientSize.Width - (left * 2), ClientSize.Height - Dp(66) - Sp(3));
    }

    /// <summary>Puts the widget where it was last left, kept on a screen that still exists.</summary>
    private void Restore()
    {
        var wanted = new Point(_services.Settings.WidgetX, _services.Settings.WidgetY);
        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        bool onAScreen = Screen.AllScreens.Any(s => s.WorkingArea.Contains(wanted));
        Location = onAScreen ? wanted : new Point(area.Right - Width - Dp(16), area.Top + Dp(16));
    }

    private void OnDragStart(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragStart = e.Location;
        }
    }

    private void OnDrag(object? sender, MouseEventArgs e)
    {
        if (_dragStart is { } start)
        {
            Location = new Point(Location.X + e.X - start.X, Location.Y + e.Y - start.Y);
        }
    }

    private void OnDragEnd(object? sender, MouseEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        _dragStart = null;
        _services.Settings.WidgetX = Location.X;
        _services.Settings.WidgetY = Location.Y;
        _services.Settings.Save();
    }

    private void OnThemeChanged()
    {
        if (!IsDisposed)
        {
            BackColor = Theme.Current.Bg;
            Dwm.ApplyTitleBarTheme(this, Theme.IsDark);
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Theme.RadiusCard(DeviceDpi);
        g.FillRounded(bounds, radius, p.Surface);
        g.DrawRounded(bounds, radius, p.Border);

        int left = Sp(3);
        int top = Sp(3);
        int dot = Dp(10);
        int gap = Dp(6);

        // Seven dots, one per step, in the step's colour and shape - the Status page's list at a glance.
        StepId[] order = [StepId.Adapter, StepId.Link, StepId.IpAddress, StepId.Gateway, StepId.PublicIp, StepId.Dns, StepId.Target];
        int x = left;
        foreach (StepId step in order)
        {
            StepState state = _snapshot?.StateOf(step) ?? StepState.Pending;
            Severity severity = state switch
            {
                StepState.Ok => Severity.Ok,
                StepState.Warning => Severity.Warning,
                StepState.Failed => Severity.Error,
                _ => Severity.Idle,
            };
            g.FillRounded(new RectangleF(x, top + Dp(3), dot, dot), dot / 2f, SeverityColors.Strong(severity));
            x += dot + gap;
        }

        string headline;
        Color headlineColor;
        if (_snapshot is null)
        {
            headline = loc.Get("hero.checking.title");
            headlineColor = p.TextMuted;
        }
        else if (_snapshot.FirstFailure is { } failure)
        {
            headline = failure.Headline;
            headlineColor = p.Error;
        }
        else if (_snapshot.AnyWarning)
        {
            headline = loc.Get("hero.degraded.title");
            headlineColor = p.Warn;
        }
        else
        {
            headline = loc.Get("hero.ok.title");
            headlineColor = p.Ok;
        }

        double? latency = _snapshot?[StepId.PublicIp]?.ChartValue ?? _snapshot?[StepId.Target]?.ChartValue;
        string latencyText = latency is { } ms ? $"{ms:F0} ms" : string.Empty;
        Size latencySize = GdiExtensions.MeasureCrisp(latencyText, Theme.Font(FontRole.Small));
        g.DrawTextCrisp(latencyText, Theme.Font(FontRole.Small), p.TextMuted, new Rectangle(Width - left - latencySize.Width, top, latencySize.Width, Dp(16)));

        g.DrawTextCrisp(
            headline,
            Theme.Font(FontRole.BodyStrong),
            headlineColor,
            new Rectangle(left, top + Dp(20), Width - (left * 2), Dp(20)));

        string time = _snapshot is { } s ? s.TakenUtc.ToLocalTime().ToString("HH:mm:ss") : string.Empty;
        g.DrawTextCrisp(
            time.Length > 0 ? loc.Format("widget.lastCheck", time) : string.Empty,
            Theme.Font(FontRole.Small),
            p.TextMuted,
            new Rectangle(left, top + Dp(42), Width - (left * 2), Dp(16)));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutChart();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The widget is hidden, never closed by the user: closing it means the app is going down.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideRequested?.Invoke();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Unsubscribe();
            Theme.Changed -= OnThemeChanged;
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Dp(int px) => Theme.Dp(px, DeviceDpi);

    private int Sp(int step) => Theme.Space(step, DeviceDpi);
}
