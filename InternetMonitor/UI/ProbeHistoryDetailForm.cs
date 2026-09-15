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

    private readonly TextBox _detailsTextBox;
    private readonly Font _detailsFont;
    private readonly Panel _historyPanel;
    private readonly HistoryChartControl _chart;

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
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("ProbeHistoryDetailForm must be constructed on the UI thread.");

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
        _historyPanel.Controls.Add(_chart);
        _historyPanel.Resize += (_, _) => _chart.NotifyViewportResized();
        Controls.Add(_historyPanel);
        Controls.Add(_detailsTextBox);

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
            if (_detailsLiveUpdate)
            {
                _detailsTextBox.Text = ProbeDetailsFormatter.Format(e.Result);
            }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _detailsFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
