using System.Diagnostics;
using System.Net.NetworkInformation;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record NetworkInterfaceProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    bool AdapterPresent,
    bool AdapterActive,
    bool ActuallyConnected,
    string? InterfaceName,
    NetworkInterfaceType? InterfaceType,
    string? MacAddress,
    long? LinkSpeedMbps,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => null;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string>
        {
            ["AdapterPresent"] = AdapterPresent.ToString(),
            ["AdapterActive"] = AdapterActive.ToString(),
            ["ActuallyConnected"] = ActuallyConnected.ToString(),
        };
        if (InterfaceName is not null) d["InterfaceName"] = InterfaceName;
        if (InterfaceType is not null) d["InterfaceType"] = InterfaceType.ToString()!;
        if (MacAddress is not null) d["MacAddress"] = MacAddress;
        if (LinkSpeedMbps is not null) d["LinkSpeedMbps"] = LinkSpeedMbps.Value.ToString();
        return d;
    }
}

/// <summary>
/// Is there an active network adapter at all? Distinguishes adapter-present (exists),
/// adapter-active (operationally up), and actually-connected (up AND carrying a usable IPv4
/// address) - three separate failure points a user can hit before ever reaching the gateway.
/// </summary>
public sealed class NetworkInterfaceProbe : IProbe
{
    public string Id => "network";
    public string Category => "Network";

    public Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var candidates = ActiveInterfaceSelector.GetAllNonVirtual().ToList();
        bool adapterPresent = candidates.Count > 0;
        NetworkInterface? active = ActiveInterfaceSelector.GetActive();
        bool adapterActive = active is not null;

        bool actuallyConnected = false;
        string? mac = null;
        long? speedMbps = null;
        NetworkInterfaceType? type = active?.NetworkInterfaceType;

        if (active is not null)
        {
            var ipProps = active.GetIPProperties();
            actuallyConnected = ipProps.UnicastAddresses
                .Any(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && !a.Address.Equals(System.Net.IPAddress.Any));
            byte[] macBytes = active.GetPhysicalAddress().GetAddressBytes();
            if (macBytes.Length > 0)
            {
                mac = string.Join(":", macBytes.Select(b => b.ToString("X2")));
            }
            speedMbps = active.Speed > 0 ? active.Speed / 1_000_000 : null;
        }

        sw.Stop();

        ProbeStatus status;
        string summary;
        string? error = null;
        if (!adapterPresent)
        {
            status = ProbeStatus.Error;
            summary = LocalizationManager.Instance.Get("probe.network.noAdapter");
            error = "No non-virtual network adapters present";
        }
        else if (!adapterActive)
        {
            status = ProbeStatus.Error;
            summary = LocalizationManager.Instance.Get("probe.network.notActive");
            error = "No operational (Up) network adapter found";
        }
        else if (!actuallyConnected)
        {
            status = ProbeStatus.Warning;
            summary = LocalizationManager.Instance.Format("probe.network.noUsableIp", active!.Name);
        }
        else
        {
            string kind = type == NetworkInterfaceType.Wireless80211 ? "WiFi" : "Ethernet";
            status = ProbeStatus.Ok;
            summary = LocalizationManager.Instance.Format("probe.network.connected", kind, active!.Name);
        }

        return Task.FromResult<IProbeResult>(new NetworkInterfaceProbeResult(
            Id, status, summary, sw.Elapsed, DateTimeOffset.UtcNow,
            adapterPresent, adapterActive, actuallyConnected,
            active?.Name, type, mac, speedMbps, error));
    }
}
