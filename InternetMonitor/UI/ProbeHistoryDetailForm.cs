using InternetMonitor.Localization;
using InternetMonitor.Network;
using InternetMonitor.Network.Diagnostics;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.UI;

/// <summary>
/// Popup opened by double-clicking a row on the Diagnostics screen: the full, scrollable/
/// zoomable/hoverable history for one check, live-updating while open. At most one of these is
/// ever open per item - <see cref="DiagnosticsForm"/> owns that single-window-per-key tracking,
/// this form just represents one such window once opened.
/// </summary>
public sealed class ProbeHistoryDetailForm : Form
{
    private readonly DiagnosticsCoordinator _coordinator;
    private readonly IncidentStore _incidentStore;
    private readonly SynchronizationContext _uiContext;
    private readonly string _historyKey;
    private readonly string _triggerProbeId;
    private readonly string _valueUnitLabel;
    private readonly bool _detailsLiveUpdate;

    // IP-address is a string, not a number - charting it (even as some hashed/encoded fake value)
    // would be meaningless to read, so it gets a second display mode: a plain change log instead
    // of the chart every other probe uses. Both _chart and _changeLogListView are always
    // constructed regardless of mode (simpler than making every method that touches one of them
    // nullable-aware) - only the one this instance actually needs is added to _historyPanel.
    private readonly bool _isChangeLog;

    private readonly TextBox _detailsTextBox;
    private readonly Font _detailsFont;
    private readonly Panel _historyPanel;
    private readonly HistoryChartControl _chart;
    private readonly ListView _changeLogListView;

    /// <param name="initialDetailsText">
    /// The selected row's details (SSID/BSSID/link speed, target address, etc. - whatever
    /// <see cref="ProbeDetailsFormatter"/> produced for it) at the moment this popup was opened.
    /// The chart already shows this check's value over time; this fills in everything about the
    /// check that ISN'T a time series - shown right above the chart rather than requiring the
    /// user to also keep the main Diagnostics window's row selected to see it.
    /// </param>
    /// <param name="detailsLiveUpdate">
    /// Whether to keep refreshing the details text as new data arrives (true for a genuine
    /// per-probe result; false for the public-IP endpoint rows, whose shared "internet" probe id
    /// doesn't carry this specific endpoint's own data on every completion).
    /// </param>
    public ProbeHistoryDetailForm(
        DiagnosticsCoordinator coordinator,
        IncidentStore incidentStore,
        string historyKey,
        string triggerProbeId,
        string displayName,
        string valueUnitLabel,
        string? initialDetailsText,
        bool detailsLiveUpdate)
    {
        _coordinator = coordinator;
        _incidentStore = incidentStore;
        _historyKey = historyKey;
        _triggerProbeId = triggerProbeId;
        _valueUnitLabel = valueUnitLabel;
        _detailsLiveUpdate = detailsLiveUpdate;
        _isChangeLog = triggerProbeId == "ip";
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ProbeHistoryDetailForm must be constructed on the UI thread.");

        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"{displayName} — {LocalizationManager.Instance.Get("history.detail.titleSuffix")}";
        Icon = TrayIconFactory.AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(480, 340);

        _detailsFont = new Font(FontFamily.GenericMonospace, 9f);
        _detailsTextBox = new TextBox
        {
            Dock = DockStyle.Top,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Height = 70,
            Font = _detailsFont,
            Text = initialDetailsText ?? string.Empty,
        };

        _historyPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _chart = new HistoryChartControl
        {
            Location = Point.Empty,
            Size = new Size(600, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
        };
        _changeLogListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _changeLogListView.Columns.Add(LocalizationManager.Instance.Get("history.changeLog.time"), 160);
        _changeLogListView.Columns.Add(LocalizationManager.Instance.Get("history.changeLog.ipAddress"), 200);

        if (_isChangeLog)
        {
            _historyPanel.Controls.Add(_changeLogListView);
        }
        else
        {
            _historyPanel.Controls.Add(_chart);
            _historyPanel.Resize += (_, _) => _chart.NotifyViewportResized();
        }

        Controls.Add(_historyPanel);
        Controls.Add(_detailsTextBox);

        // Deferred to Load rather than done here: before the form/panel handle exists,
        // _historyPanel.ClientSize can still read 0,0, which would compute a wrong initial zoom.
        Load += (_, _) => RefreshHistory();

        _coordinator.ProbeCompleted += OnProbeCompleted;
        FormClosed += (_, _) => _coordinator.ProbeCompleted -= OnProbeCompleted;
    }

    private void OnProbeCompleted(object? sender, ProbeCompletedEventArgs e)
    {
        if (e.ProbeId != _triggerProbeId)
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            if (IsDisposed) return;
            RefreshHistory();
            if (_detailsLiveUpdate)
            {
                _detailsTextBox.Text = ProbeDetailsFormatter.Format(e.Result);
            }
        }, null);
    }

    private void RefreshHistory()
    {
        if (_isChangeLog)
        {
            RefreshChangeLog();
        }
        else
        {
            RefreshChart();
        }
    }

    private void RefreshChangeLog()
    {
        if (IsDisposed)
        {
            return;
        }

        var points = _coordinator.GetHistoryWithMetadata(_historyKey);

        // Only keep entries whose value actually differs from the previous *kept* entry, so a
        // value that changes away and later back to an earlier one still shows both transitions
        // rather than collapsing across the gap.
        var changes = new List<(DateTimeOffset Timestamp, string? Address)>();
        string? previousAddress = null;
        foreach (var point in points)
        {
            if (point.Metadata != previousAddress)
            {
                changes.Add((point.Timestamp, point.Metadata));
                previousAddress = point.Metadata;
            }
        }

        _changeLogListView.BeginUpdate();
        _changeLogListView.Items.Clear();
        if (changes.Count == 0)
        {
            _changeLogListView.Items.Add(new ListViewItem([LocalizationManager.Instance.Get("history.changeLog.noData"), string.Empty]));
        }
        else
        {
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                _changeLogListView.Items.Add(new ListViewItem([change.Timestamp.LocalDateTime.ToString("g"), change.Address ?? string.Empty]));
            }
        }
        _changeLogListView.EndUpdate();
    }

    private void RefreshChart()
    {
        if (IsDisposed)
        {
            return;
        }

        var outages = _incidentStore.All.Select(i => (i.StartUtc, i.EndUtc)).ToList();
        _chart.SetOutages(outages);

        // Time sync checks only run once an hour (see TimeSyncProbe.Id == "time" /
        // DiagnosticsCoordinator's TimeSyncInterval) - a generic "not enough measurements yet"
        // reads as broken shortly after starting the app, when there's genuinely only been time
        // for 0-1 checks so far, rather than as the expected, working, just-sparse state it is.
        string emptyMessage = _triggerProbeId == "time"
            ? LocalizationManager.Instance.Get("diag.sparkline.noData.timeSync")
            : LocalizationManager.Instance.Get("diag.sparkline.noData");

        if (_triggerProbeId == WifiSignalProbe.ProbeIdValue)
        {
            // Each point's own SSID/BSSID/link speed, decoded from the opaque metadata string
            // DiagnosticsCoordinator recorded alongside it - what was actually true at that exact
            // moment, not just whatever's current now. A point recorded before this feature
            // existed simply has no metadata and shows time+value only, same as before.
            var pointsWithWifiInfo = _coordinator.GetHistoryWithMetadata(_historyKey)
                .Select(p => (p.Timestamp, p.Value, FormatWifiMetadata(p.Metadata)))
                .ToList();
            _chart.SetData(pointsWithWifiInfo, _valueUnitLabel, emptyMessage);
        }
        else if (_triggerProbeId == "network")
        {
            // "Connected"/"Not connected" is fully derivable from the 0/1 value itself - no
            // stored metadata needed, unlike Wi-Fi's SSID/BSSID/Linkspeed.
            var pointsWithConnectionState = _coordinator.GetHistory(_historyKey)
                .Select(p => (p.Timestamp, p.Value,
                    (string?)(p.Value >= 0.5
                        ? LocalizationManager.Instance.Get("diag.network.connected")
                        : LocalizationManager.Instance.Get("diag.network.disconnected"))))
                .ToList();
            _chart.SetData(pointsWithConnectionState, _valueUnitLabel, emptyMessage);
        }
        else
        {
            var history = _coordinator.GetHistory(_historyKey);
            _chart.SetData(history, _valueUnitLabel, emptyMessage);
        }
    }

    private static string? FormatWifiMetadata(string? raw)
    {
        WifiHistoryPointMetadata? metadata = WifiHistoryPointMetadata.Decode(raw);
        return metadata is null
            ? null
            : $"SSID: {metadata.Ssid}\nBSSID: {metadata.Bssid}\nLinkspeed: {metadata.LinkSpeedMbps} Mbps";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _detailsFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
