using System.Runtime.InteropServices;
using System.Text;

namespace InternetMonitor.Net;

/// <summary>The radio side of a Wi-Fi connection, as Windows reports it right now.</summary>
/// <param name="Ssid">Network name, or null when Windows withheld it (see <see cref="AccessDenied"/>).</param>
/// <param name="Bssid">The access point's radio address, "aa:bb:cc:dd:ee:ff", or null when withheld.</param>
/// <param name="SignalPercent">Windows' 0-100 signal quality - the number the taskbar's bars are drawn from.</param>
/// <param name="Generation">"Wi-Fi 6", "Wi-Fi 5", ... or null for a PHY type without one.</param>
/// <param name="AccessDenied">
/// Windows refused the connection details because location access is off - since Windows 11 24H2 a
/// network name and access point count as location data. The signal is then derived from the RSSI
/// and the band from the channel, both of which Windows still reports (see <see cref="WlanInfo"/>).
/// </param>
public sealed record WifiInfo(
    string? Ssid,
    string? Bssid,
    int? SignalPercent,
    int? RssiDbm,
    int? Channel,
    int? FrequencyMHz,
    string? Generation,
    double? RxMbps,
    double? TxMbps,
    bool AccessDenied)
{
    /// <summary>"2.4 GHz", "5 GHz" or "6 GHz", from the centre frequency; null when unknown.</summary>
    public string? Band => FrequencyMHz switch
    {
        >= 2400 and < 2500 => "2.4 GHz",
        >= 4900 and < 5925 => "5 GHz",
        >= 5925 and < 7200 => "6 GHz",
        _ => null,
    };

    public static WifiInfo Denied { get; } = new(null, null, null, null, null, null, null, null, null, AccessDenied: true);
}

/// <summary>
/// Reads the current Wi-Fi connection's details through the Native Wifi API (wlanapi.dll).
///
/// Same conventions as <see cref="IcmpPing"/>: classic DllImport, raw handles released in finally,
/// and the returned structures read field by field at their documented offsets rather than through
/// marshalled struct definitions - the connection-attributes structure alone nests four others,
/// and only a handful of its fields are needed.
///
/// Everything degrades to "unknown" rather than throwing: a machine without the WLAN service
/// (Server Core, some VMs) or a driver that does not report a field must still get a working step 2.
/// </summary>
public static class WlanInfo
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ClientVersion = 2;

    private const int OpcodeChannelNumber = 8;
    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeRssi = 0x10000102;
    private const int BssTypeAny = 3;

    // WLAN_CONNECTION_ATTRIBUTES: two DWORDs, a 256-WCHAR profile name, then the association
    // attributes - an SSID (length + 32 bytes), the BSS type, the 6-byte BSSID padded to 8, then
    // the PHY type, PHY index, signal quality and receive/transmit rates in kbit/s.
    private const int AssociationOffset = 8 + 512;
    private const int SsidLengthOffset = AssociationOffset;
    private const int SsidOffset = AssociationOffset + 4;
    private const int BssidOffset = AssociationOffset + 40;
    private const int PhyTypeOffset = AssociationOffset + 48;
    private const int SignalQualityOffset = AssociationOffset + 56;
    private const int RxRateOffset = AssociationOffset + 60;
    private const int TxRateOffset = AssociationOffset + 64;

    // WLAN_BSS_LIST: total size and item count, then WLAN_BSS_ENTRY records of a fixed 360 bytes
    // (their variable-length information elements are stored after the array, not inline).
    private const int BssListHeader = 8;
    private const int BssEntrySize = 360;
    private const int BssEntryBssidOffset = 40;
    private const int BssEntryFrequencyOffset = 92;

    /// <summary>
    /// Queries the connection on the Wi-Fi adapter with this interface GUID - on Windows that is
    /// <c>NetworkInterface.Id</c>. Null when the adapter is not connected or the API is unavailable.
    /// </summary>
    public static WifiInfo? Query(Guid interfaceId)
    {
        IntPtr client = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out client) != ErrorSuccess)
            {
                return null;
            }

            uint result = WlanQueryInterface(client, ref interfaceId, OpcodeCurrentConnection, IntPtr.Zero, out _, out IntPtr connection, out _);
            if (result == ErrorAccessDenied)
            {
                return WithoutLocation(client, interfaceId);
            }

            if (result != ErrorSuccess)
            {
                return null;
            }

            string? ssid;
            string? bssid;
            int signal;
            int phyType;
            double rx;
            double tx;
            try
            {
                int ssidLength = Math.Clamp(Marshal.ReadInt32(connection, SsidLengthOffset), 0, 32);
                var ssidBytes = new byte[ssidLength];
                Marshal.Copy(connection + SsidOffset, ssidBytes, 0, ssidLength);
                ssid = ssidLength == 0 ? null : Encoding.UTF8.GetString(ssidBytes);

                var bssidBytes = new byte[6];
                Marshal.Copy(connection + BssidOffset, bssidBytes, 0, 6);
                bssid = bssidBytes.All(b => b == 0) ? null : string.Join(":", bssidBytes.Select(b => b.ToString("x2")));

                phyType = Marshal.ReadInt32(connection, PhyTypeOffset);
                signal = Marshal.ReadInt32(connection, SignalQualityOffset);
                rx = Marshal.ReadInt32(connection, RxRateOffset) / 1000.0;
                tx = Marshal.ReadInt32(connection, TxRateOffset) / 1000.0;
            }
            finally
            {
                WlanFreeMemory(connection);
            }

            int? rssi = QueryInt(client, interfaceId, OpcodeRssi);
            int? channel = QueryInt(client, interfaceId, OpcodeChannelNumber);
            int? frequency = bssid is null ? null : FrequencyOf(client, interfaceId, bssid);

            return new WifiInfo(
                ssid,
                bssid,
                Math.Clamp(signal, 0, 100),
                rssi is < 0 and > -120 ? rssi : null,
                channel is > 0 ? channel : null,
                frequency,
                GenerationOf(phyType),
                rx > 0 ? rx : null,
                tx > 0 ? tx : null,
                AccessDenied: ssid is null && bssid is null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (client != IntPtr.Zero)
            {
                WlanCloseHandle(client, IntPtr.Zero);
            }
        }
    }

    /// <summary>
    /// What is left with location access off. Measured on Windows 11 (build 26300): the connection
    /// details and both scan lists are refused, but the RSSI and the channel are not - and those are
    /// the two that matter for "is the signal good enough". The percentage is derived from the RSSI
    /// the way Windows itself scales it (-100 dBm is 0%, -50 dBm and above is 100%), and the band
    /// from the channel number - which cannot tell a 6 GHz channel from a 2.4 or 5 GHz one with the
    /// same number, a rare enough case to accept.
    /// </summary>
    private static WifiInfo WithoutLocation(IntPtr client, Guid interfaceId)
    {
        int? rssi = QueryInt(client, interfaceId, OpcodeRssi) is < 0 and > -120 and int r ? r : null;
        int? channel = QueryInt(client, interfaceId, OpcodeChannelNumber) is > 0 and int c ? c : null;

        return WifiInfo.Denied with
        {
            RssiDbm = rssi,
            SignalPercent = rssi is { } dbm ? SignalFromRssi(dbm) : null,
            Channel = channel,
            FrequencyMHz = channel is { } ch ? FrequencyOfChannel(ch) : null,
        };
    }

    public static int SignalFromRssi(int dbm) => Math.Clamp(2 * (dbm + 100), 0, 100);

    /// <summary>The centre frequency of a 2.4 or 5 GHz channel; null for a number in neither range.</summary>
    public static int? FrequencyOfChannel(int channel) => channel switch
    {
        14 => 2484,
        >= 1 and <= 13 => 2407 + (5 * channel),
        >= 32 and <= 177 => 5000 + (5 * channel),
        _ => null,
    };

    private static int? QueryInt(IntPtr client, Guid interfaceId, int opcode)
    {
        if (WlanQueryInterface(client, ref interfaceId, opcode, IntPtr.Zero, out int size, out IntPtr data, out _) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            return size >= 4 ? Marshal.ReadInt32(data) : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    /// <summary>
    /// The connected access point's centre frequency, from the scan cache. The channel number alone
    /// cannot tell the bands apart - channel 37 exists on both 5 GHz and 6 GHz - the frequency can.
    /// </summary>
    private static int? FrequencyOf(IntPtr client, Guid interfaceId, string bssid)
    {
        if (WlanGetNetworkBssList(client, ref interfaceId, IntPtr.Zero, BssTypeAny, false, IntPtr.Zero, out IntPtr list) != ErrorSuccess)
        {
            return null;
        }

        try
        {
            int count = Marshal.ReadInt32(list, 4);
            for (int i = 0; i < count; i++)
            {
                IntPtr entry = list + BssListHeader + (i * BssEntrySize);
                var bytes = new byte[6];
                Marshal.Copy(entry + BssEntryBssidOffset, bytes, 0, 6);
                if (string.Join(":", bytes.Select(b => b.ToString("x2"))) == bssid)
                {
                    int kHz = Marshal.ReadInt32(entry, BssEntryFrequencyOffset);
                    return kHz > 0 ? kHz / 1000 : null;
                }
            }

            return null;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    /// <summary>DOT11_PHY_TYPE to the marketing generation people recognise from their router's box.</summary>
    private static string? GenerationOf(int phyType) => phyType switch
    {
        7 => "Wi-Fi 4",
        8 => "Wi-Fi 5",
        10 => "Wi-Fi 6",
        11 => "Wi-Fi 7",
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        _ => null,
    };

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        int opCode,
        IntPtr reserved,
        out int dataSize,
        out IntPtr data,
        out int opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(
        IntPtr clientHandle,
        ref Guid interfaceGuid,
        IntPtr dot11Ssid,
        int dot11BssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled,
        IntPtr reserved,
        out IntPtr wlanBssList);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
