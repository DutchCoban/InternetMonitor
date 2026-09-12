using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace InternetMonitor.Network.Probes;

/// <summary>
/// Picks "the" active network interface so NetworkInterfaceProbe, IpAddressProbe,
/// DnsResolutionProbe and GatewayReachability all agree on which adapter they're describing.
/// Rather than guessing via a heuristic (which can disagree with itself, or with an
/// independently-guessing caller, on a machine with more than one active adapter - a VPN client
/// or a Docker/Hyper-V/VMware virtual switch are common and easy to forget about), this asks
/// Windows directly which interface it would actually use to reach the internet, via the
/// GetBestInterface routing-table lookup (iphlpapi.dll - a plain lookup, no packets sent). .NET
/// has no managed equivalent, so this is a small P/Invoke, in the same spirit as the other
/// hand-rolled OS/protocol bits already in this codebase (NativeMethods.DestroyIcon, the SNTP
/// client in TimeSyncProbe, the DNS-over-UDP client in PublicDnsProbe). Falls back to the old
/// heuristic (prefer an Up, non-loopback/tunnel interface with an IPv4 gateway, then the fastest
/// link) only if the API call fails or its answer can't be matched to an interface - so this is
/// never worse than the previous behavior, just more often correct.
/// </summary>
internal static class ActiveInterfaceSelector
{
    // A routing-table lookup, not a real destination - GetBestInterface never sends a packet.
    private static readonly uint WellKnownDestination = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);

    // NetworkInterfaceProbe, IpAddressProbe and DnsResolutionProbe all call into this class in
    // the same poll cycle (they run concurrently via Task.WhenAll), so without this cache the OS
    // would be asked to enumerate every network interface 3 separate times a few milliseconds
    // apart for no benefit - the adapter list can't meaningfully change within that window.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
    private static readonly object CacheLock = new();
    private static NetworkInterface[]? _cachedInterfaces;
    private static DateTime _cachedAtUtc;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);

    public static NetworkInterface? GetActive()
    {
        NetworkInterface[] all = GetAllInterfaces();

        if (GetBestInterface(WellKnownDestination, out uint bestIfIndex) == 0)
        {
            NetworkInterface? byRoute = all.FirstOrDefault(nic => IndexMatches(nic, bestIfIndex));
            if (byRoute is not null)
            {
                return byRoute;
            }
        }

        // Fallback: GetBestInterface failed (e.g. no route at all) or its answer didn't match any
        // enumerated interface - use the previous heuristic rather than reporting nothing.
        return all
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                && nic.OperationalStatus == OperationalStatus.Up)
            .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(nic => nic.Speed)
            .FirstOrDefault();
    }

    public static IEnumerable<NetworkInterface> GetAllNonVirtual() =>
        GetAllInterfaces()
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

    private static bool IndexMatches(NetworkInterface nic, uint index)
    {
        try
        {
            return (uint)nic.GetIPProperties().GetIPv4Properties().Index == index;
        }
        catch (NetworkInformationException)
        {
            // IPv4 not enabled on this interface - can't have been the IPv4 best-route match.
            return false;
        }
    }

    private static NetworkInterface[] GetAllInterfaces()
    {
        lock (CacheLock)
        {
            DateTime now = DateTime.UtcNow;
            if (_cachedInterfaces is null || now - _cachedAtUtc > CacheDuration)
            {
                _cachedInterfaces = NetworkInterface.GetAllNetworkInterfaces();
                _cachedAtUtc = now;
            }

            return _cachedInterfaces;
        }
    }
}
