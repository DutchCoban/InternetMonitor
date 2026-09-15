using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Network.Diagnostics;

namespace InternetMonitor.UI;

/// <summary>
/// Small popup showing the uptime/SLA percentage for a chosen period, computed via
/// <see cref="UptimeReportBuilder"/> from already-recorded incidents, honoring whichever items
/// are checked in Settings > SLA Report (<see cref="AppSettings.SlaExcludedClassifications"/>/
/// <see cref="AppSettings.SlaExcludedEndpointIds"/>).
/// </summary>
public sealed class UptimeReportForm : Form
{
    private static readonly int[] PeriodDays = [7, 30, 90];

    private readonly IncidentStore _incidentStore;
    private readonly AppSettings _settings;
    private readonly ComboBox _periodCombo;
    private readonly Label _resultLabel;
    private readonly Font _resultFont;

    public UptimeReportForm(IncidentStore incidentStore, AppSettings settings)
    {
        _incidentStore = incidentStore;
        _settings = settings;

        Text = LocalizationManager.Instance.Get("uptimeReport.title");
        Icon = TrayIconFactory.AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 170);

        var periodLabel = new Label { Text = LocalizationManager.Instance.Get("uptimeReport.period"), Bounds = new Rectangle(16, 16, 100, 22) };
        _periodCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(120, 14, 220, 24) };
        foreach (int days in PeriodDays)
        {
            _periodCombo.Items.Add(LocalizationManager.Instance.Format("uptimeReport.periodDays", days));
        }
        _periodCombo.SelectedIndex = 1;
        _periodCombo.SelectedIndexChanged += (_, _) => RefreshReport();

        _resultFont = new Font(Font.FontFamily, 10f);
        _resultLabel = new Label { AutoSize = false, Font = _resultFont, Bounds = new Rectangle(16, 56, 328, 70) };

        var closeButton = new Button { Text = LocalizationManager.Instance.Get("settings.close"), Bounds = new Rectangle(260, 134, 84, 26), DialogResult = DialogResult.OK };

        Controls.Add(periodLabel);
        Controls.Add(_periodCombo);
        Controls.Add(_resultLabel);
        Controls.Add(closeButton);
        CancelButton = closeButton;

        RefreshReport();
    }

    private void RefreshReport()
    {
        int days = PeriodDays[Math.Max(0, _periodCombo.SelectedIndex)];
        DateTimeOffset periodEnd = DateTimeOffset.UtcNow;
        DateTimeOffset periodStart = periodEnd - TimeSpan.FromDays(days);

        UptimeReport report = UptimeReportBuilder.Compute(
            _incidentStore.All, periodStart, periodEnd,
            _settings.SlaExcludedClassifications, _settings.SlaExcludedEndpointIds);

        _resultLabel.Text =
            LocalizationManager.Instance.Format("uptimeReport.result", report.UptimePercentage.ToString("F2")) +
            Environment.NewLine +
            LocalizationManager.Instance.Format("uptimeReport.downtime", report.Downtime.ToString(@"hh\:mm\:ss"), report.IncidentCount);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _resultFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
