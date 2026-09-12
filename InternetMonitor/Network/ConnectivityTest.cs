using System.Diagnostics;

namespace InternetMonitor.Network;

/// <summary>
/// Runs the multi-signal connectivity probe: DNS resolution plus a cascade of small HTTPS
/// requests. Success requires either a resolvable DNS hostname or a reachable HTTPS endpoint,
/// so a single blocked signal (e.g. DNS filtering, or one endpoint being down) never by itself
/// produces a false "no internet" result.
/// </summary>
public sealed class ConnectivityTest
{
    // European-first: RIPE NCC (Netherlands) for DNS, Volla (Germany) as the primary 204 check,
    // Kuketz (Germany) as fallback - both verified live to return a fast, validly-certified 204
    // over HTTPS. No US-based service is used here.
    private const string DnsProbeHostname = "www.ripe.net";
    private const string PrimaryHttpEndpoint = "https://connectivitycheck.volla.tech/generate_204";
    private const string FallbackHttpEndpoint = "https://captiveportal.kuketz.de/generate_204";
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _httpClient;

    public ConnectivityTest(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ConnectivityCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // DNS is independent of the HTTP cascade below, so start it immediately rather than
        // after - this runs once a second, so folding its latency into the HTTP cascade instead
        // of adding it afterward measurably speeds up how fast a state change is detected.
        Task<bool> dnsTask = DnsProbe.CheckAsync(DnsProbeHostname, DnsTimeout, cancellationToken);

        bool httpOk = await HttpProbe.CheckAsync(_httpClient, PrimaryHttpEndpoint, HttpTimeout, cancellationToken).ConfigureAwait(false);
        if (!httpOk)
        {
            httpOk = await HttpProbe.CheckAsync(_httpClient, FallbackHttpEndpoint, HttpTimeout, cancellationToken).ConfigureAwait(false);
        }

        bool dnsOk = await dnsTask.ConfigureAwait(false);

        stopwatch.Stop();
        return new ConnectivityCheckResult(httpOk || dnsOk, stopwatch.Elapsed);
    }
}
