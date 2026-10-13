using System.Net;

namespace InternetMonitor.Core.Tools.LanScan;

/// <summary>
/// One device found on the local network. <see cref="Hostname"/> and <see cref="MacAddress"/> are
/// both best-effort and frequently null - a device that ignores reverse DNS, or one whose ARP
/// entry has not been (re)learned recently, is not a bug, just a device this simple a scanner
/// cannot name further.
/// </summary>
public sealed record LanDevice(IPAddress Address, string? Hostname, string? MacAddress, double? RoundtripMs);
