using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps;

/// <summary>
/// What a step actually observed, in typed form.
///
/// A step reports two things: a summary a person can read, and these facts a program can reason
/// over. Keeping them apart is what lets the root-cause analyser ask precise questions - "was the
/// address an APIPA one?", "did ICMP fail while HTTPS worked?" - instead of parsing strings back
/// out of display text, which is how diagnosis logic rots.
///
/// Derived records rather than one wide type, so each step only declares what it can actually
/// know, and the analyser pattern-matches on the one it wants.
/// </summary>
public abstract record StepFacts;

public sealed record AdapterFacts(
    bool AnyAdapterPresent,
    bool Enabled,
    string? Name,
    string? Description,
    NetworkInterfaceType? Type,
    string? MacAddress,
    long? SpeedMbps,
    bool IsWireless,
    bool IsLikelyVirtual,
    int CandidateCount) : StepFacts;

public sealed record LinkFacts(
    PhysicalLinkState Link,
    OperationalStatus? ManagedStatus,
    bool IsWireless,
    string? AdapterName,

    // The radio side of a connected Wi-Fi adapter: signal, band, access point. Null for a cable,
    // or when Windows could not report it.
    WifiInfo? Wifi = null,

    // Whether Windows treats the connection as metered (data plan, phone hotspot). Null when not
    // connected.
    ConnectionCostInfo? Cost = null) : StepFacts
{
    /// <summary>Connected over Wi-Fi with a signal below the given Windows signal quality.</summary>
    public bool WeakSignal(int thresholdPercent) => Wifi?.SignalPercent is { } signal && signal < thresholdPercent;
}

public sealed record IpAddressFacts(
    IPAddress? Address,
    IPAddress? SubnetMask,
    bool IsApipa,
    bool DhcpEnabled,
    IReadOnlyList<IPAddress> DnsServers,
    IPAddress? Gateway,

    // An active VPN connection, if any - see Net.VpnDetector.
    VpnInfo? Vpn = null,

    // The adapter's global IPv6 address, when the network offers IPv6 at all. Null on an
    // IPv4-only network, which is normal and not a fault.
    IPAddress? GlobalIpv6 = null) : StepFacts;

public sealed record GatewayFacts(
    IPAddress? Address,
    bool Reachable,

    // Which method answered: ICMP, or a TCP connect to one of the probed ports.
    string? AnsweredVia,
    double? LatencyMs,
    IPStatus? IcmpStatus) : StepFacts;

public sealed record PublicIpEndpointResult(IPAddress Address, bool Reachable, double? LatencyMs, IPStatus Status);

public sealed record PublicIpFacts(
    IReadOnlyList<PublicIpEndpointResult> Endpoints,
    double? BestLatencyMs,

    // The IPv6 ping, only when the adapter has a global IPv6 address: null when not tried.
    bool? Ipv6Reachable = null,
    double? Ipv6LatencyMs = null)
    : StepFacts
{
    public int ReachableCount => Endpoints.Count(e => e.Reachable);

    /// <summary>
    /// One silent endpoint means nothing - a single anycast resolver can be having a bad day, or
    /// be blocked on its own. Only all of them failing is evidence about the connection.
    /// </summary>
    public bool AllUnreachable => Endpoints.Count > 0 && ReachableCount == 0;
}

public sealed record DnsFacts(
    string Hostname,
    IReadOnlyList<IPAddress> ResolvedAddresses,
    double? ElapsedMs,
    IReadOnlyList<IPAddress> ConfiguredServers,

    // Null when the adapter's own DNS servers answered and no cross-check was needed. True means
    // a public resolver answered a query the adapter's servers could not, which narrows the fault
    // from "DNS is broken" to "this adapter's DNS server specifically".
    bool? PublicResolversWorked,

    // True when a public resolver gave no DNS answer but did accept a connection on port 443:
    // the server is reachable, so DNS traffic (port 53) is most likely being blocked on the way.
    bool DnsPortBlockedSuspected = false,

    // Null when the user configured no last-resort DNS servers, or they were never needed.
    bool? CustomServersWorked = null) : StepFacts;

public sealed record TargetEndpointResult(
    string Name,
    Configuration.TargetKind Kind,
    string Url,
    bool Reachable,
    int? StatusCode,
    int? ExpectedStatusCode,
    HttpStage FailedStage,
    string? Error,
    HttpTimings? Timings,
    IPAddress? RemoteAddress,

    // Set only for a TcpPort target: HttpTimings is an HTTP-shaped record that does not fit a bare
    // connect, so its one meaningful number - the time to connect - is carried separately instead.
    double? ConnectMs,
    bool IsReachabilityCheck = false)
{
    /// <summary>True when a URL target answered with something other than the one status code it was configured to expect.</summary>
    public bool StatusMismatch => Reachable && ExpectedStatusCode is { } expected && StatusCode != expected;

    /// <summary>The configured endpoint's stable id, for its history series; null for the built-in reachability check.</summary>
    public string? EndpointId { get; init; }
}

public sealed record TargetFacts(
    IReadOnlyList<TargetEndpointResult> Endpoints,

    // Set when the reachability check failed and the follow-up probe found a login page in the
    // way (see Net.CaptivePortalProbe). PortalHost is where it redirected to, for display only.
    bool CaptivePortal = false,
    string? PortalHost = null,

    // The reachability check repeated over IPv6, only when the adapter has a global IPv6 address:
    // null when not tried. Ipv6Error says which stage failed.
    bool? Ipv6Reachable = null,
    string? Ipv6Error = null) : StepFacts
{
    /// <summary>
    /// IPv6 is offered but does not work, while the same check over IPv4 does. Browsers try IPv6
    /// first, so this is the classic cause of sites that load slowly or only sometimes.
    /// </summary>
    public bool Ipv6Broken => Ipv6Reachable == false && Endpoints.Any(e => e.IsReachabilityCheck && e.Reachable);

    public bool AnyReachable => Endpoints.Any(e => e.Reachable);

    public bool AllUnreachable => Endpoints.Count > 0 && !AnyReachable;
}
