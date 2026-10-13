using System.Runtime.InteropServices;

namespace InternetMonitor.Net;

/// <summary>
/// Reads the machine-wide count of ICMP Time Exceeded messages this host has received, from the IP
/// Helper API. Like <see cref="TcpStats"/>, this is a running total since boot - a caller wanting a
/// rate has to poll on an interval and diff two readings.
///
/// "Time Exceeded received" is the one TTL-related thing actually observable this way: it is what
/// a router sends back when a packet's TTL hits zero in transit, which is also exactly what this
/// app's own traceroute deliberately provokes (see Net/IcmpPing.SendWithTtlAsync) - running a
/// traceroute while a condition built on this counter is active will move it. Inspecting the TTL
/// field of arbitrary incoming packets (a different, broader notion of "TTL errors") is not
/// something any counter-based API exposes; that needs packet capture.
/// </summary>
public static class IcmpStats
{
    private const int NoError = 0;

    /// <summary>The total number of ICMP Time Exceeded messages received since the counter last reset, or null if it could not be read.</summary>
    public static long? TimeExceededReceived()
    {
        try
        {
            var stats = default(MibIcmp);
            return GetIcmpStatistics(ref stats) == NoError ? stats.InStats.dwTimeExcds : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetIcmpStatistics(ref MibIcmp pStats);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibIcmpStats
    {
        public uint dwMsgs;
        public uint dwErrors;
        public uint dwDestUnreachs;
        public uint dwTimeExcds;
        public uint dwParmProbs;
        public uint dwSrcQuenchs;
        public uint dwRedirects;
        public uint dwEchos;
        public uint dwEchoReps;
        public uint dwTimestamps;
        public uint dwTimestampReps;
        public uint dwAddrMasks;
        public uint dwAddrMaskReps;
    }

    /// <summary>MIB_ICMP (via MIBICMPINFO) - both in/out halves declared even though only InStats is read, same reasoning as MibTcpStats.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIcmp
    {
        public MibIcmpStats InStats;
        public MibIcmpStats OutStats;
    }
}
