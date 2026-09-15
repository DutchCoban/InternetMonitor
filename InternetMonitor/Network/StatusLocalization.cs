using InternetMonitor.Localization;
using InternetMonitor.Network.Diagnosis;
using InternetMonitor.Network.Diagnostics;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network;

/// <summary>
/// Localized display text for the three enums shown raw (via .ToString()) in a few places before
/// this existed - DiagnosticsForm's probe table/incidents list and DiagnosticReportBuilder's text
/// report. Reuses existing loc keys wherever one already covers the same concept (e.g. the
/// Uptime Report checklist's per-classification keys) rather than duplicating them.
/// </summary>
public static class StatusLocalization
{
    public static string Localize(this ProbeStatus status) => LocalizationManager.Instance.Get(status switch
    {
        ProbeStatus.Unknown => "status.unknown",
        ProbeStatus.Checking => "status.checking",
        ProbeStatus.Ok => "status.ok",
        ProbeStatus.Warning => "status.warning",
        ProbeStatus.Error => "status.error",
        ProbeStatus.Blocked => "status.blocked",
        _ => "status.unknown",
    });

    public static string Localize(this IncidentStatus status) => LocalizationManager.Instance.Get(status switch
    {
        IncidentStatus.Open => "incidentStatus.open",
        IncidentStatus.Active => "incidentStatus.active",
        IncidentStatus.Resolved => "incidentStatus.resolved",
        _ => "incidentStatus.open",
    });

    public static string Localize(this DiagnosisClassification classification) => LocalizationManager.Instance.Get(classification switch
    {
        DiagnosisClassification.Healthy => "classification.healthy",
        DiagnosisClassification.Network => "diag.row.network",
        DiagnosisClassification.IpConfiguration => "diag.row.ip",
        DiagnosisClassification.Gateway => "diag.row.gateway",
        DiagnosisClassification.Internet => "diag.row.internet",
        DiagnosisClassification.Dns => "diag.row.dns",
        DiagnosisClassification.Https => "diag.row.https",
        DiagnosisClassification.Application => "classification.application",
        DiagnosisClassification.TimeSync => "diag.row.time",
        DiagnosisClassification.FirewallSuspected => "settings.sla.firewallSuspected",
        _ => "classification.healthy",
    });
}
