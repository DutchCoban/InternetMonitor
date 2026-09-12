using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network.Diagnosis;

/// <summary>The latest result of every probe from one poll cycle, handed to the diagnosis engine.</summary>
public sealed record ProbeSnapshot(
    NetworkInterfaceProbeResult NetworkInterface,
    IpAddressProbeResult IpAddress,
    GatewayProbeResult Gateway,
    InternetProbeResult Internet,
    DnsResolutionProbeResult Dns,
    HttpsEndpointProbeResult GeneralHttps,
    TimeSyncProbeResult TimeSync,
    IReadOnlyList<(string Name, IProbeResult Result)> ApplicationEndpoints);
