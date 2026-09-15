using InternetMonitor.Localization;
using InternetMonitor.Network;
using InternetMonitor.Network.Diagnostics;

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

    private readonly Panel _historyPanel;
    private readonly HistoryChartControl _chart;

    public ProbeHistoryDetailForm(
        DiagnosticsCoordinator coordinator,
        IncidentStore incidentStore,
        string historyKey,
        string triggerProbeId,
        string displayName,
        string valueUnitLabel)
    {
        _coordinator = coordinator;
        _incidentStore = incidentStore;
        _historyKey = historyKey;
        _triggerProbeId = triggerProbeId;
        _valueUnitLabel = valueUnitLabel;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ProbeHistoryDetailForm must be constructed on the UI thread.");

        Text = $"{displayName} — {LocalizationManager.Instance.Get("history.detail.titleSuffix")}";
        Icon = TrayIconFactory.AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 460);
        MinimumSize = new Size(480, 300);

        _historyPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _chart = new HistoryChartControl
        {
            Location = Point.Empty,
            Size = new Size(600, 300),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
        };
        _historyPanel.Controls.Add(_chart);
        _historyPanel.Resize += (_, _) => _chart.NotifyViewportResized();
        Controls.Add(_historyPanel);

        // Deferred to Load rather than done here: before the form/panel handle exists,
        // _historyPanel.ClientSize can still read 0,0, which would compute a wrong initial zoom.
        Load += (_, _) => RefreshChart();

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
            RefreshChart();
        }, null);
    }

    private void RefreshChart()
    {
        if (IsDisposed)
        {
            return;
        }

        var history = _coordinator.GetHistory(_historyKey);
        var outages = _incidentStore.All.Select(i => (i.StartUtc, i.EndUtc)).ToList();
        _chart.SetOutages(outages);
        _chart.SetData(history, _valueUnitLabel, LocalizationManager.Instance.Get("diag.sparkline.noData"));
    }
}
