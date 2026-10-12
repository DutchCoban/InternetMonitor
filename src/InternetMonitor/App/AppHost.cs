using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Shell;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.App;

/// <summary>
/// The composition root. It constructs everything, wires the tray's intents to the window, and
/// owns the shutdown order - and holds no logic of its own beyond that.
///
/// It is an <see cref="ApplicationContext"/> rather than a main form because this is a tray app:
/// the message loop must outlive the window, which the user can close and reopen any number of
/// times while monitoring continues. The window is created up front but only shown on request, so
/// the first open is instant rather than paying for layout at that moment.
/// </summary>
public sealed class AppHost : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly ThemeWatcher _themeWatcher;
    private readonly TrayController _tray;
    private readonly MainWindow _window;

    public AppHost(bool showWindowOnStart = false)
    {
        _settings = AppSettings.Load();

        LocalizationManager.Initialize(_settings.Language);

        // Both lines matter, in this order. Setting Mode only re-resolves the palette when the
        // value actually changes, so with the default (System) it would do nothing at all and the
        // first window would paint light on a dark desktop; the explicit Reevaluate is what reads
        // the Windows setting for the first time. Both happen before any control exists, so the
        // very first paint is already correct rather than flashing the wrong theme.
        Theme.Mode = _settings.Theme;
        Theme.Reevaluate();

        _window = new MainWindow(_settings);

        // After the window: the watcher marshals theme changes onto the UI thread through it.
        _themeWatcher = new ThemeWatcher(_window);
        _tray = new TrayController();
        _tray.OpenRequested += ShowWindow;
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += Shutdown;

        if (showWindowOnStart)
        {
            ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_window.IsDisposed)
        {
            return;
        }

        if (!_window.Visible)
        {
            _window.Show();
        }

        if (_window.WindowState == FormWindowState.Minimized)
        {
            _window.WindowState = _settings.WindowMaximized ? FormWindowState.Maximized : FormWindowState.Normal;
        }

        _window.Activate();
        _window.BringToFront();
    }

    private void ShowSettings()
    {
        ShowWindow();
        _window.ShowPage("settings");
    }

    private void Shutdown()
    {
        _settings.Save();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Tray first: it is the only thing the user can still interact with once teardown has
            // started, and a visible tray icon backed by a half-disposed app is worse than none.
            _tray.Dispose();
            _themeWatcher.Dispose();

            if (!_window.IsDisposed)
            {
                _window.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
