using System.Net;

namespace InternetMonitor.Configuration;

/// <summary>What kind of check a step-7 target performs - mirrors <see cref="EndpointKind"/>.</summary>
public enum TargetKind
{
    Url,
    TcpPort,
    Ping,
}

/// <summary>
/// What a configured endpoint for step 7 looks like. <see cref="Url"/> stays the second positional
/// parameter, and <see cref="Kind"/> defaults to <see cref="TargetKind.Url"/>, so every existing
/// two-argument <c>new TargetEndpoint(name, url)</c> call - including the built-in default below -
/// keeps compiling and keeps meaning exactly what it used to.
/// </summary>
public sealed record TargetEndpoint(
    string Name,
    string Url,
    TargetKind Kind = TargetKind.Url,
    string Host = "",
    int Port = 0,
    int? ExpectedStatusCode = null,
    IReadOnlyList<EndpointHeader>? Headers = null)
{
    // Same nullable-parameter-plus-shadowing-property trick as EndpointConfig.Headers: a default
    // parameter value must be a compile-time constant, which an empty list is not.
    public IReadOnlyList<EndpointHeader> Headers { get; init; } = Headers ?? [];

    /// <summary>
    /// Marks the built-in "is there internet at all" check, as opposed to a user's own endpoint. Only
    /// this one is followed up with <see cref="CheckTargets.CaptivePortalProbeUrl"/> when it fails:
    /// a user's own server being unreachable says nothing about a login page.
    /// </summary>
    public bool IsReachabilityCheck { get; init; }

    /// <summary>The configured endpoint this came from (its stable id), so its results can be charted under one series; null for the built-in check.</summary>
    public string? Id { get; init; }
}

/// <summary>
/// What the network steps aim at, and how long each is allowed to take.
///
/// The timeouts are the v1 values, kept because they were arrived at by measurement rather than
/// guesswork. What changes in v2 is that they no longer add up: the four network steps run
/// concurrently, so a cycle costs the slowest step (about three seconds in the worst case) rather
/// than their sum.
///
/// The defaults are European on purpose, as in v1 - this is a generic diagnostics tool with no
/// organisation's endpoints baked in, and a user's own endpoints are added through Settings.
/// </summary>
public sealed record CheckTargets
{
    /// <summary>
    /// Three well-known public resolvers, pinged in parallel. Three rather than one because a
    /// single anycast address being unreachable says nothing about the connection.
    /// </summary>
    public IReadOnlyList<IPAddress> PublicIpTargets { get; init; } =
    [
        IPAddress.Parse("9.9.9.9"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("1.1.1.1"),
    ];

    /// <summary>
    /// The same three resolvers' IPv6 addresses, pinged by step 5 only when the adapter has a
    /// global IPv6 address - the IPv6 half of the same question.
    /// </summary>
    public IReadOnlyList<IPAddress> PublicIpv6Targets { get; init; } =
    [
        IPAddress.Parse("2620:fe::fe"),
        IPAddress.Parse("2001:4860:4860::8888"),
        IPAddress.Parse("2606:4700:4700::1111"),
    ];

    /// <summary>
    /// The page step 7 fetches over IPv6 when the adapter has a global IPv6 address: Windows' own
    /// IPv6 connectivity test, which has only an IPv6 address. The reachability check above has no
    /// IPv6 address at all, so it cannot serve here. Plain HTTP, like Windows' own test.
    /// </summary>
    public Uri Ipv6CheckUrl { get; init; } = new("http://ipv6.msftconnecttest.com/connecttest.txt");

    /// <summary>The hostname step 6 resolves. A stable, neutral, European name.</summary>
    public string DnsProbeHostname { get; init; } = "www.ripe.net";

    /// <summary>
    /// Resolvers asked one after the other, in this order, only when the adapter's own DNS
    /// servers fail. The first that resolves the name proves the fault is the adapter's DNS
    /// server rather than DNS as such. Fixed on purpose: this is the yardstick, not a preference.
    /// </summary>
    public IReadOnlyList<IPAddress> PublicResolvers { get; init; } =
    [
        IPAddress.Parse("9.9.9.9"),
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
    ];

    /// <summary>
    /// The user's own last-resort DNS servers (Settings, at most two), tried only when the
    /// adapter's servers and every public resolver have failed. Empty by default.
    /// </summary>
    public IReadOnlyList<IPAddress> CustomDnsServers { get; init; } = [];

    /// <summary>
    /// The endpoints step 7 checks. The first is the general internet reachability check; the rest
    /// come from the user's configuration.
    ///
    /// It is a captive-portal "generate_204" endpoint, not a content website, and that choice is
    /// not cosmetic. A normal site sits behind a CDN that scores clients for bot-like behaviour,
    /// and the score - not the network - then decides the response time. Measured against
    /// www.ripe.net: a request identifying itself as curl gets its first byte in 0.26s, while the
    /// identical request with any other User-Agent takes 1.2 to 3.4 seconds, repeatably and
    /// interleaved. Pointing a latency monitor at an endpoint like that means measuring your own
    /// fingerprint rather than the connection, and the reading drifts whenever the CDN's rules
    /// change.
    ///
    /// A generate_204 endpoint exists to answer this exact question: it returns an empty 204 with
    /// no body, no redirect and no bot scoring. Both of these answer in about 0.25s regardless of
    /// User-Agent. They are the same endpoints v1 used for its HTTP check.
    /// </summary>
    public IReadOnlyList<TargetEndpoint> Targets { get; init; } =
    [
        new TargetEndpoint("Internet", "https://connectivitycheck.volla.tech/generate_204", ExpectedStatusCode: 204) { IsReachabilityCheck = true },
    ];

    /// <summary>
    /// The plain-HTTP page asked for when the reachability check fails, to tell a login page apart
    /// from a dead connection - Windows' own connectivity test page, so a network that lets Windows
    /// through without a login lets this through too. See <see cref="Net.CaptivePortalProbe"/>.
    /// </summary>
    public Uri CaptivePortalProbeUrl { get; init; } = new("http://www.msftconnecttest.com/connecttest.txt");

    /// <summary>The exact body <see cref="CaptivePortalProbeUrl"/> serves when nothing is in the way.</summary>
    public string CaptivePortalProbeBody { get; init; } = "Microsoft Connect Test";

    public TimeSpan CaptivePortalProbeTimeout { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Ports raced alongside ICMP when checking the gateway: whichever answers first wins.</summary>
    public IReadOnlyList<int> GatewayProbePorts { get; init; } = [80, 443, 53];

    public TimeSpan GatewayTimeout { get; init; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan PublicIpTimeout { get; init; } = TimeSpan.FromMilliseconds(1200);

    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan PublicResolverTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    public TimeSpan TargetTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Below this Windows signal quality a Wi-Fi link is reported as weak. Around 40% is where the
    /// taskbar drops to two bars and real-world throughput and stability visibly fall off.
    /// </summary>
    public int WeakWifiSignalPercent { get; init; } = 40;

    /// <summary>Above this, a latency reading is reported as degraded rather than healthy.</summary>
    public int LatencyWarningMs { get; init; } = 300;

    public int LatencyErrorMs { get; init; } = 1000;
}
