using System.Diagnostics;
using System.Net.NetworkInformation;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record PingEndpointResult(string Address, bool Reachable, double? LatencyMs);

public sealed record InternetProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<PingEndpointResult> Endpoints,
    int ReachableCount,
    string? ErrorDetail = null) : IProbeResult
{
    /// <summary>Reachable-endpoint count (0-3), not a latency - still a useful trend to chart.</summary>
    public double? ChartValue => ReachableCount;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string> { ["ReachableCount"] = $"{ReachableCount}/{Endpoints.Count}" };
        foreach (PingEndpointResult e in Endpoints)
        {
            d[e.Address] = e.Reachable ? $"OK ({e.LatencyMs:F0} ms)" : "TIMEOUT";
        }
        return d;
    }
}

/// <summary>
/// Pings multiple well-known external IPs (never just one) so a single endpoint's outage
/// doesn't get misread as "internet is down". Thresholds: 3/3 and 2/3 -&gt; Ok (one flaky
/// endpoint doesn't mean a real problem), 1/3 -&gt; Warning (doubtful/partial), 0/3 -&gt; Error.
/// </summary>
public sealed class InternetProbe : IProbe
{
    public string Id => "internet";
    public string Category => "Network";

    private static readonly string[] Endpoints = ["9.9.9.9", "8.8.8.8", "1.1.1.1"];
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    public async Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var results = new List<PingEndpointResult>();

        foreach (string address in Endpoints)
        {
            results.Add(await PingOneAsync(address, cancellationToken).ConfigureAwait(false));
        }

        sw.Stop();
        int reachableCount = results.Count(r => r.Reachable);

        // Plain language in the summary for non-technical users - the exact X/3 count is still
        // available in ToDetails() for anyone who wants to dig deeper.
        LocalizationManager loc = LocalizationManager.Instance;
        (ProbeStatus status, string summary) = reachableCount switch
        {
            3 => (ProbeStatus.Ok, loc.Get("probe.internet.reachable")),
            2 => (ProbeStatus.Ok, loc.Get("probe.internet.reachablePartial")),
            1 => (ProbeStatus.Warning, loc.Get("probe.internet.doubtful")),
            _ => (ProbeStatus.Error, loc.Get("probe.internet.unreachable")),
        };

        return new InternetProbeResult(Id, status, summary, sw.Elapsed, DateTimeOffset.UtcNow, results, reachableCount);
    }

    private static async Task<PingEndpointResult> PingOneAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            PingReply reply = await ping.SendPingAsync(address, (int)PingTimeout.TotalMilliseconds).WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success
                ? new PingEndpointResult(address, true, reply.RoundtripTime)
                : new PingEndpointResult(address, false, null);
        }
        catch (Exception ex) when (ex is PingException or OperationCanceledException)
        {
            return new PingEndpointResult(address, false, null);
        }
    }
}
