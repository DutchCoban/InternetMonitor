using System.Diagnostics;
using System.Net.NetworkInformation;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record PingLatencyProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string TargetAddress,
    double? LatencyMs,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => LatencyMs;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string> { ["TargetAddress"] = TargetAddress };
        if (LatencyMs is not null) d["LatencyMs"] = LatencyMs.Value.ToString("F0");
        return d;
    }
}

/// <summary>
/// Continuous single-target ICMP ping (same approach as one of <see cref="InternetProbe"/>'s
/// three endpoints, generalized to an arbitrary user-configurable address) intended to be run
/// on its own fast cadence for a live latency graph, independent of the main poll cycle.
/// </summary>
public sealed class PingLatencyProbe(string id, string targetAddress, TimeSpan timeout, int warningThresholdMs = 300, int errorThresholdMs = 1000) : IProbe
{
    public string Id => id;
    public string Category => "Network";
    public string TargetAddress => targetAddress;

    public async Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(targetAddress, (int)timeout.TotalMilliseconds).WaitAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();

            if (reply.Status == IPStatus.Success)
            {
                double latencyMs = reply.RoundtripTime;
                ProbeStatus status = LatencyClassifier.Classify(latencyMs, warningThresholdMs, errorThresholdMs);
                return new PingLatencyProbeResult(
                    Id, status, $"{targetAddress} ({latencyMs:F0} ms)", sw.Elapsed, DateTimeOffset.UtcNow,
                    targetAddress, latencyMs);
            }

            return new PingLatencyProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Format("probe.gateway.unreachable", targetAddress), sw.Elapsed, DateTimeOffset.UtcNow,
                targetAddress, null, $"Ping status: {reply.Status}");
        }
        catch (Exception ex) when (ex is PingException or OperationCanceledException)
        {
            sw.Stop();
            return new PingLatencyProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Format("probe.gateway.unreachable", targetAddress), sw.Elapsed, DateTimeOffset.UtcNow,
                targetAddress, null, ex.Message);
        }
    }
}
