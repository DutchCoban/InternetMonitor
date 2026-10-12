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
    string? AdapterName) : StepFacts;

public sealed record IpAddressFacts(
    IPAddress? Address,
    IPAddress? SubnetMask,
    bool IsApipa,
    bool DhcpEnabled,
    IReadOnlyList<IPAddress> DnsServers,
    IPAddress? Gateway) : StepFacts;

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
    double? BestLatencyMs)
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

    // Null when the system lookup succeeded and no cross-check was needed. True means public
    // resolvers answered a query the configured one could not, which narrows the fault from
    // "DNS is broken" to "your DNS server specifically".
    bool? PublicResolversWorked) : StepFacts;

public sealed record TargetEndpointResult(
    string Name,
    string Url,
    bool Reachable,
    int? StatusCode,
    HttpStage FailedStage,
    string? Error,
    HttpTimings? Timings,
    IPAddress? RemoteAddress);

public sealed record TargetFacts(IReadOnlyList<TargetEndpointResult> Endpoints) : StepFacts
{
    public bool AnyReachable => Endpoints.Any(e => e.Reachable);

    public bool AllUnreachable => Endpoints.Count > 0 && !AnyReachable;
}
