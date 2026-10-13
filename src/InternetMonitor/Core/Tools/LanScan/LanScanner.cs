using System.Net;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Tools.LanScan;

public sealed record LanScanResult(
    IReadOnlyList<LanDevice> Devices,
    int AddressesScanned,
    bool Truncated,
    DateTimeOffset StartedUtc,
    TimeSpan Duration);

/// <summary>
/// A simplified Advanced-IP-Scanner-style sweep of the local subnet: which addresses answer a
/// ping, with whatever hostname, MAC address and manufacturer can be found for each (the last via
/// the optional <see cref="MacVendorLookup"/>). No port scanning - this answers "what's on my
/// network", not "what's running on it".
///
/// Shaped like <see cref="Tools.Traceroute.TracerouteRunner"/>: the fast part (the ping sweep
/// itself) runs first and in full before anything is reported, then the slower per-host
/// enrichment (reverse DNS; the ARP table is a single snapshot, not per-host) streams results back
/// through <see cref="IProgress{T}"/> as each one finishes, so the caller's list fills in instead
/// of blocking on the slowest hostname lookup in the whole subnet.
/// </summary>
public sealed class LanScanner
{
    /// <summary>
    /// Hard cap on how many addresses one scan probes - the same kind of bound
    /// <see cref="Tools.Traceroute.TracerouteRunner"/> puts on hop count, here because a /16 or
    /// wider network has tens of thousands of addresses and nothing about this feature should ever
    /// take minutes. 254 covers a full /24, the common case for a home or small-office LAN.
    /// </summary>
    private const int MaxHosts = 254;

    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ReverseDnsTimeout = TimeSpan.FromSeconds(2);

    public async Task<LanScanResult> ScanAsync(
        IPAddress localAddress,
        IPAddress subnetMask,
        IProgress<LanDevice>? progress,
        CancellationToken cancellationToken,
        MacVendorLookup? vendorLookup = null)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        long startTicks = Environment.TickCount64;

        (List<IPAddress> addresses, bool truncated) = EnumerateHosts(localAddress, subnetMask);
        if (addresses.Count == 0)
        {
            return new LanScanResult([], 0, false, started, TimeSpan.Zero);
        }

        IReadOnlyList<(IPAddress Address, PingOutcome Outcome)> pingResults = await IcmpPing
            .SendAllAsync(addresses, PingTimeout, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyDictionary<IPAddress, string> arp = ArpTable.Snapshot();

        var devices = new List<LanDevice>();
        var enrichTasks = new List<Task>();

        foreach ((IPAddress address, PingOutcome outcome) in pingResults)
        {
            if (!outcome.Success)
            {
                continue;
            }

            arp.TryGetValue(address, out string? mac);
            enrichTasks.Add(EnrichAndReportAsync(address, outcome.RoundtripMs, mac, devices, progress, vendorLookup, cancellationToken));
        }

        try
        {
            await Task.WhenAll(enrichTasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Enrichment is decoration, same reasoning as TracerouteRunner's own enrich step - a
            // device that could not be named still belongs in the result.
        }

        return new LanScanResult(
            devices,
            addresses.Count,
            truncated,
            started,
            TimeSpan.FromMilliseconds(Environment.TickCount64 - startTicks));
    }

    private static async Task EnrichAndReportAsync(
        IPAddress address,
        double? roundtripMs,
        string? mac,
        List<LanDevice> devices,
        IProgress<LanDevice>? progress,
        MacVendorLookup? vendorLookup,
        CancellationToken cancellationToken)
    {
        // Reverse DNS and the vendor lookup run side by side rather than one after the other -
        // neither depends on the other's answer, and a device would otherwise wait out both
        // lookups' latency in sequence instead of just the slower of the two.
        Task<string?> hostnameTask = DnsResolve.TryReverseAsync(address, ReverseDnsTimeout, cancellationToken);
        Task<string?> vendorTask = mac is not null && vendorLookup is not null
            ? vendorLookup.LookupAsync(mac, cancellationToken)
            : Task.FromResult<string?>(null);

        await Task.WhenAll(hostnameTask, vendorTask).ConfigureAwait(false);

        var device = new LanDevice(address, hostnameTask.Result, mac, roundtripMs, vendorTask.Result);

        lock (devices)
        {
            devices.Add(device);
        }

        progress?.Report(device);
    }

    /// <summary>
    /// The usable host range of <paramref name="localAddress"/>'s own subnet - everything between
    /// the network and broadcast addresses, exclusive - capped at <see cref="MaxHosts"/>.
    /// </summary>
    private static (List<IPAddress> Addresses, bool Truncated) EnumerateHosts(IPAddress localAddress, IPAddress subnetMask)
    {
        byte[] addressBytes = localAddress.GetAddressBytes();
        byte[] maskBytes = subnetMask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            return ([], false);
        }

        uint address = ToUInt32(addressBytes);
        uint mask = ToUInt32(maskBytes);
        uint network = address & mask;
        uint broadcast = network | ~mask;

        long firstHost = network + 1L;
        long lastHost = broadcast - 1L;
        if (lastHost < firstHost)
        {
            return ([], false);
        }

        long totalHosts = lastHost - firstHost + 1;
        bool truncated = totalHosts > MaxHosts;
        long cappedLast = truncated ? firstHost + MaxHosts - 1 : lastHost;

        var addresses = new List<IPAddress>();
        for (long value = firstHost; value <= cappedLast; value++)
        {
            addresses.Add(FromUInt32((uint)value));
        }

        return (addresses, truncated);
    }

    private static uint ToUInt32(byte[] bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
