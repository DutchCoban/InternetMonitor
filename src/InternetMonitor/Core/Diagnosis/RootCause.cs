namespace InternetMonitor.Core.Diagnosis;

/// <summary>
/// The single most likely cause of what the pipeline just observed.
///
/// The names match v1's DiagnosisClassification wherever the meaning is unchanged, so incidents
/// recorded by v1 map onto v2's model without losing their classification - see
/// <see cref="RootCauseExtensions.FromLegacyName"/>. <see cref="Link"/> is new, because v1 had no
/// step 2 to blame.
/// </summary>
public enum RootCause
{
    Healthy,
    Adapter,
    Link,
    IpConfiguration,
    Gateway,
    Internet,
    Dns,
    Target,

    /// <summary>
    /// Probes are being filtered rather than the connection being down: ICMP fails while real
    /// traffic works. Reported as a warning, not a failure, because nothing the user does is
    /// actually broken.
    /// </summary>
    FirewallSuspected,

    /// <summary>
    /// The network puts a login page in front of the internet (hotel, train, guest Wi-Fi) and it
    /// has not been passed yet. Nothing is broken - the user has to log in.
    /// </summary>
    CaptivePortal,

    /// <summary>
    /// The network hands out IPv6 but IPv6 traffic does not get through, while IPv4 works. A
    /// warning: everything works, but browsers try IPv6 first and fall back slowly.
    /// </summary>
    Ipv6Broken,

    /// <summary>
    /// Raised by <see cref="Monitors.NetworkHealthMonitor"/>, not the step pipeline - a packet-level
    /// condition (retransmissions, loss, TTL errors, a rogue DHCP server) crossed its configured
    /// threshold. Which one is recorded on the incident itself, see <see cref="Incidents.Incident.ConditionKey"/>.
    /// </summary>
    NetworkHealth,

    /// <summary>
    /// Raised by <see cref="Monitors.NetworkChangeMonitor"/>: the environment moved rather than
    /// broke - the connection went over another adapter, an adapter got an automatic private
    /// address, the network's settings changed, or the router answers from different hardware.
    /// Which one is in <see cref="Incidents.Incident.ConditionKey"/>. Never an outage.
    /// </summary>
    NetworkChange,

    Unknown,
}

/// <summary>Localization keys and the mapping of v1's classification names.</summary>
public static class RootCauseExtensions
{
    public static string Key(this RootCause cause) => cause switch
    {
        RootCause.Healthy => "healthy",
        RootCause.Adapter => "adapter",
        RootCause.Link => "link",
        RootCause.IpConfiguration => "ipConfig",
        RootCause.Gateway => "gateway",
        RootCause.Internet => "internet",
        RootCause.Dns => "dns",
        RootCause.Target => "target",
        RootCause.FirewallSuspected => "firewall",
        RootCause.CaptivePortal => "captivePortal",
        RootCause.Ipv6Broken => "ipv6",
        RootCause.NetworkHealth => "networkHealth",
        RootCause.NetworkChange => "networkChange",
        _ => "unknown",
    };

    /// <summary>The cause with this <see cref="Key"/>, or <see cref="RootCause.Unknown"/> for one that does not exist (any more).</summary>
    public static RootCause FromKey(string? key) =>
        Enum.GetValues<RootCause>().FirstOrDefault(c => c.Key() == key, RootCause.Unknown);

    /// <summary>
    /// Maps a classification written by v1 into v2's enum. v1's "Network" covered adapter, link
    /// and address together, and there is no way to recover which it meant from the name alone -
    /// so it becomes <see cref="RootCause.Adapter"/>, the first of the three, rather than
    /// inventing a precision the old record never had. "Https" and "Application" both become
    /// <see cref="RootCause.Target"/>, which is exactly what v2 calls that step.
    /// </summary>
    public static RootCause FromLegacyName(string? name) => name switch
    {
        "Healthy" => RootCause.Healthy,
        "Network" => RootCause.Adapter,
        "IpConfiguration" => RootCause.IpConfiguration,
        "Gateway" => RootCause.Gateway,
        "Internet" => RootCause.Internet,
        "Dns" => RootCause.Dns,
        "Https" or "Application" => RootCause.Target,
        "FirewallSuspected" => RootCause.FirewallSuspected,

        // TimeSync was a v1 classification with no v2 step behind it yet; it is not a connectivity
        // fault, so it degrades to Unknown rather than being mapped onto something it is not.
        _ => RootCause.Unknown,
    };
}
