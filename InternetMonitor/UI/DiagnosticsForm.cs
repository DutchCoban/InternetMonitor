using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Network;
using InternetMonitor.Network.Diagnosis;
using InternetMonitor.Network.Diagnostics;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.UI;

/// <summary>
/// Professional diagnostics screen: every probe's raw measurements, the derived diagnosis,
/// diagnostic-logging status, and incident history - everything the simple status popup
/// deliberately leaves out. Opened via the tray context menu ("Diagnostiek").
/// </summary>
public sealed class DiagnosticsForm : Form
{
    private readonly DiagnosticsCoordinator _coordinator;
    private readonly IncidentStore _incidentStore;
    private readonly DiagnosticLogger _diagnosticLogger;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;

    private readonly Label _headlineLabel;
    private readonly Label _explanationLabel;
    private readonly Label _lastCheckLabel;
    private readonly Label _nextCheckLabel;
    private readonly MonitoringPulseControl _pulse;
    private readonly ListView _probeListView;
    private readonly Label _pingHeaderLabel;
    private readonly SparklineControl _sparkline;
    private readonly TextBox _detailsTextBox;
    private readonly Label _logInfoLabel;
    private readonly ListView _incidentsListView;

    // Single-window-per-item tracking for double-click history detail popups, keyed by history
    // key (not always the same as the row's raw probe id - see OpenHistoryDetailForSelectedRow).
    private readonly Dictionary<string, Form> _openHistoryWindows = new();

    // This form is destroyed and recreated fresh each time it's reopened from the tray menu, so
    // these custom Fonts (not owned by Control.Dispose, since they're assigned rather than
    // inherited from the base Control Font) are tracked here for explicit disposal.
    private readonly Font _headlineFont;
    private readonly Font _incidentsLabelFont;
    private readonly Font _pingHeaderFont;
    private readonly Font _detailsFont;

    public DiagnosticsForm(
        DiagnosticsCoordinator coordinator,
        IncidentStore incidentStore,
        DiagnosticLogger diagnosticLogger,
        AppSettings settings)
    {
        _coordinator = coordinator;
        _incidentStore = incidentStore;
        _diagnosticLogger = diagnosticLogger;
        _settings = settings;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("DiagnosticsForm must be constructed on the UI thread.");

        Text = LocalizationManager.Instance.Get("diag.title");
        Icon = TrayIconFactory.AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 800);
        MinimumSize = new Size(620, 600);

        // --- Header: diagnosis headline + explanation ---
        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 70, Padding = new Padding(12, 8, 12, 8) };
        _headlineFont = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _headlineLabel = new Label { Font = _headlineFont, AutoSize = false, Dock = DockStyle.Top, Height = 26 };
        _explanationLabel = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 36, ForeColor = Color.DimGray };
        headerPanel.Controls.Add(_explanationLabel);
        headerPanel.Controls.Add(_headlineLabel);

        // --- Toolbar: last/next check, monitoring pulse, check-now button ---
        var toolbarPanel = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(12, 4, 12, 4) };
        _lastCheckLabel = new Label { AutoSize = true, Location = new Point(0, 8) };
        _nextCheckLabel = new Label { AutoSize = true, Location = new Point(220, 8) };
        _pulse = new MonitoringPulseControl { Location = new Point(440, 4), IsActive = true };
        var checkNowButton = new Button { Text = LocalizationManager.Instance.Get("diag.checkNow"), Location = new Point(470, 2), Width = 130 };
        checkNowButton.Click += (_, _) => _ = RunNowSafelyAsync();
        toolbarPanel.Controls.Add(_lastCheckLabel);
        toolbarPanel.Controls.Add(_nextCheckLabel);
        toolbarPanel.Controls.Add(_pulse);
        toolbarPanel.Controls.Add(checkNowButton);

        // --- Logging info panel (bottom) ---
        var logPanel = new Panel { Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(12, 6, 12, 6) };
        _logInfoLabel = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 40 };
        var logButtonsPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 32, FlowDirection = FlowDirection.LeftToRight };
        var openLogButton = new Button { Text = LocalizationManager.Instance.Get("diag.openLog"), Width = 140 };
        openLogButton.Click += (_, _) => OpenLogFile();
        var exportButton = new Button { Text = LocalizationManager.Instance.Get("diag.export"), Width = 140 };
        exportButton.Click += (_, _) => ExportReport();
        var copyButton = new Button { Text = LocalizationManager.Instance.Get("diag.copy"), Width = 140 };
        copyButton.Click += (_, _) => CopyReport();
        logButtonsPanel.Controls.Add(openLogButton);
        logButtonsPanel.Controls.Add(exportButton);
        logButtonsPanel.Controls.Add(copyButton);
        logPanel.Controls.Add(logButtonsPanel);
        logPanel.Controls.Add(_logInfoLabel);

        // --- Incidents panel (bottom, above log panel) ---
        var incidentsPanel = new Panel { Dock = DockStyle.Bottom, Height = 180, Padding = new Padding(12, 6, 12, 0) };
        var incidentsHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 26, Margin = new Padding(0, 0, 0, 4) };
        _incidentsLabelFont = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        var incidentsLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = LocalizationManager.Instance.Get("diag.incidents"), Font = _incidentsLabelFont };
        var clearIncidentsButtonPanel = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        var clearIncidentsButton = new Button { Text = LocalizationManager.Instance.Get("diag.incidents.clear"), Width = 140, Height = 22 };
        clearIncidentsButton.Click += (_, _) =>
        {
            _incidentStore.Clear();
            RefreshIncidentsList();
            RefreshPingChart();
        };
        clearIncidentsButtonPanel.Controls.Add(clearIncidentsButton);
        incidentsHeaderPanel.Controls.Add(incidentsLabel);
        incidentsHeaderPanel.Controls.Add(clearIncidentsButtonPanel);

        _incidentsListView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        _incidentsListView.Columns.Add(LocalizationManager.Instance.Get("diag.incidents.id"), 160);
        _incidentsListView.Columns.Add(LocalizationManager.Instance.Get("diag.incidents.start"), 130);
        _incidentsListView.Columns.Add(LocalizationManager.Instance.Get("diag.incidents.duration"), 80);
        _incidentsListView.Columns.Add(LocalizationManager.Instance.Get("diag.incidents.type"), 120);
        _incidentsListView.Columns.Add(LocalizationManager.Instance.Get("diag.incidents.status"), 90);
        _incidentsListView.DoubleClick += (_, _) => ShowSelectedIncidentDetail();
        incidentsPanel.Controls.Add(_incidentsListView);
        incidentsPanel.Controls.Add(incidentsHeaderPanel);

        // --- Probe table + details (fills remaining space) ---
        var splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 220,
        };
        _probeListView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        _probeListView.Columns.Add(LocalizationManager.Instance.Get("diag.table.check"), 200);
        _probeListView.Columns.Add(LocalizationManager.Instance.Get("diag.table.status"), 80);
        _probeListView.Columns.Add(LocalizationManager.Instance.Get("diag.table.value"), 220);
        _probeListView.Columns.Add(LocalizationManager.Instance.Get("diag.table.duration"), 80);
        _probeListView.SelectedIndexChanged += (_, _) => ShowSelectedProbeDetails();
        _probeListView.DoubleClick += (_, _) => OpenHistoryDetailForSelectedRow();

        // --- Continuous ping-latency chart (always on, independent of row selection) ---
        var pingHeaderPanel = new Panel { Dock = DockStyle.Top, Height = 26, Margin = new Padding(0, 0, 0, 4) };
        _pingHeaderFont = new Font(Font.FontFamily, 9f, FontStyle.Bold);
        _pingHeaderLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = _pingHeaderFont };
        var clearHistoryButtonPanel = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
        var clearHistoryButton = new Button { Text = LocalizationManager.Instance.Get("diag.ping.clearHistory"), Width = 140, Height = 22 };
        clearHistoryButton.Click += (_, _) =>
        {
            _coordinator.ClearPingHistory();
            RefreshPingChart();
        };
        clearHistoryButtonPanel.Controls.Add(clearHistoryButton);
        pingHeaderPanel.Controls.Add(_pingHeaderLabel);
        pingHeaderPanel.Controls.Add(clearHistoryButtonPanel);

        _sparkline = new SparklineControl { Dock = DockStyle.Top, Height = 100, Margin = new Padding(0, 0, 0, 6) };

        _detailsTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = _detailsFont = new Font(FontFamily.GenericMonospace, 9f),
        };

        splitContainer.Panel1.Controls.Add(_probeListView);
        splitContainer.Panel2.Controls.Add(_detailsTextBox);
        splitContainer.Panel2.Controls.Add(_sparkline);
        splitContainer.Panel2.Controls.Add(pingHeaderPanel);

        Controls.Add(splitContainer);
        Controls.Add(incidentsPanel);
        Controls.Add(logPanel);
        Controls.Add(toolbarPanel);
        Controls.Add(headerPanel);

        RefreshIncidentsList();
        if (_coordinator.LatestSnapshot is { } snapshot && _coordinator.LatestDiagnosis is { } diagnosis)
        {
            PopulateProbeRows(snapshot);
            UpdateDiagnosisHeadline(diagnosis);
        }
        UpdateCheckTimes();
        UpdateLogInfo();
        RefreshPingChart();

        _coordinator.SnapshotUpdated += OnSnapshotUpdated;
        _coordinator.DiagnosisUpdated += OnDiagnosisUpdated;
        _coordinator.ProbeCompleted += OnProbeCompleted;
        FormClosed += (_, _) =>
        {
            _coordinator.SnapshotUpdated -= OnSnapshotUpdated;
            _coordinator.DiagnosisUpdated -= OnDiagnosisUpdated;
            _coordinator.ProbeCompleted -= OnProbeCompleted;
        };

        _ = RunNowSafelyAsync();
    }

    /// <summary>
    /// Fire-and-forget wrapper around RunNowAsync() for click handlers, which can't await it
    /// directly. Without this, an unexpected exception from a manual "check now" would be
    /// silently discarded along with the task.
    /// </summary>
    private async Task RunNowSafelyAsync()
    {
        try
        {
            await _coordinator.RunNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"RunNowAsync failed: {ex}");
        }
    }

    private void OnProbeCompleted(object? sender, ProbeCompletedEventArgs e)
    {
        if (e.ProbeId != DiagnosticsCoordinator.PingProbeId)
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            if (IsDisposed) return;
            RefreshPingChart();
        }, null);
    }

    private void RefreshPingChart()
    {
        DateTimeOffset windowStart = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15);

        // This always-on preview is a fixed, short "how's it doing right now" glance - it always
        // stretches whatever points it's given to fill its small fixed width, so with history
        // retention now configurable up to 32 days it must filter down to a bounded recent
        // window itself rather than showing everything GetHistory returns. The full, scrollable
        // history for any check (including this one) is available via double-click.
        var history = _coordinator.GetHistory(DiagnosticsCoordinator.PingProbeId)
            .Where(p => p.Timestamp >= windowStart)
            .ToList();
        var outages = _incidentStore.All
            .Where(i => i.EndUtc is null || i.EndUtc >= windowStart)
            .Select(i => (i.StartUtc, i.EndUtc))
            .ToList();
        _sparkline.SetOutages(outages);
        _sparkline.SetData(history, LocalizationManager.Instance.Get("diag.sparkline.noData"));
        _pingHeaderLabel.Text = LocalizationManager.Instance.Format("diag.ping.heading", _settings.PingTargetAddress);
    }

    private void OnSnapshotUpdated(object? sender, ProbeSnapshot snapshot) =>
        _uiContext.Post(_ =>
        {
            if (IsDisposed) return;
            PopulateProbeRows(snapshot);
            UpdateCheckTimes();
            RefreshIncidentsList();
        }, null);

    private void OnDiagnosisUpdated(object? sender, DiagnosisResult diagnosis) =>
        _uiContext.Post(_ =>
        {
            if (IsDisposed) return;
            UpdateDiagnosisHeadline(diagnosis);
        }, null);

    private void UpdateDiagnosisHeadline(DiagnosisResult diagnosis)
    {
        _headlineLabel.Text = $"{LocalizationManager.Instance.Get("diag.diagnosis")}: {diagnosis.Headline}";
        _headlineLabel.ForeColor = diagnosis.Classification == DiagnosisClassification.Healthy ? Color.DarkGreen
            : diagnosis.Classification == DiagnosisClassification.FirewallSuspected ? Color.DarkOrange
            : Color.Firebrick;
        _explanationLabel.Text = diagnosis.Explanation;
    }

    private void UpdateCheckTimes()
    {
        _lastCheckLabel.Text = _coordinator.LastCheckUtc is { } last
            ? LocalizationManager.Instance.Format("diag.lastCheck", last.ToLocalTime().ToString("HH:mm:ss"))
            : string.Empty;
        _nextCheckLabel.Text = _coordinator.NextCheckUtc is { } next
            ? LocalizationManager.Instance.Format("diag.nextCheck", next.ToLocalTime().ToString("HH:mm:ss"))
            : string.Empty;
    }

    // Shows the last size this form managed to read; updated in the background (see
    // RefreshLogFileSizeAsync) rather than stat-ing the file synchronously on the UI thread on
    // every SnapshotUpdated (~every 15s while this window is open), which could otherwise cause a
    // brief UI hitch on a slow or roaming profile disk.
    private string _lastLogSizeInfo = "-";

    private void UpdateLogInfo()
    {
        RenderLogInfoText();
        RefreshLogFileSizeAsync();
    }

    private void RenderLogInfoText()
    {
        string levelKey = _settings.DiagnosticLogLevel switch
        {
            DiagnosticLogLevel.Off => "settings.logLevel.off",
            DiagnosticLogLevel.Basic => "settings.logLevel.basic",
            DiagnosticLogLevel.Extended => "settings.logLevel.extended",
            _ => "settings.logLevel.full",
        };
        string level = LocalizationManager.Instance.Get(levelKey);

        _logInfoLabel.Text =
            $"{LocalizationManager.Instance.Get("diag.logLevel")}: {level}\r\n" +
            $"{LocalizationManager.Instance.Get("diag.logFile")}: {_diagnosticLogger.LogPath} ({_lastLogSizeInfo})";
    }

    private void RefreshLogFileSizeAsync()
    {
        string logPath = _diagnosticLogger.LogPath;
        _ = Task.Run(() =>
        {
            string sizeInfo = "-";
            try
            {
                if (File.Exists(logPath))
                {
                    var info = new FileInfo(logPath);
                    sizeInfo = $"{info.Length / 1024.0 / 1024.0:F2} MB";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave sizeInfo as "-"; this is informational only.
            }

            _uiContext.Post(_ =>
            {
                if (IsDisposed) return;
                _lastLogSizeInfo = sizeInfo;
                RenderLogInfoText();
            }, null);
        });
    }

    private void PopulateProbeRows(ProbeSnapshot snapshot)
    {
        string? selectedProbeId = _probeListView.SelectedItems.Count > 0
            ? GetProbeId(_probeListView.SelectedItems[0].Tag)
            : null;
        _probeListView.Items.Clear();

        AddRow(LocalizationManager.Instance.Get("diag.row.network"), snapshot.NetworkInterface);
        AddRow(LocalizationManager.Instance.Get("diag.row.ip"), snapshot.IpAddress);
        AddRow(LocalizationManager.Instance.Get("diag.row.gateway"), snapshot.Gateway);
        if (_coordinator.LatestPingResult is { } pingResult)
        {
            AddRow(LocalizationManager.Instance.Get("diag.row.ping"), pingResult);
        }
        foreach (PingEndpointResult ep in snapshot.Internet.Endpoints)
        {
            var item = new ListViewItem([ep.Address, ep.Reachable ? "OK" : "ERROR", ep.Address, ep.Reachable ? $"{ep.LatencyMs:F0} ms" : "-"])
            {
                Tag = new Dictionary<string, string>
                {
                    ["Address"] = ep.Address,
                    ["Reachable"] = ep.Reachable.ToString(),
                    ["LatencyMs"] = ep.LatencyMs?.ToString("F0") ?? "-",
                },
            };
            _probeListView.Items.Add(item);
        }
        AddRow(LocalizationManager.Instance.Get("diag.row.dns"), snapshot.Dns);
        AddRow(LocalizationManager.Instance.Get("diag.row.https"), snapshot.GeneralHttps);
        foreach (var (name, result) in snapshot.ApplicationEndpoints)
        {
            AddRow(name, result);
        }
        AddRow(LocalizationManager.Instance.Get("diag.row.time"), snapshot.TimeSync);

        UpdateLogInfo();

        if (selectedProbeId is not null)
        {
            ListViewItem? match = _probeListView.Items.Cast<ListViewItem>()
                .FirstOrDefault(item => GetProbeId(item.Tag) == selectedProbeId);
            if (match is not null)
            {
                match.Selected = true;
            }
            else
            {
                ShowSelectedProbeDetails();
            }
        }
    }

    private static string? GetProbeId(object? tag) => tag switch
    {
        IProbeResult r => r.ProbeId,
        Dictionary<string, string> d when d.TryGetValue("Address", out string? address) => address,
        _ => null,
    };

    private void AddRow(string name, IProbeResult result)
    {
        var item = new ListViewItem([name, result.Status.ToString(), result.Summary, $"{result.Duration.TotalMilliseconds:F0} ms"])
        {
            Tag = result,
        };
        if (result.Status == ProbeStatus.Error || result.Status == ProbeStatus.Blocked)
        {
            item.ForeColor = Color.Firebrick;
        }
        else if (result.Status == ProbeStatus.Warning)
        {
            item.ForeColor = Color.DarkOrange;
        }

        _probeListView.Items.Add(item);
    }

    private void ShowSelectedProbeDetails()
    {
        if (_probeListView.SelectedItems.Count == 0)
        {
            _detailsTextBox.Text = string.Empty;
            return;
        }

        object tag = _probeListView.SelectedItems[0].Tag!;
        IReadOnlyDictionary<string, string> details = tag switch
        {
            IProbeResult r => r.ToDetails(),
            Dictionary<string, string> d => d,
            _ => new Dictionary<string, string>(),
        };

        string? errorDetail = (tag as IProbeResult)?.ErrorDetail;
        var lines = details.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        if (!string.IsNullOrEmpty(errorDetail))
        {
            lines.Add($"Error: {errorDetail}");
        }

        _detailsTextBox.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Opens (or activates the already-open) history detail popup for the selected row. This is
    /// a deliberately separate mapping from <see cref="GetProbeId"/> - that one returns the bare
    /// address for internet-endpoint rows (it only exists for reselection-after-refresh); the
    /// history key for those rows needs the "internet:{address}" shape instead, to match what
    /// DiagnosticsCoordinator now records per public-IP endpoint.
    /// </summary>
    private void OpenHistoryDetailForSelectedRow()
    {
        if (_probeListView.SelectedItems.Count == 0)
        {
            return;
        }

        ListViewItem item = _probeListView.SelectedItems[0];
        string displayName = item.SubItems[0].Text;
        string historyKey;
        string triggerProbeId;
        string valueUnitLabel;

        if (item.Tag is Dictionary<string, string> d && d.TryGetValue("Address", out string? address))
        {
            historyKey = $"internet:{address}";
            triggerProbeId = "internet";
            valueUnitLabel = "ms";
        }
        else if (item.Tag is IProbeResult result)
        {
            historyKey = result.ProbeId;
            triggerProbeId = result.ProbeId;
            valueUnitLabel = result is TimeSyncProbeResult ? "s" : "ms";
        }
        else
        {
            return;
        }

        if (_openHistoryWindows.TryGetValue(historyKey, out Form? existing) && !existing.IsDisposed)
        {
            existing.Activate();
            return;
        }

        var detailForm = new ProbeHistoryDetailForm(_coordinator, _incidentStore, historyKey, triggerProbeId, displayName, valueUnitLabel);
        detailForm.FormClosed += (_, _) => _openHistoryWindows.Remove(historyKey);
        _openHistoryWindows[historyKey] = detailForm;
        detailForm.Show(this);
    }

    private void RefreshIncidentsList()
    {
        object? selectedId = _incidentsListView.SelectedItems.Count > 0 ? _incidentsListView.SelectedItems[0].Tag : null;
        _incidentsListView.Items.Clear();
        foreach (Incident incident in _incidentStore.All.OrderByDescending(i => i.StartUtc))
        {
            string duration = incident.Duration is { } d ? d.ToString(@"hh\:mm\:ss") : "-";
            var item = new ListViewItem([incident.Id, incident.StartUtc.ToLocalTime().ToString("HH:mm:ss"), duration, incident.Classification.ToString(), incident.Status.ToString()])
            {
                Tag = incident,
            };
            _incidentsListView.Items.Add(item);
        }
    }

    private void ShowSelectedIncidentDetail()
    {
        if (_incidentsListView.SelectedItems.Count == 0)
        {
            return;
        }

        var incident = (Incident)_incidentsListView.SelectedItems[0].Tag!;
        string report = DiagnosticReportBuilder.BuildIncidentReport(incident);
        using var form = new TextReportForm(incident.Id, report);
        form.ShowDialog(this);
    }

    private void OpenLogFile()
    {
        if (!File.Exists(_diagnosticLogger.LogPath))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_diagnosticLogger.LogPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No default handler registered for .log files - nothing we can do here.
        }
    }

    private void ExportReport()
    {
        if (_coordinator.LatestSnapshot is null || _coordinator.LatestDiagnosis is null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt",
            FileName = $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(dialog.FileName, DiagnosticReportBuilder.BuildReport(_coordinator.LatestSnapshot, _coordinator.LatestDiagnosis));
        }
    }

    private void CopyReport()
    {
        if (_coordinator.LatestSnapshot is null || _coordinator.LatestDiagnosis is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(DiagnosticReportBuilder.BuildReport(_coordinator.LatestSnapshot, _coordinator.LatestDiagnosis));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Clipboard can be transiently locked by another process - not worth surfacing an error for.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headlineFont.Dispose();
            _incidentsLabelFont.Dispose();
            _pingHeaderFont.Dispose();
            _detailsFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
