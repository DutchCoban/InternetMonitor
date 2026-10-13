using System.Reflection;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Tools.SpeedTest;
using InternetMonitor.Core.Updates;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Shell;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

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
    private readonly KumaPusher _kuma;
    private readonly DiagnosticLogger _diagnosticLog;
    private readonly NetworkHealthMonitor _networkHealth;
    private readonly ChangeDetector _changes;
    private readonly IncidentTracker _tracker;
    private readonly TrayController _tray;
    private readonly MainWindow _window;
    private readonly SynchronizationContext _ui;
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateInstaller _updateInstaller;
    private System.Windows.Forms.Timer? _updateRestartTimer;
    private readonly SpeedTestScheduler _speedTestScheduler;

    public AppHost(bool showWindowOnStart = false, string? initialPage = null, string? initialTrace = null)
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

        // Built fresh from current settings on every call rather than captured once: the
        // pipeline already calls its targets provider at the start of every single cycle (see
        // StepPipeline.RunCycleCoreAsync), so reading live settings here is all a step-7 endpoint
        // added through Settings needs to take effect on the very next cycle - no extra event
        // wiring required.
        var defaultTargets = new CheckTargets();
        CheckTargets BuildTargets()
        {
            List<EndpointConfig> endpoints = _settings.ApplicationEndpoints;
            if (endpoints.Count == 0)
            {
                return defaultTargets;
            }

            return defaultTargets with
            {
                Targets =
                [
                    .. defaultTargets.Targets,
                    .. endpoints.Where(e => e.Enabled).Select(e => e.Kind switch
                    {
                        EndpointKind.TcpPort => new TargetEndpoint(e.Name, Url: "", TargetKind.TcpPort, Host: e.Host, Port: e.Port),
                        _ => new TargetEndpoint(e.Name, e.Url, TargetKind.Url, ExpectedStatusCode: e.ExpectedStatusCode, Headers: e.Headers),
                    }),
                ],
            };
        }

        _pipeline = new StepPipeline(BuildTargets);
        _cadence = new CadenceController(_pipeline, () => _settings);

        _history = new MetricStore
        {
            // History is sampled at the background rate regardless of how fast the pipeline is
            // actually running, so retention and chart density do not change depending on whether
            // a window happens to be open.
            MinimumSampleInterval = TimeSpan.FromSeconds(_settings.BackgroundIntervalSeconds),
        };

        _incidents = new IncidentStore();

        _kuma = new KumaPusher();
        _kuma.Reconfigure(_settings.KumaPushUrl, _settings.KumaIntervalSeconds);
        _diagnosticLog = new DiagnosticLogger(_settings);
        _changes = new ChangeDetector();
        _tracker = new IncidentTracker(_incidents, _changes);

        _networkHealth = new NetworkHealthMonitor(
            _incidents,
            defaultTargets.PublicIpTargets,
            () => _pipeline.LatestSnapshot?.FactsFor<IpAddressFacts>(StepId.IpAddress)?.Gateway);
        _networkHealth.Reconfigure(
            _settings.NetworkHealthMonitoringEnabled,
            _settings.TcpRetransmissions,
            _settings.PacketLoss,
            _settings.TtlExceeded,
            _settings.RogueDhcp);

        _updateChecker = new UpdateChecker(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(2, 0, 0));
        _updateChecker.CheckCompleted += OnUpdateCheckCompleted;
        _updateChecker.Reconfigure(_settings.CheckForUpdates);
        _updateInstaller = new UpdateInstaller();

        _speedTestScheduler = new SpeedTestScheduler();
        _speedTestScheduler.RunCompleted += OnSpeedTestRunCompleted;
        _speedTestScheduler.Reconfigure(_settings.SpeedTestAutoRunEnabled, _settings.SpeedTestIntervalHours);

        var services = new AppServices(_settings, defaultTargets, _pipeline, _cadence, _history, _incidents, _kuma, _diagnosticLog, _networkHealth, _updateChecker, _speedTestScheduler);
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
        _tray.CheckForUpdatesRequested += OnCheckForUpdatesRequested;
        _tray.ExitRequested += Shutdown;

        _pipeline.CycleCompleted += OnCycleCompleted;
        _pipeline.StepChanged += _tracker.NoteStepChange;
        _incidents.Changed += OnIncidentsChanged;
        _tracker.IncidentOpened += OnIncidentOpened;
        _tracker.IncidentClosed += _diagnosticLog.LogIncidentResolved;
        _networkHealth.IncidentOpened += OnIncidentOpened;
        _networkHealth.IncidentClosed += _diagnosticLog.LogIncidentResolved;
        _cadence.Start();

        if (showWindowOnStart)
        {
            ShowWindow();
        }

        if (initialPage is not null)
        {
            _window.ShowPage(initialPage);
        }

        if (initialTrace is not null)
        {
            // Fire-and-forget by design: StartInitialTraceAsync is the whole of what has to
            // happen before the trace starts, and nothing here needs its result.
            _ = StartInitialTraceAsync(initialTrace);
        }
    }

    /// <summary>
    /// Starts a command-line-requested trace once the pipeline has had a chance to complete its
    /// first cycle, capped at a short timeout so a slow first cycle never blocks the trace from
    /// starting at all.
    ///
    /// Without this, --trace used right at startup fires before <see cref="StepPipeline.LatestSnapshot"/>
    /// exists, so the traceroute has no known local subnet to compare hops against and
    /// HopAnnotator correctly falls back to "unknown" for every private-address hop - accurate,
    /// but worse than the precise "your own network" / "your provider's network" distinction that
    /// is available a few hundred milliseconds later.
    /// </summary>
    private async Task StartInitialTraceAsync(string target)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            while (_pipeline.LatestSnapshot is null)
            {
                await Task.Delay(50, timeout.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out waiting - start the trace anyway, just without the local-subnet context.
        }

        if (!_window.IsDisposed)
        {
            _window.StartTraceroute(target);
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
        _diagnosticLog.LogCycle(snapshot, diagnosis, _tracker.ActiveIncident?.Id);

        UpdateTray(snapshot, diagnosis);
    }

    /// <summary>
    /// The one place an incident opening fans out to everything that cares: the diagnostics log
    /// (always, when logging is on) and a brief on-screen pop-up (only when the user has not turned
    /// that off in Settings). Marshalled to the UI thread because IncidentTracker fires this from
    /// the pipeline's background thread, and IncidentToast is a Form.
    /// </summary>
    private void OnIncidentOpened(Incident incident)
    {
        _diagnosticLog.LogIncidentOpened(incident);

        _ui.Post(
            _ =>
            {
                if (_settings.ShowIncidentPopups)
                {
                    IncidentToast.ShowFor(incident);
                }
            },
            null);
    }

    /// <summary>
    /// The background check's result - fires from <see cref="UpdateChecker"/>'s own loop, not the
    /// UI thread, so this always marshals through <see cref="_ui"/>, same as <see cref="OnIncidentOpened"/>.
    /// Only ever acts when a newer version exists; a background check that finds nothing stays
    /// silent, same as v1 - only the manual "Check for updates" (<see cref="OnCheckForUpdatesRequested"/>)
    /// ever reports "up to date" or "check failed".
    /// </summary>
    private void OnUpdateCheckCompleted(object? sender, UpdateCheckResult result)
    {
        if (!result.IsUpdateAvailable)
        {
            return;
        }

        _ui.Post(_ => _ = HandleUpdateAvailableAsync(result), null);
    }

    /// <summary>
    /// Either shows the "a newer version exists" toast (auto-update off, or nothing to download),
    /// or downloads it and shows the "installing, restarting shortly" toast before shutting down -
    /// never silently: a failed download still falls back to the plain notify toast, so the user
    /// is never left with no feedback at all.
    /// </summary>
    private async Task HandleUpdateAvailableAsync(UpdateCheckResult result)
    {
        if (_settings.AutoUpdateInstall && result.AssetDownloadUrl is { } assetUrl)
        {
            string currentExePath = Environment.ProcessPath ?? Application.ExecutablePath;
            bool applied = await _updateInstaller.DownloadAndApplyAsync(assetUrl, currentExePath, CancellationToken.None).ConfigureAwait(true);
            if (applied)
            {
                ShowInstallingToastAndRestart(result);
                return;
            }
        }

        UpdateToast.Show(
            UpdateToastKind.Available,
            LocalizationManager.Instance.Get("update.toast.available.title"),
            LocalizationManager.Instance.Format("update.toast.available.text", result.LatestVersion ?? "?"),
            result.ReleaseUrl);
    }

    private const int UpdateRestartDelaySeconds = 15;

    private void ShowInstallingToastAndRestart(UpdateCheckResult result)
    {
        UpdateToast.Show(
            UpdateToastKind.Installing,
            LocalizationManager.Instance.Get("update.toast.installing.title"),
            LocalizationManager.Instance.Format("update.toast.installing.text", result.LatestVersion ?? "?", UpdateRestartDelaySeconds),
            result.ReleaseUrl);

        _updateRestartTimer?.Stop();
        _updateRestartTimer?.Dispose();
        _updateRestartTimer = new System.Windows.Forms.Timer { Interval = UpdateRestartDelaySeconds * 1000 };
        _updateRestartTimer.Tick += (_, _) => Shutdown();
        _updateRestartTimer.Start();
    }

    /// <summary>
    /// The tray menu's "Check for updates" - unlike the background check, this always gives
    /// feedback (found, not found, or couldn't check), since a user who just clicked "check"
    /// deserves an answer even when there's nothing new to report. Never installs by itself, even
    /// with auto-update on - that stays a purely background-triggered action.
    /// </summary>
    private async void OnCheckForUpdatesRequested()
    {
        UpdateCheckResult? result = await _updateChecker.CheckOnceAsync(CancellationToken.None).ConfigureAwait(true);
        LocalizationManager loc = LocalizationManager.Instance;

        if (result is null)
        {
            UpdateToast.Show(UpdateToastKind.CheckFailed, loc.Get("update.toast.checkFailed.title"), loc.Get("update.toast.checkFailed.text"));
            return;
        }

        if (!result.IsUpdateAvailable)
        {
            UpdateToast.Show(UpdateToastKind.UpToDate, loc.Get("update.toast.upToDate.title"), loc.Get("update.toast.upToDate.text"));
            return;
        }

        UpdateToast.Show(
            UpdateToastKind.Available,
            loc.Get("update.toast.available.title"),
            loc.Format("update.toast.available.text", result.LatestVersion ?? "?"),
            result.ReleaseUrl);
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

    /// <summary>
    /// An automatic Speed Test run finished - record it the same two series a manual run (see
    /// <c>ToolsPage</c>) records into, so History and the data exports don't need to know or care
    /// which path a given sample came from. Runs on <see cref="SpeedTestScheduler"/>'s own
    /// background loop, not the UI thread - fine, same as <see cref="RecordHistory"/> above,
    /// since <see cref="MetricStore.Sample"/> is already safe to call off the UI thread.
    /// </summary>
    private void OnSpeedTestRunCompleted(object? sender, SpeedTestResult result)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _history.Sample("speedtest-download", now, result.DownloadMbps);
        _history.Sample("speedtest-upload", now, result.UploadMbps);
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
            _tracker.IncidentOpened -= OnIncidentOpened;
            _tracker.IncidentClosed -= _diagnosticLog.LogIncidentResolved;
            _networkHealth.IncidentOpened -= OnIncidentOpened;
            _networkHealth.IncidentClosed -= _diagnosticLog.LogIncidentResolved;
            _pipeline.Dispose();

            // Blocking on shutdown is acceptable and necessary: this is the final flush, and
            // letting the process exit without it loses up to two minutes of recorded history.
            _history.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _kuma.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _networkHealth.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _updateChecker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _speedTestScheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();

            _updateRestartTimer?.Stop();
            _updateRestartTimer?.Dispose();

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
