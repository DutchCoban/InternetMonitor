using System.Net;
using System.Runtime.InteropServices;

namespace InternetMonitor.Net;

/// <summary>
/// Reads the IPv4 ARP cache (Windows calls it the "IP-to-physical-address" or "neighbor" table),
/// to answer "what MAC address goes with this IP" for the LAN scanner - a ping sweep alone never
/// sees a MAC address, since ICMP never carries one.
///
/// Uses the older GetIpNetTable/MIB_IPNETROW rather than the newer GetIpNetTable2/MIB_IPNET_ROW2,
/// for the same reason <see cref="IfTable"/> prefers GetIfEntry over GetIfEntry2: MIB_IPNETROW is
/// a small flat struct with no bitfields or unions, fully describable with safe marshalling
/// attributes, where MIB_IPNET_ROW2 packs its neighbor state into a bitfield union that is easy to
/// get subtly wrong. The trade-off is the classic two-call "ask for the required size, then ask
/// again with a buffer that size" pattern GetIpNetTable2 would have avoided - worth it here for a
/// struct this easy to get right.
/// </summary>
public static class ArpTable
{
    private const int NoError = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaxlenPhysaddr = 8;

    /// <summary>Entries with this type are stale/pending removal, not a real neighbor - see MIB_IPNETROW.dwType.</summary>
    private const int TypeInvalid = 2;

    public static IReadOnlyDictionary<IPAddress, string> Snapshot()
    {
        try
        {
            return Read();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or OutOfMemoryException)
        {
            return new Dictionary<IPAddress, string>();
        }
    }

    private static IReadOnlyDictionary<IPAddress, string> Read()
    {
        // First call with no buffer only fills in the size needed (it returns "insufficient buffer").
        int size = 0;
        _ = GetIpNetTable(IntPtr.Zero, ref size, false);

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != NoError)
            {
                return new Dictionary<IPAddress, string>();
            }

            // MIB_IPNETTABLE: a DWORD entry count, then the rows back to back.
            int numEntries = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibIpNetRow>();
            IntPtr rowsStart = buffer + 4;

            var result = new Dictionary<IPAddress, string>();
            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(rowsStart + (i * rowSize));
                if (row.dwType == TypeInvalid || row.dwPhysAddrLen == 0)
                {
                    continue;
                }

                var address = new IPAddress(row.bAddr);
                string mac = string.Join(':', row.bPhysAddr.Take((int)row.dwPhysAddrLen).Select(b => b.ToString("X2")));
                result[address] = mac;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    /// <summary>
    /// MIB_IPNETROW. bPhysAddr and bAddr are read as raw byte blobs rather than typed fields
    /// (DWORD/BYTE[MAXLEN_PHYSADDR] in the native struct) specifically to sidestep endianness -
    /// an IPv4 address's four octets are stored in the order they'd appear on the wire either way,
    /// so reading them as bytes and handing them straight to <see cref="IPAddress(byte[])"/> (which
    /// expects exactly that order) needs no byte-swapping logic to get right.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public uint dwIndex;
        public uint dwPhysAddrLen;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxlenPhysaddr)]
        public byte[] bPhysAddr;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] bAddr;

        public uint dwType;
    }
}
