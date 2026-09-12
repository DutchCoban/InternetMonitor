using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record DnsResolutionProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string Hostname,
    string? ResolvedAddress,
    IReadOnlyList<string> ConfiguredResolvers,
    bool? PublicResolversResolved = null,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => Status == ProbeStatus.Ok ? Duration.TotalMilliseconds : null;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string> { ["Hostname"] = Hostname };
        if (ResolvedAddress is not null) d["ResolvedAddress"] = ResolvedAddress;
        if (ConfiguredResolvers.Count > 0) d["ConfiguredResolvers"] = string.Join(", ", ConfiguredResolvers);
        if (PublicResolversResolved is not null) d["PublicResolversResolved"] = PublicResolversResolved.Value.ToString();
        return d;
    }
}

/// <summary>
/// Can a hostname actually be resolved? Reports the resolved address and the interface's
/// configured resolvers (the OS doesn't expose which specific server answered a normal lookup,
/// so the configured server list is the honest signal to show - this is a permanent limitation,
/// not something the cross-check below works around).
///
/// When the normal system lookup fails, this additionally cross-checks the same hostname
/// directly against a few well-known public resolvers (bypassing the OS resolver entirely, via
/// <see cref="PublicDnsProbe"/>) to tell apart two very different problems that would otherwise
/// look identical: "my configured DNS server specifically is broken" (public resolvers still
/// resolve fine) vs. "DNS/the network is broken more generally" (they fail too). This only runs
/// on failure - no added cost on the common healthy path.
/// </summary>
public sealed class DnsResolutionProbe : IProbe
{
    public string Id => "dns";
    public string Category => "Network";

    private const string DefaultHostname = "www.ripe.net";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PublicResolverTimeout = TimeSpan.FromSeconds(2);
    private static readonly IPAddress[] PublicResolvers =
    [
        IPAddress.Parse("9.9.9.9"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("1.1.1.1"),
    ];

    public async Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var resolvers = ActiveInterfaceSelector.GetActive()?
            .GetIPProperties().DnsAddresses
            .Select(a => a.ToString())
            .ToList() ?? [];

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(DefaultHostname, cts.Token).ConfigureAwait(false);
            sw.Stop();
            IPAddress? first = addresses.FirstOrDefault();
            if (first is null)
            {
                bool publicResolversOk = await AnyPublicResolverWorksAsync(cancellationToken).ConfigureAwait(false);
                return new DnsResolutionProbeResult(
                    Id, ProbeStatus.Error, SummaryFor(publicResolversOk, "probe.dns.noResult"), sw.Elapsed, DateTimeOffset.UtcNow,
                    DefaultHostname, null, resolvers, publicResolversOk, "DNS query returned no addresses");
            }

            return new DnsResolutionProbeResult(
                Id, ProbeStatus.Ok, $"{DefaultHostname} → {first}", sw.Elapsed, DateTimeOffset.UtcNow,
                DefaultHostname, first.ToString(), resolvers);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            sw.Stop();
            bool publicResolversOk = await AnyPublicResolverWorksAsync(cancellationToken).ConfigureAwait(false);
            return new DnsResolutionProbeResult(
                Id, ProbeStatus.Error, SummaryFor(publicResolversOk, "probe.dns.resolutionFailed"), sw.Elapsed, DateTimeOffset.UtcNow,
                DefaultHostname, null, resolvers, publicResolversOk, ex.Message);
        }
    }

    private static string SummaryFor(bool publicResolversOk, string fallbackKey) =>
        publicResolversOk
            ? LocalizationManager.Instance.Get("probe.dns.systemResolverBroken")
            : LocalizationManager.Instance.Get(fallbackKey);

    private static async Task<bool> AnyPublicResolverWorksAsync(CancellationToken cancellationToken)
    {
        Task<bool>[] checks = PublicResolvers
            .Select(server => PublicDnsProbe.CanResolveAsync(server, DefaultHostname, PublicResolverTimeout, cancellationToken))
            .ToArray();
        bool[] results = await Task.WhenAll(checks).ConfigureAwait(false);
        return Array.Exists(results, ok => ok);
    }
}
