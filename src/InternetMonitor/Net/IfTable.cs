using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace InternetMonitor.Net;

/// <summary>Whether Windows considers the adapter administratively enabled.</summary>
public enum AdminStatus
{
    Unknown = 0,
    Enabled = 1,
    Disabled = 2,
}

/// <summary>
/// The adapter's physical link state, as reported by the interface table. This is the distinction
/// .NET's <see cref="OperationalStatus"/> cannot make: it reports a disabled adapter and an
/// adapter with its cable unplugged identically, as Down.
/// </summary>
public enum PhysicalLinkState
{
    Unknown = 0,
    NonOperational = 1,
    Unreachable = 2,

    /// <summary>No carrier: the cable is out, or the Wi-Fi radio is not associated with an AP.</summary>
    Disconnected = 3,

    Connecting = 4,
    Connected = 5,
    Operational = 6,
}

public readonly record struct InterfaceLinkInfo(AdminStatus Admin, PhysicalLinkState Link, long? SpeedBps)
{
    public bool HasCarrier => Link is PhysicalLinkState.Connected or PhysicalLinkState.Operational;
}

/// <summary>
/// Reads the Windows interface table for one adapter, to answer two questions .NET's
/// <see cref="NetworkInterface"/> cannot answer separately:
///
///   "Is the adapter switched on?"   -> dwAdminStatus
///   "Is anything plugged into it?"  -> dwOperStatus
///
/// Both come back as <see cref="OperationalStatus.Down"/> through the managed API, which is why v1
/// could only ever report "network adapter not active" and had no way to tell a disabled adapter
/// from an unplugged cable. Those are completely different problems with completely different
/// fixes, and in v2 they are steps 1 and 2.
///
/// This uses the older GetIfEntry/MIB_IFROW rather than GetIfEntry2/MIB_IF_ROW2 deliberately.
/// MIB_IF_ROW2 is roughly 1300 bytes across a dozen nested native types that would all have to be
/// declared and kept correct, and its MediaConnectState tells us nothing that dwOperStatus here
/// does not already say. The older structure is flat, fully describable with safe marshalling
/// attributes, and has been stable since Windows 2000.
/// </summary>
public static class IfTable
{
    private const int MaxInterfaceNameLen = 256;
    private const int MaxlenPhysaddr = 8;
    private const int MaxlenIfdescr = 256;
    private const int NoError = 0;

    public static InterfaceLinkInfo Query(NetworkInterface adapter)
    {
        try
        {
            IPv4InterfaceProperties? ipv4 = GetIPv4Properties(adapter);
            if (ipv4 is null)
            {
                return new InterfaceLinkInfo(AdminStatus.Unknown, PhysicalLinkState.Unknown, null);
            }

            var row = new MibIfRow { dwIndex = (uint)ipv4.Index };
            if (GetIfEntry(ref row) != NoError)
            {
                return new InterfaceLinkInfo(AdminStatus.Unknown, PhysicalLinkState.Unknown, null);
            }

            return new InterfaceLinkInfo(
                row.dwAdminStatus switch
                {
                    1 => AdminStatus.Enabled,
                    2 => AdminStatus.Disabled,
                    _ => AdminStatus.Unknown,
                },
                row.dwOperStatus switch
                {
                    0 => PhysicalLinkState.NonOperational,
                    1 => PhysicalLinkState.Unreachable,
                    2 => PhysicalLinkState.Disconnected,
                    3 => PhysicalLinkState.Connecting,
                    4 => PhysicalLinkState.Connected,
                    5 => PhysicalLinkState.Operational,
                    _ => PhysicalLinkState.Unknown,
                },
                row.dwSpeed > 0 ? row.dwSpeed : null);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or EntryPointNotFoundException or DllNotFoundException)
        {
            // Every caller treats Unknown as "fall back to what the managed API said", so losing
            // this detail degrades v2 to v1's behaviour rather than breaking the check.
            return new InterfaceLinkInfo(AdminStatus.Unknown, PhysicalLinkState.Unknown, null);
        }
    }

    private static IPv4InterfaceProperties? GetIPv4Properties(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties().GetIPv4Properties();
        }
        catch (NetworkInformationException)
        {
            // Thrown for adapters that have no IPv4 stack bound at all, which is a legitimate
            // state for an adapter that is present but disabled.
            return null;
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern int GetIfEntry(ref MibIfRow pIfRow);

    /// <summary>
    /// MIB_IFROW. Every field is declared even though only four are read: GetIfEntry writes the
    /// whole structure, so a short declaration would have it write past the end of the marshalled
    /// buffer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MibIfRow
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxInterfaceNameLen)]
        public string wszName;

        public uint dwIndex;
        public uint dwType;
        public uint dwMtu;
        public uint dwSpeed;
        public uint dwPhysAddrLen;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxlenPhysaddr)]
        public byte[] bPhysAddr;

        public uint dwAdminStatus;
        public uint dwOperStatus;
        public uint dwLastChange;
        public uint dwInOctets;
        public uint dwInUcastPkts;
        public uint dwInNUcastPkts;
        public uint dwInDiscards;
        public uint dwInErrors;
        public uint dwInUnknownProtos;
        public uint dwOutOctets;
        public uint dwOutUcastPkts;
        public uint dwOutNUcastPkts;
        public uint dwOutDiscards;
        public uint dwOutErrors;
        public uint dwOutQLen;
        public uint dwDescrLen;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxlenIfdescr)]
        public byte[] bDescr;
    }
}
