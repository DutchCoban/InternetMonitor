using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace InternetMonitor.Net;

/// <summary>What Windows says the internet connection costs, plus whether it is mobile broadband.</summary>
/// <param name="Metered">
/// Windows treats the connection as metered: a mobile data plan, a phone's hotspot (phones announce
/// themselves as metered), or a Wi-Fi network the user marked as metered. Null when Windows could
/// not say.
/// </param>
/// <param name="Mobile">The adapter itself is a cellular modem (WWAN), not Wi-Fi or Ethernet.</param>
/// <param name="Roaming">Windows reports the connection as roaming outside the plan's home network.</param>
/// <param name="OverLimit">Windows reports the data plan as over, or close to, its limit.</param>
public sealed record ConnectionCostInfo(bool? Metered, bool Mobile, bool Roaming, bool OverLimit);

/// <summary>
/// Asks Windows' Network List Manager whether the internet connection is metered - the same flag
/// Windows Update and OneDrive respect. Used to keep automatic speed tests off a data plan and to
/// read high latency on a mobile connection for what it is.
///
/// INetworkCostManager is COM, declared here by hand, in the same spirit as the P/Invoke elsewhere:
/// no interop package, three methods, only one of them used. Asked with no destination address, it
/// answers for the machine's internet connection as a whole, which is the one the checks use.
/// The answer is cached for a short while: it changes when the network does, and the pipeline asks
/// every few seconds.
/// </summary>
public static class ConnectionCost
{
    private const uint CostFixed = 0x2;
    private const uint CostVariable = 0x4;
    private const uint CostOverDataLimit = 0x10000;
    private const uint CostRoaming = 0x40000;
    private const uint CostApproachingDataLimit = 0x80000;

    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);
    private static readonly object Gate = new();

    private static (uint? Cost, DateTimeOffset At)? _cached;

    public static ConnectionCostInfo Query(NetworkInterface? adapter)
    {
        bool mobile = adapter?.NetworkInterfaceType is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2;
        uint? cost = CachedCost();
        if (cost is not { } flags || flags == 0)
        {
            // Unknown to Windows; a cellular modem is still metered for all practical purposes.
            return new ConnectionCostInfo(mobile ? true : null, mobile, false, false);
        }

        bool metered = (flags & (CostFixed | CostVariable | CostOverDataLimit | CostRoaming)) != 0;
        return new ConnectionCostInfo(
            metered || mobile,
            mobile,
            (flags & CostRoaming) != 0,
            (flags & (CostOverDataLimit | CostApproachingDataLimit)) != 0);
    }

    /// <summary>
    /// The cost flags, from the cache while it is fresh. The COM call runs outside the lock: two
    /// threads may both ask Windows at an expiry, which is harmless, rather than one waiting on the other.
    /// </summary>
    private static uint? CachedCost()
    {
        lock (Gate)
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - cached.At < CacheLifetime)
            {
                return cached.Cost;
            }
        }

        uint? cost = ReadCost();
        lock (Gate)
        {
            _cached = (cost, DateTimeOffset.UtcNow);
        }

        return cost;
    }

    /// <summary>
    /// Asks the Network List Manager once. Created and released per call rather than kept, so no COM
    /// object outlives the thread (and apartment) that made it. Null on any COM failure.
    /// </summary>
    private static uint? ReadCost()
    {
        object? manager = null;
        try
        {
            if (Type.GetTypeFromCLSID(NetworkListManagerClsid) is not { } type)
            {
                return null;
            }

            manager = Activator.CreateInstance(type);
            return manager is INetworkCostManager costs && costs.GetCost(out uint cost, IntPtr.Zero) == 0 ? cost : null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
            {
                Marshal.ReleaseComObject(manager);
            }
        }
    }

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        [PreserveSig]
        int GetCost(out uint pCost, IntPtr pDestIPAddr);

        [PreserveSig]
        int GetDataPlanStatus(IntPtr pDataPlanStatus, IntPtr pDestIPAddr);

        [PreserveSig]
        int SetDestinationAddresses(uint length, IntPtr pDestIPAddrList, [MarshalAs(UnmanagedType.VariantBool)] bool bAppend);
    }
}
