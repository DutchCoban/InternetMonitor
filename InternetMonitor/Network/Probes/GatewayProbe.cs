using System.Diagnostics;
using System.Net;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record GatewayProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string? GatewayAddress,
    double? LatencyMs,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => LatencyMs;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string>();
        if (GatewayAddress is not null) d["GatewayAddress"] = GatewayAddress;
        if (LatencyMs is not null) d["LatencyMs"] = LatencyMs.Value.ToString("F0");
        return d;
    }
}

/// <summary>
/// Is the default gateway known, and is it actually reachable? "No gateway discoverable at
/// all" and "gateway known but unreachable" are reported as distinct summaries/errors, per the
/// requirement that a missing gateway be its own separate cause.
/// </summary>
public sealed class GatewayProbe : IProbe
{
    public string Id => "gateway";
    public string Category => "Network";

    public async Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        IPAddress? gateway = GatewayReachability.DiscoverDefaultGateway();

        if (gateway is null)
        {
            sw.Stop();
            return new GatewayProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Get("probe.gateway.unknown"), sw.Elapsed, DateTimeOffset.UtcNow,
                null, null, "No default gateway found in routing table");
        }

        (bool reachable, TimeSpan? latency) = await GatewayReachability.CheckAsync(gateway, cancellationToken).ConfigureAwait(false);
        sw.Stop();

        if (!reachable)
        {
            return new GatewayProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Format("probe.gateway.unreachable", gateway), sw.Elapsed, DateTimeOffset.UtcNow,
                gateway.ToString(), null, "Gateway did not respond on any probed port");
        }

        double latencyMs = latency!.Value.TotalMilliseconds;
        return new GatewayProbeResult(
            Id, ProbeStatus.Ok, $"{gateway} ({latencyMs:F0} ms)", sw.Elapsed, DateTimeOffset.UtcNow,
            gateway.ToString(), latencyMs);
    }
}
