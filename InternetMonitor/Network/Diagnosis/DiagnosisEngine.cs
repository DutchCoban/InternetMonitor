using InternetMonitor.Localization;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network.Diagnosis;

/// <summary>
/// Pure function: interprets a snapshot of every probe's latest result and identifies the
/// most likely single root cause, from the network layer outward (adapter -&gt; IP -&gt; gateway
/// -&gt; WAN -&gt; DNS -&gt; HTTPS -&gt; application), rather than reporting every failed probe as its
/// own "internet is down" symptom. No network I/O, fully deterministic given a snapshot and the
/// current UI language - safe to unit-test with synthetic snapshots as long as
/// <see cref="LocalizationManager.Initialize"/> has been called first (headline/explanation text
/// is looked up from there, like the rest of the UI, rather than hardcoded in one language).
/// </summary>
public static class DiagnosisEngine
{
    public static DiagnosisResult Diagnose(ProbeSnapshot snapshot)
    {
        LocalizationManager loc = LocalizationManager.Instance;

        if (snapshot.NetworkInterface.Status != ProbeStatus.Ok)
        {
            return new DiagnosisResult(
                DiagnosisClassification.Network,
                loc.Get("diagnosis.network.title"),
                loc.Format("diagnosis.network.detail", snapshot.NetworkInterface.Summary),
                [snapshot.NetworkInterface.ProbeId],
                ProbeStatus.Error);
        }

        if (snapshot.IpAddress.Status != ProbeStatus.Ok)
        {
            string reason = snapshot.IpAddress.IsApipa
                ? loc.Get("diagnosis.ipConfig.reason.apipa")
                : loc.Get("diagnosis.ipConfig.reason.noIp");
            return new DiagnosisResult(
                DiagnosisClassification.IpConfiguration,
                loc.Get("diagnosis.ipConfig.title"),
                loc.Format("diagnosis.ipConfig.detail", reason),
                [snapshot.IpAddress.ProbeId],
                ProbeStatus.Error);
        }

        if (snapshot.Gateway.Status != ProbeStatus.Ok)
        {
            return new DiagnosisResult(
                DiagnosisClassification.Gateway,
                loc.Get("diagnosis.gateway.title"),
                loc.Format("diagnosis.gateway.detail", snapshot.Gateway.Summary),
                [snapshot.Gateway.ProbeId],
                ProbeStatus.Error);
        }

        bool icmpDegraded = snapshot.Internet.Status != ProbeStatus.Ok;
        bool generalHttpsOk = snapshot.GeneralHttps.Status is ProbeStatus.Ok or ProbeStatus.Warning;

        if (icmpDegraded && !generalHttpsOk)
        {
            return new DiagnosisResult(
                DiagnosisClassification.Internet,
                loc.Get("diagnosis.internet.title"),
                loc.Format("diagnosis.internet.detail", snapshot.Internet.Summary),
                [snapshot.Internet.ProbeId, snapshot.GeneralHttps.ProbeId],
                ProbeStatus.Error);
        }

        if (icmpDegraded && generalHttpsOk)
        {
            return new DiagnosisResult(
                DiagnosisClassification.FirewallSuspected,
                loc.Get("diagnosis.firewallSuspected.title"),
                loc.Format("diagnosis.firewallSuspected.detail", snapshot.Internet.Summary),
                [snapshot.Internet.ProbeId],
                ProbeStatus.Warning);
        }

        if (snapshot.Dns.Status != ProbeStatus.Ok)
        {
            return new DiagnosisResult(
                DiagnosisClassification.Dns,
                loc.Get("diagnosis.dns.title"),
                loc.Format("diagnosis.dns.detail", snapshot.Dns.Summary),
                [snapshot.Dns.ProbeId],
                ProbeStatus.Error);
        }

        // Includes Warning (e.g. a 5xx response), not just Error - otherwise a degraded general
        // HTTPS endpoint with everything else healthy silently falls through to Healthy below,
        // since Warning is treated as "ok enough" by the ICMP-comparison branches above.
        if (snapshot.GeneralHttps.Status is ProbeStatus.Error or ProbeStatus.Warning)
        {
            return new DiagnosisResult(
                DiagnosisClassification.Https,
                loc.Get("diagnosis.https.title"),
                loc.Format("diagnosis.https.detail", snapshot.GeneralHttps.Summary),
                [snapshot.GeneralHttps.ProbeId],
                ProbeStatus.Error);
        }

        var failedEndpoints = snapshot.ApplicationEndpoints
            .Where(e => e.Result.Status is ProbeStatus.Error or ProbeStatus.Warning)
            .ToList();
        if (failedEndpoints.Count > 0)
        {
            string names = string.Join(", ", failedEndpoints.Select(e => e.Name));
            return new DiagnosisResult(
                DiagnosisClassification.Application,
                loc.Get("diagnosis.application.title"),
                loc.Format("diagnosis.application.detail", names),
                failedEndpoints.Select(e => e.Result.ProbeId).ToList(),
                ProbeStatus.Error);
        }

        if (snapshot.TimeSync.Status == ProbeStatus.Unknown)
        {
            return new DiagnosisResult(
                DiagnosisClassification.TimeSync,
                loc.Get("diagnosis.timeSync.unreachable.title"),
                loc.Get("diagnosis.timeSync.unreachable.detail"),
                [snapshot.TimeSync.ProbeId],
                ProbeStatus.Warning);
        }

        if (snapshot.TimeSync.Status == ProbeStatus.Warning)
        {
            return new DiagnosisResult(
                DiagnosisClassification.TimeSync,
                loc.Get("diagnosis.timeSync.drift.title"),
                loc.Format("diagnosis.timeSync.drift.detail", snapshot.TimeSync.Summary),
                [snapshot.TimeSync.ProbeId],
                ProbeStatus.Warning);
        }

        return new DiagnosisResult(
            DiagnosisClassification.Healthy,
            loc.Get("diagnosis.healthy.title"),
            loc.Get("diagnosis.healthy.detail"),
            [],
            ProbeStatus.Ok);
    }
}
