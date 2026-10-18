using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace InternetMonitor.Net;

/// <summary>
/// One adapter's address situation, for every adapter and not only the one the pipeline describes.
/// The step facts cover the primary adapter in depth; this is the shallow view of all of them that
/// <see cref="Core.Monitors.NetworkChangeMonitor"/> needs to notice a secondary adapter losing its
/// DHCP lease while Windows quietly routes around it.
/// </summary>
public sealed record AdapterReading(
    string Id,
    string Name,
    bool IsWireless,
    bool IsLikelyVirtual,
    bool Up,
    IPAddress? Address,
    IPAddress? Mask,
    bool IsApipa,
    bool DhcpEnabled,
    IPAddress? DhcpServer,
    IPAddress? Gateway)
{
    /// <summary>A usable address: present and not the 169.254 one Windows invents when DHCP stays silent.</summary>
    public bool HasRealAddress => Address is not null && !IsApipa;

    /// <summary>"192.168.1.0/24" - the network the address sits in, or null without an address or mask.</summary>
    public string? Network => Address is null || Mask is null || InterfaceSelector.IsUnspecified(Mask) ? null : NetworkOf(Address, Mask);

    public static string NetworkOf(IPAddress address, IPAddress mask)
    {
        byte[] a = address.GetAddressBytes();
        byte[] m = mask.GetAddressBytes();
        if (a.Length != 4 || m.Length != 4)
        {
            return address.ToString();
        }

        var network = new byte[4];
        int prefix = 0;
        for (int i = 0; i < 4; i++)
        {
            network[i] = (byte)(a[i] & m[i]);
            prefix += System.Numerics.BitOperations.PopCount(m[i]);
        }

        return $"{new IPAddress(network)}/{prefix}";
    }
}

/// <summary>Reads an <see cref="AdapterReading"/> for each candidate adapter - one GetIPProperties per adapter, nothing sent.</summary>
public static class AdapterInventory
{
    public static IReadOnlyList<AdapterReading> Read(IReadOnlyList<NetworkInterface> candidates) =>
        [.. candidates.Select(ReadOne)];

    public static AdapterReading ReadOne(NetworkInterface adapter)
    {
        IPInterfaceProperties? properties;
        try
        {
            properties = adapter.GetIPProperties();
        }
        catch (NetworkInformationException)
        {
            // A disabled adapter can have no IP stack bound at all.
            properties = null;
        }

        UnicastIPAddressInformation? unicast = properties?.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !InterfaceSelector.IsUnspecified(a.Address));
        IPAddress? gateway = properties?.GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !InterfaceSelector.IsUnspecified(a));
        IPAddress? dhcpServer = properties?.DhcpServerAddresses
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !InterfaceSelector.IsUnspecified(a));

        return new AdapterReading(
            adapter.Id,
            adapter.Name,
            adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
            InterfaceSelector.IsLikelyVirtual(adapter),
            adapter.OperationalStatus == OperationalStatus.Up,
            unicast?.Address,
            unicast?.IPv4Mask,
            unicast is not null && InterfaceSelector.IsApipa(unicast.Address),
            DhcpEnabled(properties),
            dhcpServer,
            gateway);
    }

    private static bool DhcpEnabled(IPInterfaceProperties? properties)
    {
        try
        {
            return properties?.GetIPv4Properties().IsDhcpEnabled ?? false;
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
