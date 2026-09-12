using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace InternetMonitor.Network.Probes;

/// <summary>
/// Picks "the" active network interface so NetworkInterfaceProbe, IpAddressProbe and
/// GatewayProbe all agree on which adapter they're describing. Heuristic: prefer an
/// operational, non-loopback/tunnel interface that owns an IPv4 default gateway, then the
/// fastest link.
/// </summary>
internal static class ActiveInterfaceSelector
{
    // NetworkInterfaceProbe, IpAddressProbe and DnsResolutionProbe all call into this class in
    // the same poll cycle (they run concurrently via Task.WhenAll), so without this cache the OS
    // would be asked to enumerate every network interface 3 separate times a few milliseconds
    // apart for no benefit - the adapter list can't meaningfully change within that window.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
    private static readonly object CacheLock = new();
    private static NetworkInterface[]? _cachedInterfaces;
    private static DateTime _cachedAtUtc;

    public static NetworkInterface? GetActive() =>
        GetAllInterfaces()
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                && nic.OperationalStatus == OperationalStatus.Up)
            .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(nic => nic.Speed)
            .FirstOrDefault();

    public static IEnumerable<NetworkInterface> GetAllNonVirtual() =>
        GetAllInterfaces()
            .Where(nic => nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

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
