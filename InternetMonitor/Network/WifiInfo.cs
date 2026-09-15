using System.Runtime.InteropServices;
using System.Linq;

namespace InternetMonitor.Network;

public sealed record WifiSnapshot(string Ssid, string Bssid, int SignalQualityPercent, int LinkSpeedMbps, string PhyType);

/// <summary>
/// Thin wrapper around the native Windows WLAN API (wlanapi.dll) - .NET's own
/// <see cref="System.Net.NetworkInformation.NetworkInterface"/> exposes no signal-strength
/// member at all, so this is the only way to answer "how good is my Wi-Fi right now". Uses
/// <c>WlanQueryInterface</c>'s current-connection opcode, which reports signal quality as a
/// 0-100 percentage rather than raw RSSI in dBm - a coarser number than dBm, but avoids the far
/// larger BSS-list-scanning API (<c>WlanGetNetworkBssList</c>) for a diagnostic reading that
/// doesn't need it. No elevation required; every call opens and closes its own client handle
/// (mirrors this app's other one-shot-per-poll probes, e.g. <c>PingLatencyProbe</c>'s
/// <c>using var ping = new Ping()</c>) rather than holding a persistent handle that would need
/// lifecycle wiring into <see cref="DiagnosticsCoordinator"/>'s disposal.
/// </summary>
public static class WifiInfo
{
    private const int WlanApiVersion2 = 2;
    private const int WlanIntfOpcodeCurrentConnection = 7;
    private const int WlanInterfaceStateConnected = 1;

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(int dwClientVersion, IntPtr pReserved, out int pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int opCode, IntPtr pReserved, out int pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strInterfaceDescription;
        public int isState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_ASSOCIATION_ATTRIBUTES
    {
        public DOT11_SSID dot11Ssid;
        public int dot11BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;
        public int dot11PhyType;
        public uint uDot11PhyIndex;
        public uint wlanSignalQuality;
        public uint ulRxRate;
        public uint ulTxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_SECURITY_ATTRIBUTES
    {
        [MarshalAs(UnmanagedType.Bool)] public bool bSecurityEnabled;
        [MarshalAs(UnmanagedType.Bool)] public bool bOneXEnabled;
        public int dot11AuthAlgorithm;
        public int dot11CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_ATTRIBUTES
    {
        public int isState;
        public int wlanConnectionMode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public WLAN_ASSOCIATION_ATTRIBUTES wlanAssociationAttributes;
        public WLAN_SECURITY_ATTRIBUTES wlanSecurityAttributes;
    }

    /// <summary>Returns the current Wi-Fi connection's signal quality/SSID/BSSID/link details, or null if there's no wireless interface, no client service, or it isn't currently connected.</summary>
    public static WifiSnapshot? TryGetCurrentConnection()
    {
        if (WlanOpenHandle(WlanApiVersion2, IntPtr.Zero, out _, out IntPtr clientHandle) != 0)
        {
            return null;
        }

        try
        {
            if (WlanEnumInterfaces(clientHandle, IntPtr.Zero, out IntPtr interfaceListPtr) != 0)
            {
                return null;
            }

            try
            {
                int count = Marshal.ReadInt32(interfaceListPtr, 0);
                if (count == 0)
                {
                    return null;
                }

                // Header is two DWORDs (dwNumberOfItems, dwIndex) followed by the WLAN_INTERFACE_INFO
                // array - only the first interface is used, matching how ActiveInterfaceSelector
                // elsewhere in this app picks a single "the" active adapter rather than enumerating all.
                IntPtr firstInterfacePtr = interfaceListPtr + 8;
                var interfaceInfo = Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(firstInterfacePtr);
                if (interfaceInfo.isState != WlanInterfaceStateConnected)
                {
                    return null;
                }

                Guid guid = interfaceInfo.InterfaceGuid;
                if (WlanQueryInterface(clientHandle, ref guid, WlanIntfOpcodeCurrentConnection, IntPtr.Zero, out _, out IntPtr dataPtr, IntPtr.Zero) != 0)
                {
                    return null;
                }

                try
                {
                    var attributes = Marshal.PtrToStructure<WLAN_CONNECTION_ATTRIBUTES>(dataPtr);
                    if (attributes.isState != WlanInterfaceStateConnected)
                    {
                        return null;
                    }

                    WLAN_ASSOCIATION_ATTRIBUTES assoc = attributes.wlanAssociationAttributes;
                    string ssid = System.Text.Encoding.UTF8.GetString(assoc.dot11Ssid.ucSSID, 0, (int)Math.Min(assoc.dot11Ssid.uSSIDLength, 32));
                    string bssid = string.Join(":", assoc.dot11Bssid.Select(b => b.ToString("X2")));

                    return new WifiSnapshot(ssid, bssid, (int)assoc.wlanSignalQuality, (int)(assoc.ulRxRate / 1000), PhyTypeName(assoc.dot11PhyType));
                }
                finally
                {
                    WlanFreeMemory(dataPtr);
                }
            }
            finally
            {
                WlanFreeMemory(interfaceListPtr);
            }
        }
        finally
        {
            WlanCloseHandle(clientHandle, IntPtr.Zero);
        }
    }

    private static string PhyTypeName(int dot11PhyType) => dot11PhyType switch
    {
        4 => "OFDM (802.11a/g)",
        5 => "HR-DSSS (802.11b)",
        7 => "HT (802.11n)",
        8 => "VHT (802.11ac)",
        9 => "DMG (802.11ad)",
        10 => "HE (802.11ax)",
        11 => "EHT (802.11be)",
        _ => "Unknown",
    };
}
