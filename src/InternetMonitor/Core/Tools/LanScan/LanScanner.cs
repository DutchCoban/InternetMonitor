using System.Linq;
using System.Net;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Tools.LanScan;

public sealed record LanScanResult(
    IReadOnlyList<LanDevice> Devices,
    int AddressesScanned,
    bool Truncated,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    Task VendorResolutionComplete);

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
///
/// Manufacturer lookups are a further, third stage, deliberately split off from the above instead
/// of folded into the per-host enrichment: <see cref="MacVendorLookup"/> is rate-limited to be
/// polite to its two free APIs, so waiting for it before reporting a device would make every scan
/// as slow as its slowest vendor lookup. Instead <see cref="ScanAsync"/> returns as soon as ping +
/// ARP + hostname are done, and manufacturer names trickle in afterwards as a background task
/// (<see cref="LanScanResult.VendorResolutionComplete"/>) that keeps updating already-reported
/// devices through the same <see cref="IProgress{T}"/> channel.
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

    /// <summary>
    /// Candidate ports for the TCP fallback sweep (see <see cref="ScanAsync"/>'s <c>tcpFallback</c>
    /// parameter) - common enough services (web, SSH, SMB, NetBIOS) that most devices answer on at
    /// least one, without racing so many ports that a /24 of mostly-empty addresses takes
    /// noticeably longer to scan.
    /// </summary>
    private static readonly int[] TcpFallbackPorts = [80, 443, 22, 445, 139];

    private static readonly TimeSpan TcpFallbackTimeout = TimeSpan.FromMilliseconds(500);

    public async Task<LanScanResult> ScanAsync(
        IPAddress localAddress,
        IPAddress subnetMask,
        IProgress<LanDevice>? progress,
        CancellationToken cancellationToken,
        MacVendorLookup? vendorLookup = null,
        bool tcpFallback = false)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        long startTicks = Environment.TickCount64;

        (List<IPAddress> addresses, bool truncated) = EnumerateHosts(localAddress, subnetMask);
        if (addresses.Count == 0)
        {
            return new LanScanResult([], 0, false, started, TimeSpan.Zero, Task.CompletedTask);
        }

        IReadOnlyList<(IPAddress Address, PingOutcome Outcome)> pingResults = await IcmpPing
            .SendAllAsync(addresses, PingTimeout, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyDictionary<IPAddress, string> arp = ArpTable.Snapshot();

        var devices = new List<LanDevice>();
        var enrichTasks = new List<Task>();
        var silentAddresses = new List<IPAddress>();

        foreach ((IPAddress address, PingOutcome outcome) in pingResults)
        {
            if (!outcome.Success)
            {
                silentAddresses.Add(address);
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

        if (tcpFallback && silentAddresses.Count > 0)
        {
            await TcpFallbackSweepAsync(silentAddresses, arp, devices, progress, vendorLookup, cancellationToken).ConfigureAwait(false);
        }

        Task vendorResolutionComplete = ResolveVendorsAsync(devices, vendorLookup, progress, cancellationToken);

        return new LanScanResult(
            devices,
            addresses.Count,
            truncated,
            started,
            TimeSpan.FromMilliseconds(Environment.TickCount64 - startTicks),
            vendorResolutionComplete);
    }

    /// <summary>
    /// Second-chance discovery for addresses that did not answer a ping - a device that ignores
    /// ICMP but has an open port still belongs on the network map. Takes a fresh ARP snapshot
    /// rather than reusing the one from before the sweep: a silent address's entry only appears in
    /// the OS ARP cache once something - here, the TCP connection attempt itself - actually talks
    /// to it, so the earlier snapshot would find nothing for exactly the devices this is for.
    /// </summary>
    private static async Task TcpFallbackSweepAsync(
        List<IPAddress> silentAddresses,
        IReadOnlyDictionary<IPAddress, string> pingArp,
        List<LanDevice> devices,
        IProgress<LanDevice>? progress,
        MacVendorLookup? vendorLookup,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(IPAddress Address, (int Port, TcpOutcome Outcome)? Result)> results = await TcpConnect
            .SweepAsync(silentAddresses, TcpFallbackPorts, TcpFallbackTimeout, cancellationToken)
            .ConfigureAwait(false);

        List<(IPAddress Address, double? RoundtripMs)> answered = [.. results
            .Where(r => r.Result is { Outcome.Answered: true })
            .Select(r => (r.Address, r.Result!.Value.Outcome.ElapsedMs))];

        if (answered.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<IPAddress, string> arp = ArpTable.Snapshot();
        var enrichTasks = new List<Task>();
        foreach ((IPAddress address, double? roundtripMs) in answered)
        {
            if (!arp.TryGetValue(address, out string? mac))
            {
                pingArp.TryGetValue(address, out mac);
            }

            enrichTasks.Add(EnrichAndReportAsync(address, roundtripMs, mac, devices, progress, vendorLookup, cancellationToken));
        }

        try
        {
            await Task.WhenAll(enrichTasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same reasoning as the ping-sweep's own enrichment: decoration, not essential.
        }
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
        string? hostname = await DnsResolve.TryReverseAsync(address, ReverseDnsTimeout, cancellationToken).ConfigureAwait(false);
        bool vendorPending = mac is not null && vendorLookup is not null;
        var device = new LanDevice(address, hostname, mac, roundtripMs, Vendor: null, VendorPending: vendorPending);

        lock (devices)
        {
            devices.Add(device);
        }

        progress?.Report(device);
    }

    /// <summary>
    /// The third, slower stage - see the class doc comment. Runs after every device has already
    /// been reported once, looking up each one's manufacturer and reporting the same device again
    /// (now with <see cref="LanDevice.Vendor"/> filled in and <see cref="LanDevice.VendorPending"/>
    /// cleared) as each lookup finishes, instead of making the caller wait for all of them.
    /// </summary>
    private static async Task ResolveVendorsAsync(List<LanDevice> devices, MacVendorLookup? vendorLookup, IProgress<LanDevice>? progress, CancellationToken cancellationToken)
    {
        if (vendorLookup is null)
        {
            return;
        }

        LanDevice[] pending;
        lock (devices)
        {
            pending = devices.Where(d => d.VendorPending).ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        var vendorTasks = pending.Select(device => ResolveVendorAsync(device, vendorLookup, devices, progress, cancellationToken));
        await Task.WhenAll(vendorTasks).ConfigureAwait(false);
    }

    private static async Task ResolveVendorAsync(LanDevice device, MacVendorLookup vendorLookup, List<LanDevice> devices, IProgress<LanDevice>? progress, CancellationToken cancellationToken)
    {
        string? vendor = await vendorLookup.LookupAsync(device.MacAddress!, cancellationToken).ConfigureAwait(false);
        LanDevice updated = device with { Vendor = vendor, VendorPending = false };

        lock (devices)
        {
            int index = devices.FindIndex(d => d.Address.Equals(updated.Address));
            if (index >= 0)
            {
                devices[index] = updated;
            }
        }

        progress?.Report(updated);
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
