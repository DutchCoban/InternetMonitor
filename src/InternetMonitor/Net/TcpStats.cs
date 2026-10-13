using System.Runtime.InteropServices;

namespace InternetMonitor.Net;

/// <summary>
/// Reads the machine-wide TCP retransmit counter from the IP Helper API.
///
/// This is a running total since boot, not a per-connection or per-remote-host figure - there is
/// no Windows API that attributes a retransmit to "which conversation", short of packet capture.
/// A caller wanting "N retransmits in the last Y minutes" has to poll this on an interval and take
/// the difference between two readings itself; this class only ever answers "what is the counter
/// right now".
/// </summary>
public static class TcpStats
{
    private const int NoError = 0;
    private const int AfInet = 2;

    /// <summary>The total number of TCP segments retransmitted since the counter last reset (normally, since boot), or null if the counter could not be read.</summary>
    public static long? RetransmittedSegments()
    {
        try
        {
            var stats = default(MibTcpStats);
            return GetTcpStatisticsEx(ref stats, AfInet) == NoError ? stats.dwRetransSegs : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetTcpStatisticsEx(ref MibTcpStats pStats, int dwFamily);

    /// <summary>
    /// MIB_TCPSTATS. Every field is declared even though only one is read: GetTcpStatisticsEx writes
    /// the whole structure, so a short declaration would have it write past the end of the
    /// marshalled buffer - the same reasoning IfTable.MibIfRow documents for GetIfEntry.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpStats
    {
        public uint dwRtoAlgorithm;
        public uint dwRtoMin;
        public uint dwRtoMax;
        public uint dwMaxConn;
        public uint dwActiveOpens;
        public uint dwPassiveOpens;
        public uint dwAttemptFails;
        public uint dwEstabResets;
        public uint dwCurrEstab;
        public uint dwInSegs;
        public uint dwOutSegs;
        public uint dwRetransSegs;
        public uint dwInErrs;
        public uint dwOutRsts;
        public uint dwNumConns;
    }
}
