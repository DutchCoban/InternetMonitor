using System.Net;

namespace InternetMonitor.Core.Tools.LanScan;

/// <summary>
/// One device found on the local network. <see cref="Hostname"/>, <see cref="MacAddress"/> and
/// <see cref="Vendor"/> are all best-effort and frequently null - a device that ignores reverse
/// DNS, one whose ARP entry has not been (re)learned recently, or a manufacturer neither lookup
/// service recognizes, is not a bug, just a device this simple a scanner cannot name further.
/// </summary>
public sealed record LanDevice(IPAddress Address, string? Hostname, string? MacAddress, double? RoundtripMs, string? Vendor = null);
