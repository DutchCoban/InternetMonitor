using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Shell.Pages;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell;

/// <summary>
/// The one window: a nav rail on the left and a page area on the right. This replaces v1's
/// thirteen separate forms, each of which had its own size, its own layout conventions and its own
/// idea of what a heading looked like.
///
/// Closing the window hides it instead of exiting. This is a tray application - monitoring has to
/// continue, and the X button is how people close windows they are done looking at, not how they
/// stop a monitor. Only the tray menu's Exit actually ends the process.
///
/// Pages are constructed lazily and kept alive after that (see <see cref="PageBase"/>), so
/// switching back to a page returns to it as it was.
/// </summary>
public sealed class MainWindow : Form
{
    /// <summary>
    /// Below this client width the rail collapses to icons. Chosen so the content area never drops
    /// under roughly 560 design pixels, which is where the step rows start wrapping badly.
    /// </summary>
    private const int RailCollapseWidthDp = 820;

    private readonly AppServices _services;
    private readonly AppSettings _settings;
    private readonly NavRail _rail;
    private readonly Panel _content;
    private readonly Dictionary<string, PageBase> _pages = [];
    private PageBase? _activePage;

    public MainWindow(AppServices services)
    {
        _services = services;
        _settings = services.Settings;

        Text = LocalizationManager.Instance.Get("app.name");
        Icon = TrayIconFactory.AppIcon.Value;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(Theme.Dp(620, DeviceDpi), Theme.Dp(520, DeviceDpi));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Current.Bg;
        DoubleBuffered = true;

        Size = new Size(
            _settings.WindowWidth > 0 ? _settings.WindowWidth : Theme.Dp(1000, DeviceDpi),
            _settings.WindowHeight > 0 ? _settings.WindowHeight : Theme.Dp(700, DeviceDpi));
        if (_settings.WindowMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.Bg };

        _rail = new NavRail { Dock = DockStyle.Left };
        _rail.Width = _rail.ExpandedWidth;
        _rail.AddItem(new NavRail.Item("status", Glyph.Status));
        _rail.AddItem(new NavRail.Item("diagnostics", Glyph.Diagnostics));
        _rail.AddItem(new NavRail.Item("history", Glyph.History));
        _rail.AddItem(new NavRail.Item("incidents", Glyph.Incidents));
        _rail.AddItem(new NavRail.Item("tools", Glyph.Tools));
        _rail.AddItem(new NavRail.Item("settings", Glyph.Settings));
        _rail.SelectionChanged += ShowPage;

        _pages["status"] = new StatusPage(services);
        _pages["diagnostics"] = new DiagnosticsPage(services);
        _pages["history"] = new HistoryPage(services);
        _pages["incidents"] = new IncidentsPage(services);
        _pages["tools"] = new ToolsPage(services);
        _pages["settings"] = new SettingsPage(services);

        foreach (PageBase page in _pages.Values)
        {
            page.Visible = false;
            _content.Controls.Add(page);
        }

        // Content first, then the rail: WinForms docks in reverse z-order, so adding the filled
        // panel first is what keeps the rail from being overlapped by it.
        Controls.Add(_content);
        Controls.Add(_rail);

        Theme.Changed += OnThemeChanged;
        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;

        ApplyLocalization();
        ShowPage("status");
    }

    /// <summary>Switches to a page by nav-rail id, building it if this is its first time on screen.</summary>
    public void ShowPage(string id)
    {
        if (!_pages.TryGetValue(id, out PageBase? page) || ReferenceEquals(page, _activePage))
        {
            return;
        }

        _activePage?.Deactivate();
        _activePage = page;
        page.Activate();
        page.BringToFront();

        if (_rail.SelectedId != id)
        {
            _rail.Select(id);
        }
    }

    /// <summary>Shows the number of open incidents on the nav rail, or clears it at zero.</summary>
    public void SetIncidentBadge(int count) => _rail.SetBadge("incidents", count);

    /// <summary>Opens the Tools page and traces a route, as the --trace command line asks for.</summary>
    public void StartTraceroute(string target)
    {
        ShowPage("tools");
        if (_pages["tools"] is ToolsPage tools)
        {
            tools.StartTrace(target);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // Setting MinimumSize/Size in the constructor raises OnResize before the field assignments
        // below it have run, so this fires with a null rail during construction. A guard is more
        // robust than relying on constructor ordering, which WinForms can disturb from several
        // directions (DPI changes, restoring window state).
        if (_rail is null)
        {
            return;
        }

        _rail.Collapsed = ClientSize.Width < Theme.Dp(RailCollapseWidthDp, DeviceDpi);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);

        // The cadence controller only needs to know whether anyone can see a result. Hiding the
        // window drops it back to the background interval without stopping monitoring.
        _services.Cadence.Foreground = Visible;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RememberGeometry();

        // The user closing the window means "put this away", not "stop monitoring". Anything else
        // - Windows shutting down, the tray menu's Exit calling Application.Exit - is a real close
        // and is allowed through.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private void RememberGeometry()
    {
        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.Save();
    }

    private void ApplyWindowChrome()
    {
        Dwm.ApplyTitleBarTheme(this, Theme.IsDark);
        Dwm.ApplyWindowStyling(this, mica: true);
    }

    private void OnThemeChanged()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        BackColor = Theme.Current.Bg;
        _content.BackColor = Theme.Current.Bg;

        // The title bar is per-window DWM state, not a live binding to the system setting, so it
        // has to be re-applied on every flip or it keeps the colour it was created with.
        ApplyWindowChrome();
        Invalidate(true);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed && !Disposing)
        {
            ApplyLocalization();
        }
    }

    private void ApplyLocalization()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        Text = loc.Get("app.name");
        _rail.BrandText = loc.Get("app.name");
        _rail.RefreshLabels(id => loc.Get($"nav.{id}"));

        foreach (PageBase page in _pages.Values)
        {
            page.ApplyLocalization();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
        }

        base.Dispose(disposing);
    }
}
