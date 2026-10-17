using InternetMonitor.Localization;
using InternetMonitor.Ui;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.App;

/// <summary>
/// Owns the tray icon and its menu, and nothing else: it raises intent as events and lets
/// <see cref="AppHost"/> decide what to do. Keeping the tray dumb is what allows the same
/// "open the app" action to come from a left click, a menu item or a balloon click without three
/// copies of the logic.
///
/// The icon itself is rebuilt whenever the severity changes, because it carries a status badge.
/// The previous <see cref="ManagedIcon"/> is disposed only after the new one is assigned - the
/// other order briefly leaves the shell rendering a destroyed handle.
/// </summary>
public sealed class TrayController : IDisposable
{
    private static readonly (string Key, TimeSpan? Duration)[] PauseChoices =
    [
        ("menu.pause.30m", TimeSpan.FromMinutes(30)),
        ("menu.pause.1h", TimeSpan.FromHours(1)),
        ("menu.pause.4h", TimeSpan.FromHours(4)),
        ("menu.pause.indefinite", null),
    ];

    private readonly NotifyIcon _notifyIcon;
    private ManagedIcon? _currentIcon;
    private Severity _severity = Severity.Idle;
    private bool _hasSeverity;
    private ToolStripMenuItem _pauseMenu = null!;
    private ToolStripMenuItem _resumeItem = null!;
    private ToolStripMenuItem _widgetItem = null!;
    private bool _paused;
    private DateTimeOffset? _pausedUntil;
    private string? _lastStatusText;

    public TrayController()
    {
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.MouseClick += OnTrayMouseClick;
        _notifyIcon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();

        ApplyIcon(Severity.Idle);
        ApplyTooltip();

        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
    }

    public event Action? OpenRequested;

    public event Action? CheckNowRequested;

    public event Action? SettingsRequested;

    public event Action? CheckForUpdatesRequested;

    public event Action? ExitRequested;

    /// <summary>The user asked to pause monitoring: for a while, or (null) until they resume it.</summary>
    public event Action<TimeSpan?>? PauseRequested;

    public event Action? ResumeRequested;

    /// <summary>The widget's menu item was toggled; the argument is whether it should now be shown.</summary>
    public event Action<bool>? WidgetToggled;

    /// <summary>A Windows notification shown through <see cref="ShowBalloon"/> was clicked.</summary>
    public event Action? BalloonClicked;

    /// <summary>
    /// Updates the badge. <paramref name="statusText"/> is the already-localized one-line summary
    /// shown as the tooltip.
    /// </summary>
    public void SetStatus(Severity severity, string? statusText = null)
    {
        if (_hasSeverity && severity == _severity && statusText is null)
        {
            return;
        }

        _hasSeverity = true;
        _severity = severity;
        _lastStatusText = statusText ?? _lastStatusText;
        ApplyIcon(severity);
        ApplyTooltip();
    }

    /// <summary>Reflects a pause in the menu (Resume instead of Pause) and the tooltip.</summary>
    public void SetPaused(bool paused, DateTimeOffset? until)
    {
        _paused = paused;
        _pausedUntil = until;
        _pauseMenu.Visible = !paused;
        _resumeItem.Visible = paused;
        ApplyTooltip();
    }

    public void SetWidgetShown(bool shown) => _widgetItem.Checked = shown;

    /// <summary>
    /// A Windows notification from the tray icon - the alternative to the app's own pop-up. Goes
    /// through the notification centre, so it stays readable after it has gone, and it honours
    /// "do not disturb".
    /// </summary>
    public void ShowBalloon(string title, string text, ToolTipIcon icon) =>
        _notifyIcon.ShowBalloonTip(10_000, title, text, icon);

    private void ApplyIcon(Severity severity)
    {
        ManagedIcon next = TrayIconFactory.CreateTrayIcon(severity);
        _notifyIcon.Icon = next.Icon;
        _currentIcon?.Dispose();
        _currentIcon = next;
    }

    private void ApplyTooltip()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        string text = _paused
            ? _pausedUntil is { } until
                ? loc.Format("tray.tooltip.paused", until.ToLocalTime().ToString("HH:mm"))
                : loc.Get("tray.tooltip.pausedIndefinitely")
            : _lastStatusText is null
                ? loc.Get("tray.tooltip.checking")
                : loc.Format("tray.tooltip.state", _lastStatusText);

        // Windows silently truncates a tray tooltip past 127 characters; trimming here keeps a
        // long localized status from simply vanishing.
        _notifyIcon.Text = text.Length <= 127 ? text : text[..127];
    }

    private ContextMenuStrip BuildMenu()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var menu = new ContextMenuStrip();
        menu.Items.Add(loc.Get("menu.open"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(loc.Get("menu.checkNow"), null, (_, _) => CheckNowRequested?.Invoke());

        _pauseMenu = new ToolStripMenuItem(loc.Get("menu.pause")) { Visible = !_paused };
        foreach ((string key, TimeSpan? duration) in PauseChoices)
        {
            _pauseMenu.DropDownItems.Add(loc.Get(key), null, (_, _) => PauseRequested?.Invoke(duration));
        }

        menu.Items.Add(_pauseMenu);

        _resumeItem = new ToolStripMenuItem(loc.Get("menu.resume")) { Visible = _paused };
        _resumeItem.Click += (_, _) => ResumeRequested?.Invoke();
        menu.Items.Add(_resumeItem);

        _widgetItem = new ToolStripMenuItem(loc.Get("menu.widget")) { CheckOnClick = true, Checked = _widgetItem?.Checked ?? false };
        _widgetItem.CheckedChanged += (_, _) => WidgetToggled?.Invoke(_widgetItem.Checked);
        menu.Items.Add(_widgetItem);

        menu.Items.Add(loc.Get("menu.settings"), null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(loc.Get("menu.checkForUpdates"), null, (_, _) => CheckForUpdatesRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(loc.Get("menu.exit"), null, (_, _) => ExitRequested?.Invoke());
        return menu;
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            OpenRequested?.Invoke();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        ContextMenuStrip? old = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = BuildMenu();
        old?.Dispose();
        ApplyTooltip();
    }

    public void Dispose()
    {
        LocalizationManager.Instance.LanguageChanged -= OnLanguageChanged;
        _notifyIcon.MouseClick -= OnTrayMouseClick;

        // Hide before dispose: a NotifyIcon disposed while visible can leave a ghost icon in the
        // notification area until the user hovers over it.
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
