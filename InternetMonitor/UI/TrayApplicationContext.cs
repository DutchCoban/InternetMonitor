using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Logging;
using InternetMonitor.Network;
using InternetMonitor.Network.Diagnosis;
using InternetMonitor.Network.Diagnostics;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.UI;

/// <summary>
/// Composition root: owns the connectivity monitor, the diagnostics coordinator, tray icon,
/// context menu, and popups, and marshals events (raised on thread-pool threads) onto the UI
/// thread. NotifyIcon's own events already arrive on the UI thread (it uses a hidden
/// message-only window), so those do not need marshaling - only monitor/coordinator events do.
/// </summary>
public sealed class TrayApplicationContext : System.Windows.Forms.ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly ConnectivityMonitor _monitor = new();
    private readonly OutagePopupController _popupController = new();
    private readonly SimpleFileLogger _logger = new();
    private readonly UptimeKumaPusher _kumaPusher;
    private readonly IncidentStore _incidentStore = new();
    private readonly IncidentTracker _incidentTracker;
    private readonly DiagnosticLogger _diagnosticLogger;
    private readonly DiagnosticsCoordinator _diagnosticsCoordinator;
    private readonly NotifyIcon _trayIcon;
    private readonly SynchronizationContext _uiContext;
    private ManagedIcon? _currentIcon;
    private DateTimeOffset? _currentOutageStartedUtc;
    private StatusPopupForm? _statusPopupForm;
    private DiagnosticsForm? _diagnosticsForm;
    private SettingsForm? _settingsForm;

    public TrayApplicationContext()
    {
        // Constructed before Application.Run starts the message loop, so the WinForms sync
        // context isn't installed automatically yet - install it explicitly.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        }

        _uiContext = SynchronizationContext.Current!;

        _settings = AppSettings.Load();
        LocalizationManager.Initialize(_settings.Language);

        _kumaPusher = new UptimeKumaPusher(_logger);
        _kumaPusher.Reconfigure(_settings.KumaPushUrl, _settings.KumaIntervalSeconds);

        _incidentTracker = new IncidentTracker(_incidentStore);
        _diagnosticLogger = new DiagnosticLogger(_settings);
        _diagnosticsCoordinator = new DiagnosticsCoordinator(_incidentTracker, _diagnosticLogger);
        _diagnosticsCoordinator.UpdateEndpoints(_settings.ApplicationEndpoints);
        _diagnosticsCoordinator.UpdatePingTarget(_settings.PingTargetAddress);
        _diagnosticsCoordinator.UpdateLatencyThresholds(_settings.LatencyWarningThresholdMs, _settings.LatencyErrorThresholdMs);
        _diagnosticsCoordinator.UpdateHistoryRetention(TimeSpan.FromMinutes(_settings.HistoryRetentionMinutes));
        _diagnosticsCoordinator.Start();

        _currentIcon = TrayIconFactory.Build(ProbeStatus.Unknown);
        _trayIcon = new NotifyIcon
        {
            Icon = _currentIcon.Icon,
            Text = LocalizationManager.Instance.Get("tray.tooltip.checking"),
            Visible = true,
            ContextMenuStrip = BuildContextMenu(),
        };
        _trayIcon.MouseClick += OnTrayIconMouseClick;

        _popupController.RecoveredNotificationRequested += (_, _) => ShowRecoveredBalloon();

        LocalizationManager.Instance.LanguageChanged += (_, _) => RefreshLocalizedSurfaces();

        _monitor.StateChanged += OnMonitorStateChanged;
        _diagnosticsCoordinator.DiagnosisUpdated += OnDiagnosisUpdated;
        _monitor.Start();
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(LocalizationManager.Instance.Get("menu.status"), null, (_, _) => ShowStatusPopup());
        menu.Items.Add(LocalizationManager.Instance.Get("menu.diagnostics"), null, (_, _) => ShowDiagnostics());
        menu.Items.Add(LocalizationManager.Instance.Get("menu.settings"), null, (_, _) => ShowSettings());
        menu.Items.Add(LocalizationManager.Instance.Get("menu.about"), null, (_, _) => ShowAbout());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(LocalizationManager.Instance.Get("menu.exit"), null, OnExitClicked);
        return menu;
    }

    private void RefreshLocalizedSurfaces()
    {
        if (_trayIcon.ContextMenuStrip is { } oldMenu)
        {
            oldMenu.Dispose();
        }

        _trayIcon.ContextMenuStrip = BuildContextMenu();
        _trayIcon.Text = BuildTooltipText(_diagnosticsCoordinator.LatestDiagnosis);
    }

    private void OnTrayIconMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowStatusPopup();
        }
    }

    private void ShowStatusPopup()
    {
        if (_statusPopupForm is { IsDisposed: false })
        {
            _statusPopupForm.Activate();
            return;
        }

        _statusPopupForm = new StatusPopupForm(_diagnosticsCoordinator, _incidentTracker, _settings);
        _statusPopupForm.FormClosed += (_, _) => _statusPopupForm = null;
        ScreenPositioning.PlaceNearTray(_statusPopupForm);
        _statusPopupForm.Show();
    }

    private void ShowDiagnostics()
    {
        if (_diagnosticsForm is { IsDisposed: false })
        {
            _diagnosticsForm.Activate();
            return;
        }

        _diagnosticsForm = new DiagnosticsForm(_diagnosticsCoordinator, _incidentStore, _diagnosticLogger, _settings);
        _diagnosticsForm.FormClosed += (_, _) => _diagnosticsForm = null;
        _diagnosticsForm.Show();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings);
        _settingsForm.KumaSettingsChanged += (_, _) =>
            _kumaPusher.Reconfigure(_settings.KumaPushUrl, _settings.KumaIntervalSeconds);
        _settingsForm.EndpointsChanged += (_, _) =>
            _diagnosticsCoordinator.UpdateEndpoints(_settings.ApplicationEndpoints);
        _settingsForm.PingTargetChanged += (_, _) =>
            _diagnosticsCoordinator.UpdatePingTarget(_settings.PingTargetAddress);
        _settingsForm.LatencyThresholdsChanged += (_, _) =>
            _diagnosticsCoordinator.UpdateLatencyThresholds(_settings.LatencyWarningThresholdMs, _settings.LatencyErrorThresholdMs);
        _settingsForm.HistoryRetentionChanged += (_, _) =>
            _diagnosticsCoordinator.UpdateHistoryRetention(TimeSpan.FromMinutes(_settings.HistoryRetentionMinutes));
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    private void ShowAbout()
    {
        using var form = new AboutForm();
        form.ShowDialog();
    }

    private void OnMonitorStateChanged(object? sender, ConnectivityStateChangedEventArgs e)
    {
        _uiContext.Post(_ => HandleStateChangedOnUiThread(e), null);
    }

    private void HandleStateChangedOnUiThread(ConnectivityStateChangedEventArgs e)
    {
        if (e.NewState == ConnectivityState.Outage && e.OldState != ConnectivityState.Outage)
        {
            _currentOutageStartedUtc = e.OutageStartedUtc;
            _logger.Log("OUTAGE_START");
        }
        else if (e.NewState == ConnectivityState.Connected && _currentOutageStartedUtc is { } startedUtc)
        {
            TimeSpan duration = DateTimeOffset.UtcNow - startedUtc;
            _logger.Log($"OUTAGE_END duration={duration:hh\\:mm\\:ss}");
            _currentOutageStartedUtc = null;
        }

        if (_settings.ShowOutagePopups)
        {
            _popupController.OnStateChanged(e.OldState, e.NewState);
        }

        // ConnectivityMonitor polls every second and can notice a change well before
        // DiagnosticsCoordinator's own 15-second cycle would - without this, the outage popup
        // (driven by this same state change) could appear while the Status window, if already
        // open, still shows an up-to-15-seconds-stale "everything fine" snapshot. Triggering an
        // immediate diagnostics cycle here keeps the detailed view in step with the fast one.
        _ = RunDiagnosticsCycleSafelyAsync();
    }

    private async Task RunDiagnosticsCycleSafelyAsync()
    {
        try
        {
            await _diagnosticsCoordinator.RunNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"RunNowAsync failed: {ex}");
        }
    }

    private void OnDiagnosisUpdated(object? sender, DiagnosisResult diagnosis)
    {
        _uiContext.Post(_ => ApplyDiagnosisToIcon(diagnosis), null);
    }

    private void ApplyDiagnosisToIcon(DiagnosisResult diagnosis)
    {
        ManagedIcon newIcon = TrayIconFactory.Build(diagnosis.Severity);
        _trayIcon.Icon = newIcon.Icon;
        _currentIcon?.Dispose();
        _currentIcon = newIcon;
        _trayIcon.Text = Truncate(BuildTooltipText(diagnosis), 63);
    }

    private static string BuildTooltipText(DiagnosisResult? diagnosis) => diagnosis switch
    {
        null => LocalizationManager.Instance.Get("tray.tooltip.checking"),
        { Severity: ProbeStatus.Ok } => LocalizationManager.Instance.Get("tray.tooltip.connected"),
        _ => diagnosis.Headline,
    };

    private void ShowRecoveredBalloon()
    {
        _trayIcon.BalloonTipTitle = LocalizationManager.Instance.Get("outage.recoveredBalloon.title");
        _trayIcon.BalloonTipText = LocalizationManager.Instance.Get("outage.recoveredBalloon.text");
        _trayIcon.ShowBalloonTip(5000);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private async void OnExitClicked(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        await _monitor.DisposeAsync();
        await _diagnosticsCoordinator.DisposeAsync();
        await _kumaPusher.DisposeAsync();
        _popupController.Dispose();
        _currentIcon?.Dispose();
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        ExitThread();
    }
}
