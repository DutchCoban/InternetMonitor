using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network;

/// <summary>
/// Turns a raw latency measurement into a status using the user-configured Warning/Error
/// thresholds - the one place "300ms/1000ms" means something, reused by every probe that
/// measures latency (Gateway, PingLatency, PortReachability, HttpsEndpoint) so the meaning stays
/// consistent across the app. Takes plain thresholds rather than <see cref="Configuration.AppSettings"/>
/// directly so probes don't need to depend on the Configuration layer.
/// </summary>
public static class LatencyClassifier
{
    public static ProbeStatus Classify(double latencyMs, int warningThresholdMs, int errorThresholdMs) => latencyMs switch
    {
        _ when latencyMs >= errorThresholdMs => ProbeStatus.Error,
        _ when latencyMs >= warningThresholdMs => ProbeStatus.Warning,
        _ => ProbeStatus.Ok,
    };
}
