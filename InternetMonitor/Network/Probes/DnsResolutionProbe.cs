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
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => Status == ProbeStatus.Ok ? Duration.TotalMilliseconds : null;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string> { ["Hostname"] = Hostname };
        if (ResolvedAddress is not null) d["ResolvedAddress"] = ResolvedAddress;
        if (ConfiguredResolvers.Count > 0) d["ConfiguredResolvers"] = string.Join(", ", ConfiguredResolvers);
        return d;
    }
}

/// <summary>
/// Can a hostname actually be resolved? Reports the resolved address and the interface's
/// configured resolvers (the OS doesn't expose which specific server answered a normal
/// lookup, so the configured server list is the honest signal to show).
/// </summary>
public sealed class DnsResolutionProbe : IProbe
{
    public string Id => "dns";
    public string Category => "Network";

    private const string DefaultHostname = "www.ripe.net";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

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
                return new DnsResolutionProbeResult(
                    Id, ProbeStatus.Error, LocalizationManager.Instance.Get("probe.dns.noResult"), sw.Elapsed, DateTimeOffset.UtcNow,
                    DefaultHostname, null, resolvers, "DNS query returned no addresses");
            }

            return new DnsResolutionProbeResult(
                Id, ProbeStatus.Ok, $"{DefaultHostname} → {first}", sw.Elapsed, DateTimeOffset.UtcNow,
                DefaultHostname, first.ToString(), resolvers);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            sw.Stop();
            return new DnsResolutionProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Get("probe.dns.resolutionFailed"), sw.Elapsed, DateTimeOffset.UtcNow,
                DefaultHostname, null, resolvers, ex.Message);
        }
    }
}
