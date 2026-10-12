using System.Net;

namespace InternetMonitor.Configuration;

/// <summary>What a configured endpoint for step 7 looks like.</summary>
public sealed record TargetEndpoint(string Name, string Url);

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

    /// <summary>The hostname step 6 resolves. A stable, neutral, European name.</summary>
    public string DnsProbeHostname { get; init; } = "www.ripe.net";

    /// <summary>
    /// Resolvers used only to cross-check a failed lookup. If these answer where the system
    /// resolver did not, the problem is the configured DNS server rather than DNS as such.
    /// </summary>
    public IReadOnlyList<IPAddress> PublicResolvers { get; init; } =
    [
        IPAddress.Parse("9.9.9.9"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("1.1.1.1"),
    ];

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
        new TargetEndpoint("Internet", "https://connectivitycheck.volla.tech/generate_204"),
    ];

    /// <summary>Ports raced alongside ICMP when checking the gateway: whichever answers first wins.</summary>
    public IReadOnlyList<int> GatewayProbePorts { get; init; } = [80, 443, 53];

    public TimeSpan GatewayTimeout { get; init; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan PublicIpTimeout { get; init; } = TimeSpan.FromMilliseconds(1200);

    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan PublicResolverTimeout { get; init; } = TimeSpan.FromMilliseconds(1000);

    public TimeSpan TargetTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Above this, a latency reading is reported as degraded rather than healthy.</summary>
    public int LatencyWarningMs { get; init; } = 300;

    public int LatencyErrorMs { get; init; } = 1000;
}
