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
    string? Method = null,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => LatencyMs;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string>();
        if (GatewayAddress is not null) d["GatewayAddress"] = GatewayAddress;
        if (LatencyMs is not null) d["LatencyMs"] = LatencyMs.Value.ToString("F0");
        if (Method is not null) d["Method"] = Method;
        return d;
    }
}

/// <summary>
/// Is the default gateway known, and is it actually reachable? "No gateway discoverable at
/// all" and "gateway known but unreachable" are reported as distinct summaries/errors, per the
/// requirement that a missing gateway be its own separate cause.
///
/// Deliberately no retry here, even though the gateway is confirmed to genuinely go unreachable
/// for a few seconds whenever a network settings change is applied (verified live: a plain
/// `ping` from outside this app failed for ~4s during the same transition). That's not a
/// measurement error to hide - it's the real, momentary state of the machine, and this is a
/// monitoring tool: reporting what's actually true right now takes priority over smoothing a
/// result into something more convenient to look at.
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
            return new GatewayProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Get("probe.gateway.unknown"), sw.Elapsed, DateTimeOffset.UtcNow,
                null, null, ErrorDetail: "No default gateway found in routing table");
        }

        (bool reachable, TimeSpan? latency, string? method) = await GatewayReachability.CheckAsync(gateway, cancellationToken).ConfigureAwait(false);

        if (!reachable)
        {
            return new GatewayProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Format("probe.gateway.unreachable", gateway), sw.Elapsed, DateTimeOffset.UtcNow,
                gateway.ToString(), null, ErrorDetail: "Gateway did not respond to ICMP or any probed port");
        }

        double latencyMs = latency!.Value.TotalMilliseconds;
        return new GatewayProbeResult(
            Id, ProbeStatus.Ok, $"{gateway} ({latencyMs:F0} ms, {method})", sw.Elapsed, DateTimeOffset.UtcNow,
            gateway.ToString(), latencyMs, method);
    }
}
