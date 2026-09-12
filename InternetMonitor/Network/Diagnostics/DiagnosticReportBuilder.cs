using System.Text;
using InternetMonitor.Localization;
using InternetMonitor.Network.Diagnosis;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network.Diagnostics;

/// <summary>Builds the plain-text diagnostics report used by the "copy"/"export" actions and incident detail views.</summary>
public static class DiagnosticReportBuilder
{
    public static string BuildReport(ProbeSnapshot snapshot, DiagnosisResult diagnosis)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var sb = new StringBuilder();
        sb.AppendLine(loc.Get("report.heading"));
        sb.AppendLine(loc.Format("report.timestamp", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine();

        sb.AppendLine(loc.Get("report.interface"));
        sb.AppendLine(loc.Format("report.interface.type", snapshot.NetworkInterface.InterfaceType?.ToString() ?? "-"));
        sb.AppendLine(loc.Format("report.interface.status", snapshot.NetworkInterface.Summary));
        sb.AppendLine(loc.Format("report.interface.ipv4", snapshot.IpAddress.IPv4Address ?? "-"));
        sb.AppendLine(loc.Format("report.interface.gateway", snapshot.Gateway.GatewayAddress ?? "-"));
        sb.AppendLine();

        sb.AppendLine(loc.Get("report.gateway"));
        sb.AppendLine($"  {snapshot.Gateway.GatewayAddress ?? "-"}: {snapshot.Gateway.Status} ({snapshot.Gateway.Summary})");
        sb.AppendLine();

        sb.AppendLine(loc.Get("report.externalConnectivity"));
        foreach (PingEndpointResult ep in snapshot.Internet.Endpoints)
        {
            sb.AppendLine(ep.Reachable ? $"  {ep.Address}: OK ({ep.LatencyMs:F0} ms)" : $"  {ep.Address}: TIMEOUT");
        }
        sb.AppendLine();

        sb.AppendLine(loc.Get("report.dns"));
        sb.AppendLine($"  {snapshot.Dns.Hostname}: {snapshot.Dns.Status} ({snapshot.Dns.Summary})");
        sb.AppendLine();

        sb.AppendLine(loc.Get("report.https"));
        sb.AppendLine($"  {snapshot.GeneralHttps.Url}: {snapshot.GeneralHttps.Summary}");
        sb.AppendLine();

        if (snapshot.ApplicationEndpoints.Count > 0)
        {
            sb.AppendLine(loc.Get("report.applicationEndpoints"));
            foreach (var (name, result) in snapshot.ApplicationEndpoints)
            {
                sb.AppendLine($"  {name}: {result.Summary}");
            }
            sb.AppendLine();
        }

        sb.AppendLine(loc.Get("report.timeSync"));
        sb.AppendLine(loc.Format("report.timeSync.difference", snapshot.TimeSync.Summary));
        sb.AppendLine();

        sb.AppendLine(loc.Get("report.diagnosis"));
        sb.AppendLine($"  {diagnosis.Headline}.");
        sb.AppendLine($"  {diagnosis.Explanation}");

        return sb.ToString();
    }

    public static string BuildIncidentReport(Incident incident)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var sb = new StringBuilder();
        sb.AppendLine(incident.Id);
        sb.AppendLine($"{loc.Get("report.incident.status")} {incident.Status}");
        sb.AppendLine($"{loc.Get("report.incident.type")} {incident.Classification}");
        sb.AppendLine(loc.Format("report.incident.start", incident.StartUtc.ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine(incident.EndUtc is { } end
            ? loc.Format("report.incident.end", end.ToString("yyyy-MM-dd HH:mm:ss"))
            : loc.Get("report.incident.end.none"));
        sb.AppendLine(loc.Format("report.incident.duration", incident.Duration is { } d ? d.ToString(@"hh\:mm\:ss") : "-"));
        sb.AppendLine();
        sb.AppendLine(loc.Get("report.diagnosis"));
        sb.AppendLine(incident.Diagnosis);
        sb.AppendLine();
        sb.AppendLine(loc.Get("report.incident.duringIncident"));
        foreach (var (probeId, details) in incident.SnapshotAtStart)
        {
            sb.AppendLine($"  {probeId}: {(details.TryGetValue("Summary", out string? s) ? s : "-")}");
        }

        if (incident.SnapshotAtResolution is not null)
        {
            sb.AppendLine();
            sb.AppendLine(loc.Get("report.incident.recovery"));
            foreach (var (probeId, details) in incident.SnapshotAtResolution)
            {
                sb.AppendLine($"  {probeId}: {(details.TryGetValue("Summary", out string? s) ? s : "-")}");
            }
        }

        return sb.ToString();
    }
}
