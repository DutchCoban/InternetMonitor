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
    private readonly NotifyIcon _notifyIcon;
    private ManagedIcon? _currentIcon;
    private Severity _severity = Severity.Idle;
    private bool _hasSeverity;

    public TrayController()
    {
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.MouseClick += OnTrayMouseClick;

        ApplyIcon(Severity.Idle);
        ApplyTooltip();

        LocalizationManager.Instance.LanguageChanged += OnLanguageChanged;
    }

    public event Action? OpenRequested;

    public event Action? SettingsRequested;

    public event Action? ExitRequested;

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
        ApplyIcon(severity);
        ApplyTooltip(statusText);
    }

    private void ApplyIcon(Severity severity)
    {
        ManagedIcon next = TrayIconFactory.CreateTrayIcon(severity);
        _notifyIcon.Icon = next.Icon;
        _currentIcon?.Dispose();
        _currentIcon = next;
    }

    private void ApplyTooltip(string? statusText = null)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        string text = statusText is null
            ? loc.Get("tray.tooltip.checking")
            : loc.Format("tray.tooltip.state", statusText);

        // Windows silently truncates a tray tooltip past 127 characters; trimming here keeps a
        // long localized status from simply vanishing.
        _notifyIcon.Text = text.Length <= 127 ? text : text[..127];
    }

    private ContextMenuStrip BuildMenu()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var menu = new ContextMenuStrip();
        menu.Items.Add(loc.Get("menu.open"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(loc.Get("menu.settings"), null, (_, _) => SettingsRequested?.Invoke());
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
