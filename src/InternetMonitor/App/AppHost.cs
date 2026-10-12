using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
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
    private readonly MetricStore _history;
    private readonly IncidentStore _incidents;
    private readonly ChangeDetector _changes;
    private readonly IncidentTracker _tracker;
    private readonly TrayController _tray;
    private readonly MainWindow _window;
    private readonly SynchronizationContext _ui;

    public AppHost(bool showWindowOnStart = false, string? initialPage = null)
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

        _history = new MetricStore
        {
            // History is sampled at the background rate regardless of how fast the pipeline is
            // actually running, so retention and chart density do not change depending on whether
            // a window happens to be open.
            MinimumSampleInterval = TimeSpan.FromSeconds(_settings.BackgroundIntervalSeconds),
        };

        _incidents = new IncidentStore();
        _changes = new ChangeDetector();
        _tracker = new IncidentTracker(_incidents, _changes);

        var services = new AppServices(_settings, targets, _pipeline, _cadence, _history, _incidents);
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
        _pipeline.StepChanged += _tracker.NoteStepChange;
        _incidents.Changed += OnIncidentsChanged;
        _cadence.Start();

        if (showWindowOnStart)
        {
            ShowWindow();
        }

        if (initialPage is not null)
        {
            _window.ShowPage(initialPage);
        }
    }

    /// <summary>
    /// Keeps the tray badge in step with the pipeline. The tray is the only part of the app that
    /// is always visible, so it reflects the real state whether or not a window is open.
    /// </summary>
    private void OnCycleCompleted(StepSnapshot snapshot)
    {
        RecordHistory(snapshot);

        RootCauseResult diagnosis = RootCauseAnalyzer.Analyze(snapshot);
        _tracker.Observe(snapshot, diagnosis);

        UpdateTray(snapshot, diagnosis);
    }

    /// <summary>
    /// Writes each step's one chartable number into its series. Runs on the pipeline's thread
    /// rather than the UI's: it is pure bookkeeping, and the UI thread has no business waiting on
    /// a lock that a flush might be holding.
    /// </summary>
    private void RecordHistory(StepSnapshot snapshot)
    {
        foreach (StepResult result in snapshot.All)
        {
            if (result.ChartValue is not { } value || SeriesNames.For(result.Step) is not { } series)
            {
                continue;
            }

            // Skipped steps measured nothing. Recording a zero for them would draw a cliff on the
            // chart that looks like a measurement rather than an absence.
            if (result.State == StepState.Skipped)
            {
                continue;
            }

            _history.Sample(series, result.TimestampUtc, value);
        }
    }

    private void UpdateTray(StepSnapshot snapshot, RootCauseResult diagnosis) => _ui.Post(
        _ =>
        {
            Ui.Theming.Severity severity = diagnosis.Severity switch
            {
                Core.Diagnosis.Severity.Error => Ui.Theming.Severity.Error,
                Core.Diagnosis.Severity.Degraded => Ui.Theming.Severity.Warning,
                _ => Ui.Theming.Severity.Ok,
            };

            string text = snapshot.FirstFailure?.Headline
                ?? snapshot.All.FirstOrDefault(r => r.State == StepState.Warning)?.Headline
                ?? LocalizationManager.Instance.Get("hero.ok.title");

            _tray.SetStatus(severity, text);
        },
        null);

    /// <summary>Keeps the nav rail's incident badge in step with the store.</summary>
    private void OnIncidentsChanged() => _ui.Post(
        _ =>
        {
            if (!_window.IsDisposed)
            {
                _window.SetIncidentBadge(_incidents.ActiveCount);
            }
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
            _pipeline.StepChanged -= _tracker.NoteStepChange;
            _incidents.Changed -= OnIncidentsChanged;
            _pipeline.Dispose();

            // Blocking on shutdown is acceptable and necessary: this is the final flush, and
            // letting the process exit without it loses up to two minutes of recorded history.
            _history.DisposeAsync().AsTask().GetAwaiter().GetResult();

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
