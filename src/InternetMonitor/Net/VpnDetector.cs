using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace InternetMonitor.Net;

/// <summary>An active VPN connection.</summary>
/// <param name="Name">The adapter's name, for the status screen only - never written to a report or export.</param>
/// <param name="FullTunnel">
/// True when Windows routes internet traffic through the VPN (everything goes through it); false
/// when only some destinations do (split tunnel), so the checks still measure the line directly.
/// </param>
public sealed record VpnInfo(string Name, bool FullTunnel);

/// <summary>
/// Notices an active VPN, because it changes what the checks measure: with all traffic going
/// through a tunnel, the latency is that of the VPN server's route, DNS is often the VPN's own,
/// and a VPN connecting or dropping is exactly the kind of change that explains an incident. A
/// user staring at "high latency" deserves to be told a VPN is in the way.
///
/// Recognition is by adapter type (PPP, or a tunnel) and by the names the common clients give their
/// adapters. Windows' own IPv6 transition tunnels (Teredo, 6to4, ISATAP, IP-HTTPS) are tunnels too,
/// but carry no VPN, so they are excluded by name. Only adapters that are up and have an IPv4
/// address count - a VPN client installed but not connected leaves its adapter down.
/// </summary>
public static class VpnDetector
{
    private static readonly string[] VpnMarkers =
    [
        "vpn", "wireguard", "wintun", "openvpn", "tap-windows", "anyconnect", "fortinet", "forticlient",
        "globalprotect", "pangp", "nordlynx", "juniper", "pulse secure", "zscaler", "tailscale",
        "zerotier", "cloudflare warp", "proton", "sonicwall", "checkpoint", "f5 ",
    ];

    private static readonly string[] TransitionTunnelMarkers = ["teredo", "6to4", "isatap", "ip-https"];

    public static VpnInfo? Detect()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        uint? routedIndex = InterfaceSelector.GetRoutedIndex();
        VpnInfo? found = null;
        foreach (NetworkInterface nic in all)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || !LooksLikeVpn(nic.NetworkInterfaceType, nic.Name, nic.Description) || Ipv4Index(nic) is not { } index)
            {
                continue;
            }

            var vpn = new VpnInfo(nic.Name, routedIndex == index);
            if (vpn.FullTunnel)
            {
                return vpn;
            }

            found ??= vpn;
        }

        return found;
    }

    /// <summary>The recognition rule on its own, for tests.</summary>
    public static bool LooksLikeVpn(NetworkInterfaceType type, string name, string description)
    {
        string haystack = $"{name} {description}";
        if (TransitionTunnelMarkers.Any(m => haystack.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return type is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel
            || VpnMarkers.Any(m => haystack.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static uint? Ipv4Index(NetworkInterface nic)
    {
        try
        {
            IPInterfaceProperties properties = nic.GetIPProperties();
            if (!properties.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                return null;
            }

            return (uint)properties.GetIPv4Properties().Index;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
