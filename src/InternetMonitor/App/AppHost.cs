using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
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
    private readonly StepPipeline _pipeline;
    private readonly CadenceController _cadence;
    private readonly TrayController _tray;
    private readonly MainWindow _window;
    private readonly SynchronizationContext _ui;

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

        var targets = new CheckTargets();
        _pipeline = new StepPipeline(() => targets);
        _cadence = new CadenceController(_pipeline, () => _settings);

        var services = new AppServices(_settings, _pipeline, _cadence);
        _window = new MainWindow(services);

        // Creating the handle now rather than on first show means the pipeline has somewhere to
        // marshal to from its very first cycle, which starts below before any window is visible.
        _ = _window.Handle;
        _ui = SynchronizationContext.Current
            ?? throw new InvalidOperationException("The WinForms synchronization context was not installed.");

        _themeWatcher = new ThemeWatcher(_window);

        _tray = new TrayController();
        _tray.OpenRequested += ShowWindow;
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += Shutdown;

        _pipeline.CycleCompleted += OnCycleCompleted;
        _cadence.Start();

        if (showWindowOnStart)
        {
            ShowWindow();
        }
    }

    /// <summary>
    /// Keeps the tray badge in step with the pipeline. The tray is the only part of the app that
    /// is always visible, so it reflects the real state whether or not a window is open.
    /// </summary>
    private void OnCycleCompleted(StepSnapshot snapshot) => _ui.Post(
        _ =>
        {
            StepResult? failure = snapshot.FirstFailure;
            Severity severity = failure is not null ? Severity.Error
                : snapshot.AnyWarning ? Severity.Warning
                : Severity.Ok;

            string text = failure?.Headline
                ?? snapshot.All.FirstOrDefault(r => r.State == StepState.Warning)?.Headline
                ?? LocalizationManager.Instance.Get("hero.ok.title");

            _tray.SetStatus(severity, text);
        },
        null);

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
            // Order matters on the way out. Stop producing work first (cadence, then pipeline),
            // then take down what consumes it, and only then the tray - a visible tray icon backed
            // by a half-disposed app is worse than none.
            _cadence.Dispose();
            _pipeline.CycleCompleted -= OnCycleCompleted;
            _pipeline.Dispose();

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
