using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record IpAddressProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string? IPv4Address,
    bool IsApipa,
    string? InterfaceName,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => null;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string> { ["IsApipa"] = IsApipa.ToString() };
        if (IPv4Address is not null) d["IPv4Address"] = IPv4Address;
        if (InterfaceName is not null) d["InterfaceName"] = InterfaceName;
        return d;
    }
}

/// <summary>
/// Does the active interface have a usable IPv4 address? A 169.254.x.x (APIPA) address means
/// DHCP failed - a distinct, specific diagnosis from "no address at all".
/// </summary>
public sealed class IpAddressProbe : IProbe
{
    public string Id => "ip";
    public string Category => "Network";

    public Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var active = ActiveInterfaceSelector.GetActive();

        IPAddress? address = active?.GetIPProperties().UnicastAddresses
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

        sw.Stop();

        bool isApipa = address is not null && IsApipaAddress(address);

        ProbeStatus status;
        string summary;
        string? error = null;
        if (active is null || address is null)
        {
            status = ProbeStatus.Error;
            summary = LocalizationManager.Instance.Get("probe.ip.noAddress");
            error = "No IPv4 address on the active interface";
        }
        else if (isApipa)
        {
            status = ProbeStatus.Warning;
            summary = LocalizationManager.Instance.Format("probe.ip.apipa", address);
        }
        else
        {
            status = ProbeStatus.Ok;
            summary = address.ToString();
        }

        return Task.FromResult<IProbeResult>(new IpAddressProbeResult(
            Id, status, summary, sw.Elapsed, DateTimeOffset.UtcNow,
            address?.ToString(), isApipa, active?.Name, error));
    }

    private static bool IsApipaAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}
