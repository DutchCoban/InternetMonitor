using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Diagnosis;

/// <summary>Three short paragraphs a non-technical person can act on.</summary>
public sealed record Explanation(string What, string LikelyCause, string WhatToDo);

/// <summary>
/// Turns a root cause plus the facts behind it into plain language.
///
/// This is the rule table that stands in for the AI that was considered and deliberately not
/// built. For a problem space this small - seven steps, a handful of distinguishable conditions in
/// each - a rule table is better than a model in every way that matters here: it is deterministic,
/// it costs nothing, it needs no key and no network call at the exact moment the network is
/// broken, it cannot invent a cause that was never measured, and it can be unit-tested for
/// complete coverage.
///
/// The distinguishing facts are what make it worth more than a canned message per enum value. "No
/// IP address" and "an APIPA address because DHCP never answered" are the same root cause with
/// completely different advice; so are "DNS is broken" and "your DNS server specifically is
/// broken", and "the internet is down" versus "this network blocks ping".
/// </summary>
public static class PlainLanguage
{
    public static Explanation Describe(RootCause cause, StepSnapshot snapshot)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        string prefix = Prefix(cause, snapshot);

        return new Explanation(
            loc.Get($"{prefix}.what"),
            loc.Format($"{prefix}.cause", Arguments(cause, snapshot)),
            loc.Get($"{prefix}.todo"));
    }

    /// <summary>
    /// Same as <see cref="Describe"/>, resolved in English regardless of the active language - for
    /// an incident's <see cref="Incidents.Incident.Explanation"/>, which is recorded once and
    /// should not read differently depending on what the screen happened to show that moment.
    /// </summary>
    public static Explanation DescribeEnglish(RootCause cause, StepSnapshot snapshot)
    {
        string prefix = Prefix(cause, snapshot);

        return new Explanation(
            LocalizationManager.GetEnglish($"{prefix}.what"),
            LocalizationManager.FormatEnglish($"{prefix}.cause", Arguments(cause, snapshot)),
            LocalizationManager.GetEnglish($"{prefix}.todo"));
    }

    private static string Prefix(RootCause cause, StepSnapshot snapshot) => $"explain.{cause.Key()}{Variant(cause, snapshot)}";

    /// <summary>
    /// Picks the sub-case within a root cause, as a key suffix. Empty means the general case.
    /// </summary>
    private static string Variant(RootCause cause, StepSnapshot snapshot) => cause switch
    {
        RootCause.Link when snapshot.FactsFor<LinkFacts>(StepId.Link) is { IsWireless: true } => ".wifi",

        RootCause.IpConfiguration when snapshot.FactsFor<IpAddressFacts>(StepId.IpAddress) is { IsApipa: true } => ".apipa",

        RootCause.Dns when snapshot.FactsFor<DnsFacts>(StepId.Dns) is { PublicResolversWorked: true } => ".ownResolver",

        // Which probe is being filtered changes the advice: a router that ignores pings is a
        // configuration choice, while a network that drops all ICMP is usually a policy upstream.
        RootCause.FirewallSuspected when snapshot.StateOf(StepId.Gateway) == StepState.Failed => ".gateway",

        RootCause.Target when snapshot.FactsFor<TargetFacts>(StepId.Target) is { } target
            && target.Endpoints.Any(e => e.Reachable && e.StatusCode >= 500) => ".serverError",

        RootCause.Target when snapshot.FactsFor<TargetFacts>(StepId.Target) is { } target
            && target.Endpoints.Any(e => !e.Reachable && e.FailedStage == Net.HttpStage.Tls) => ".tls",

        _ => string.Empty,
    };

    /// <summary>
    /// The measured values a cause's explanation quotes back. Keeping these as arguments rather
    /// than baking them into the sentence is what lets the same text be translated four ways.
    /// </summary>
    private static object[] Arguments(RootCause cause, StepSnapshot snapshot) => cause switch
    {
        RootCause.Adapter => [snapshot.FactsFor<AdapterFacts>(StepId.Adapter)?.Name ?? "-"],

        RootCause.Link => [snapshot.FactsFor<LinkFacts>(StepId.Link)?.AdapterName ?? "-"],

        RootCause.IpConfiguration =>
            [snapshot.FactsFor<IpAddressFacts>(StepId.IpAddress)?.Address?.ToString() ?? "-"],

        RootCause.Gateway => [snapshot.FactsFor<GatewayFacts>(StepId.Gateway)?.Address?.ToString() ?? "-"],

        RootCause.Dns =>
        [
            snapshot.FactsFor<DnsFacts>(StepId.Dns)?.Hostname ?? "-",

            // Falls back to a dash rather than an empty string: a sentence ending in "()" reads
            // like a bug, and the server list can legitimately be empty when DHCP never supplied
            // one - which is itself worth seeing.
            Join(snapshot.FactsFor<DnsFacts>(StepId.Dns)?.ConfiguredServers.Select(s => s.ToString())),
        ],

        RootCause.Target =>
        [
            Join(snapshot.FactsFor<TargetFacts>(StepId.Target)?.Endpoints
                .Where(e => !e.Reachable || e.StatusCode >= 500)
                .Select(e => e.Name)),
        ],

        RootCause.Internet =>
        [
            (snapshot.FactsFor<PublicIpFacts>(StepId.PublicIp)?.Endpoints.Count ?? 0).ToString(),
        ],

        _ => [],
    };

    private static string Join(IEnumerable<string>? values)
    {
        string joined = string.Join(", ", values ?? []);
        return joined.Length == 0 ? "-" : joined;
    }
}
