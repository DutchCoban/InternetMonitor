using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using InternetMonitor.Configuration;

namespace InternetMonitor.Net;

/// <summary>What <see cref="InterfaceSelector.Choose"/> decided: an adapter id, automatic selection (null), or a fixed adapter that is not there.</summary>
public sealed record AdapterChoice(string? Id, bool MissingFixed);

/// <summary>
/// Decides which adapter the whole pipeline is talking about, so steps 1 to 4 never end up
/// describing different pieces of hardware.
///
/// The primary answer comes from Windows itself: GetBestInterface asks the routing table which
/// interface would be used to reach a public address. It sends no packets - it is a table lookup -
/// and it is right in the cases a heuristic gets wrong, which on a developer machine means a VPN
/// client, a Hyper-V switch, a Docker bridge or a VMware adapter all being Up at once.
///
/// The fallback matters more in v2 than it did in v1. When the cable is unplugged there is no
/// route at all, so GetBestInterface fails - and that is precisely the case step 2 exists to
/// report. So the fallback deliberately considers adapters that are *down*, ranking by how likely
/// each is to be the user's real network card, rather than returning nothing and letting the
/// pipeline claim there is no adapter at all.
///
/// Enumerating the adapters (<see cref="GetAll"/>) is the one expensive call here - a trip through
/// GetAdaptersAddresses that builds a fresh object per adapter - so every method takes the list a
/// caller already has rather than enumerating again. A pipeline cycle enumerates once and hands
/// that list to everything that needs it.
/// </summary>
public static class InterfaceSelector
{
    // A routing-table lookup target, not a destination: nothing is sent to it.
    private static readonly uint RouteLookupDestination =
        BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes(), 0);

    /// <summary>
    /// Adapter descriptions that mark a virtual or software interface. Used only to rank the
    /// fallback, never to exclude: a machine really can have its only connection over one of
    /// these, and silently refusing to monitor it would be worse than ranking it last.
    /// </summary>
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "vmware", "hyper-v", "vethernet", "virtualbox", "docker",
        "loopback", "tap-", "tun", "wintun", "wireguard", "openvpn", "pseudo",
    ];

    private const int RouteLookupAttempts = 3;

    private static readonly TimeSpan RouteLookupRetryDelay = TimeSpan.FromMilliseconds(30);

    /// <summary>Every adapter Windows knows, or none when the enumeration itself fails.</summary>
    public static IReadOnlyList<NetworkInterface> GetAll()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>Every adapter that could plausibly carry real traffic: physical ones, in no particular order.</summary>
    public static IReadOnlyList<NetworkInterface> Candidates(IReadOnlyList<NetworkInterface> all) =>
    [
        .. all.Where(nic =>
            nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel),
    ];

    public static IReadOnlyList<NetworkInterface> GetCandidates() => Candidates(GetAll());

    /// <summary>
    /// The adapter Windows would route internet traffic over, or null when there is no route -
    /// which usually means no usable connection at all.
    ///
    /// Retried briefly before giving up: the route lookup itself is instant, but matching its
    /// index back to an adapter can fail for a moment while that adapter is being reconfigured
    /// (GetIPv4Properties throws mid-DHCP-renewal). Falling straight through to the heuristic
    /// on that one bad moment let a secondary adapter - a USB NIC plugged into a switch for
    /// configuration, with link but no internet - be described as the connection, which showed
    /// up as a false DNS failure. The first attempt uses the list the caller has; a retry
    /// enumerates afresh, since a stale snapshot is exactly what the retry is for.
    /// </summary>
    public static NetworkInterface? GetRouted(IReadOnlyList<NetworkInterface> all)
    {
        for (int attempt = 1; attempt <= RouteLookupAttempts; attempt++)
        {
            if (TryGetRoutedOnce(attempt == 1 ? all : GetAll()) is { } routed)
            {
                return routed;
            }

            if (attempt < RouteLookupAttempts)
            {
                Thread.Sleep(RouteLookupRetryDelay);
            }
        }

        return null;
    }

    public static NetworkInterface? GetRouted() => GetRouted(GetAll());

    /// <summary>The interface index Windows would route internet traffic over, or null when there is no route.</summary>
    public static uint? GetRoutedIndex()
    {
        try
        {
            return GetBestInterface(RouteLookupDestination, out uint index) == 0 ? index : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static NetworkInterface? TryGetRoutedOnce(IReadOnlyList<NetworkInterface> all)
    {
        if (GetRoutedIndex() is { } bestIndex)
        {
            return Candidates(all).FirstOrDefault(nic => IndexMatches(nic, bestIndex));
        }

        return null;
    }

    /// <summary>
    /// The adapter the status screen should describe: the routed one if there is a route, and
    /// otherwise the most plausible physical adapter even if it is down, so steps 1 and 2 can say
    /// *which* adapter has no link.
    /// </summary>
    public static NetworkInterface? GetPrimary(IReadOnlyList<NetworkInterface> all) => GetRouted(all) ?? GuessPhysical(all);

    /// <summary>
    /// The adapter the pipeline should describe under the user's adapter setting: the chosen one
    /// when the mode says so and it is there, automatic otherwise, and null for a fixed adapter
    /// that Windows no longer lists - which step 1 reports as exactly that.
    /// </summary>
    public static NetworkInterface? GetPrimary(IReadOnlyList<NetworkInterface> all, AdapterMode mode, string? adapterId)
    {
        AdapterChoice choice = Choose(all.Select(nic => (nic.Id, nic.OperationalStatus == OperationalStatus.Up)), mode, adapterId);
        if (choice.Id is { } id)
        {
            return all.First(nic => nic.Id == id);
        }

        return choice.MissingFixed ? null : GetPrimary(all);
    }

    /// <summary>The decision on its own, over ids and up/down flags, so it can be tested without real adapters.</summary>
    public static AdapterChoice Choose(IEnumerable<(string Id, bool Up)> adapters, AdapterMode mode, string? wantedId)
    {
        if (mode == AdapterMode.Automatic || string.IsNullOrEmpty(wantedId))
        {
            return new AdapterChoice(null, false);
        }

        (string Id, bool Up) wanted = adapters.FirstOrDefault(a => a.Id == wantedId);
        if (wanted.Id is null)
        {
            return new AdapterChoice(null, mode == AdapterMode.Fixed);
        }

        return mode == AdapterMode.Fixed || wanted.Up
            ? new AdapterChoice(wanted.Id, false)
            : new AdapterChoice(null, false);
    }

    public static NetworkInterface? GetPrimary() => GetPrimary(GetAll());

    /// <summary>The fallback ranking on its own: up before down, with a gateway before without, physical before virtual, fast before slow. Internal so the tests can rank made-up adapters.</summary>
    internal static NetworkInterface? GuessPhysical(IReadOnlyList<NetworkInterface> all) =>
        Candidates(all)
            .OrderByDescending(nic => nic.OperationalStatus == OperationalStatus.Up)
            .ThenByDescending(HasIPv4Gateway)
            .ThenBy(IsLikelyVirtual)
            .ThenByDescending(nic => nic.Speed)
            .FirstOrDefault();

    public static bool IsLikelyVirtual(NetworkInterface nic)
    {
        string haystack = $"{nic.Description} {nic.Name}";
        return VirtualMarkers.Any(marker => haystack.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasIPv4Gateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IsUnspecified(g.Address));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Windows lists a placeholder 0.0.0.0 gateway on adapters that have none. Treating that as a
    /// real gateway would have step 4 ping the unspecified address and report the resulting
    /// failure as "your router is unreachable", which is both wrong and alarming.
    /// </summary>
    public static bool IsUnspecified(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    private static bool IndexMatches(NetworkInterface nic, uint index)
    {
        try
        {
            return (uint)nic.GetIPProperties().GetIPv4Properties().Index == index;
        }
        catch (NetworkInformationException)
        {
            // No IPv4 bound, so it cannot have been the IPv4 best-route match.
            return false;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
}
