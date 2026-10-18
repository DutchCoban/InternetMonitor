using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.Incidents;

/// <summary>Whether an incident is still going on or has been closed by two good cycles in a row.</summary>
public enum IncidentStatus
{
    Active,
    Resolved,
}

/// <summary>
/// One step's state at one moment, flattened for storage. <see cref="Headline"/>/<see cref="Detail"/>
/// are English - see <see cref="StepResult.EnglishHeadline"/> - even though the step that produced
/// them was reporting in whatever language the screen showed at the time, since this is exactly
/// the kind of text that gets permanently frozen into an incident.
/// </summary>
public sealed record StepSnapshotEntry(
    StepId Step,
    StepState State,
    string Headline,
    string? Detail,
    IReadOnlyDictionary<string, string> Measurements)
{
    public static StepSnapshotEntry From(StepResult result) => new(
        result.Step,
        result.State,
        result.EnglishHeadline(),
        result.EnglishDetail(),
        result.Measurements.ToDictionary(m => m.Label, m => m.Value));
}

/// <summary>A moment during an incident when something changed.</summary>
public sealed record TimelineEntry(DateTimeOffset AtUtc, string Text);

/// <summary>One hop of the route traced when an incident opened; <see cref="Address"/> is null for a silent hop.</summary>
public sealed record IncidentHop(int Ttl, string? Address, string? Hostname, string? Owner, double? LatencyMs);

/// <summary>
/// The route to the reachability target as it was when a provider-side incident opened - the
/// evidence a help desk asks for: which hop the packets stopped at.
/// </summary>
public sealed record IncidentTraceroute(string Target, DateTimeOffset AtUtc, bool Reached, IReadOnlyList<IncidentHop> Hops)
{
    /// <summary>One line per hop, for the detail panel and the text exports.</summary>
    public IReadOnlyList<string> Describe(string noAnswer) =>
    [
        .. Hops.Select(h => h.Address is null
            ? $"{h.Ttl,2}. {noAnswer}"
            : $"{h.Ttl,2}. {h.Address}"
              + (h.Hostname is { Length: > 0 } ? $"  {h.Hostname}" : string.Empty)
              + (h.Owner is { Length: > 0 } ? $"  {h.Owner}" : string.Empty)
              + (h.LatencyMs is { } ms ? $"  {ms:F0} ms" : string.Empty)),
    ];
}

/// <summary>Where the capacity a window's utilization is judged against came from.</summary>
public enum ThroughputCapacitySource
{
    None,
    SpeedTest,
    LinkSpeed,
}

/// <summary>
/// This computer's own traffic over one stretch of time around an incident, from the traffic
/// history: the average rate, the fastest cycle and when it was, and both as a share of the line's
/// capacity when that is known. Each history point covers the recording interval that ends at its
/// timestamp, so a window's edges are accurate to one interval.
/// </summary>
public sealed record ThroughputWindow(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    int Samples,
    double AverageDownMbps,
    double AverageUpMbps,
    double PeakDownMbps,
    double PeakUpMbps,
    DateTimeOffset PeakAtUtc,
    double? AverageUtilization,
    double? PeakUtilization);

/// <summary>
/// The load this computer put on the line around an incident - what an investigator needs next
/// to the cause: was the PC pushing the line hard when it broke (a router or bufferbloat problem),
/// or idle (the provider's), and what was normal just before. Four windows: the 15 minutes before
/// the 5 minutes before it opened (the baseline), those 5 minutes, the incident itself, and the
/// 5 minutes after it closed. A window is null when there were no measurements for it; the after
/// window is also null while <see cref="AfterPending"/> says it has not fully elapsed yet. Only
/// this computer's traffic is visible: another device filling the line is not.
/// </summary>
public sealed record IncidentThroughput(
    ThroughputWindow? Baseline,
    ThroughputWindow? Before,
    ThroughputWindow? During,
    ThroughputWindow? After,
    double? CapacityDownMbps,
    double? CapacityUpMbps,
    ThroughputCapacitySource CapacitySource)
{
    /// <summary>True while the five minutes after the incident closed have not all been recorded yet.</summary>
    public bool AfterPending { get; init; }

    public bool HasAnyWindow => Baseline is not null || Before is not null || During is not null || After is not null;

    /// <summary>
    /// One line per window, then the capacity and the only-this-computer reminder - for the detail
    /// panel and the text export in the user's language, and for the AI export in English, which is
    /// why the texts come in as functions rather than as a manager.
    /// </summary>
    public IReadOnlyList<string> Describe(Func<string, string> get, Func<string, object[], string> format)
    {
        var lines = new List<string>(6)
        {
            Line(get, format, get("incidents.traffic.baseline"), Baseline, false, false),
            Line(get, format, get("incidents.traffic.before"), Before, true, false),
            Line(get, format, get("incidents.traffic.during"), During, false, false),
            Line(get, format, get("incidents.traffic.after"), After, false, AfterPending),
        };

        lines.Add(CapacitySource switch
        {
            ThroughputCapacitySource.SpeedTest => format("incidents.traffic.capacity.speedTest", [Mbps(CapacityDownMbps ?? 0), Mbps(CapacityUpMbps ?? 0)]),
            ThroughputCapacitySource.LinkSpeed => format("incidents.traffic.capacity.link", [Mbps(CapacityDownMbps ?? 0)]),
            _ => get("incidents.traffic.capacity.unknown"),
        });
        lines.Add(get("incidents.traffic.onlyThisComputer"));
        return lines;
    }

    private static string Line(Func<string, string> get, Func<string, object[], string> format, string label, ThroughputWindow? window, bool withPeakTime, bool pending)
    {
        if (window is null)
        {
            return format(pending ? "incidents.traffic.pending" : "incidents.traffic.none", [label]);
        }

        string peakAt = withPeakTime ? format("incidents.traffic.peakAt", [window.PeakAtUtc.ToLocalTime().ToString("HH:mm:ss")]) : string.Empty;
        return window.AverageUtilization is { } averageShare && window.PeakUtilization is { } peakShare
            ? format("incidents.traffic.line.capacity", [label, Mbps(window.AverageDownMbps), Mbps(window.AverageUpMbps), Percent(averageShare), Mbps(window.PeakDownMbps), Mbps(window.PeakUpMbps), Percent(peakShare), peakAt, window.Samples])
            : format("incidents.traffic.line", [label, Mbps(window.AverageDownMbps), Mbps(window.AverageUpMbps), Mbps(window.PeakDownMbps), Mbps(window.PeakUpMbps), peakAt, window.Samples]);
    }

    /// <summary>"12.3" - one decimal, invariant, the way every Mbit/s figure in the app is written.</summary>
    public static string Mbps(double value) => value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"85%" - a share of capacity as a whole percentage.</summary>
    public static string Percent(double share) => Math.Round(share * 100, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Everything known about one disruption.
///
/// The shape is v1's, extended where v1 left questions unanswerable. v1 stored a snapshot at the
/// start and another at resolution, and nothing in between - so "did it flap, or was it down the
/// whole time?" and "what changed just before it broke?" had no answer. <see cref="Timeline"/> and
/// <see cref="RecentChanges"/> are those two answers, and <see cref="Explanation"/> is the
/// incident in plain language rather than in probe output.
/// </summary>
public sealed record Incident
{
    /// <summary>The readable id, INC-20261017-213816.729: the local date and time it opened, to the millisecond.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// A globally unique id for the outside world. Two machines posting to the same webhook can
    /// open incidents in the same millisecond and so repeat <see cref="Id"/>; this never repeats,
    /// so a receiver can open and close exactly this incident and nothing else. Incidents recorded
    /// before it existed get one when they are next loaded.
    /// </summary>
    public Guid Uuid { get; init; } = Guid.NewGuid();

    public required RootCause Cause { get; init; }

    public required DateTimeOffset StartUtc { get; init; }

    public DateTimeOffset? EndUtc { get; set; }

    public IncidentStatus Status { get; set; } = IncidentStatus.Active;

    /// <summary>The headline shown in the list - the failing step's own words.</summary>
    public string Headline { get; set; } = string.Empty;

    public Explanation? Explanation { get; set; }

    public IReadOnlyList<StepId> AffectedSteps { get; set; } = [];

    public List<TimelineEntry> Timeline { get; init; } = [];

    public IReadOnlyList<StepSnapshotEntry> StepsAtStart { get; set; } = [];

    public IReadOnlyList<StepSnapshotEntry>? StepsAtResolution { get; set; }

    /// <summary>What moved in the half hour before this opened. Often the actual cause.</summary>
    public IReadOnlyList<string> RecentChanges { get; set; } = [];

    /// <summary>
    /// Which network-health condition opened this, e.g. "tcpRetransmissions" - null for every
    /// pipeline-driven incident. Lets the AI export group these by condition without parsing
    /// <see cref="Headline"/> text.
    /// </summary>
    public string? ConditionKey { get; init; }

    /// <summary>The user's own words about this incident - a ticket number, what the provider said.</summary>
    public string? Note { get; set; }

    /// <summary>Set once the trace started when the incident opened has finished; null when none was run.</summary>
    public IncidentTraceroute? Traceroute { get; set; }

    /// <summary>
    /// This computer's own traffic around the incident, captured when it opened and completed
    /// after it closed; null for an incident recorded before this existed.
    /// </summary>
    public IncidentThroughput? Throughput { get; set; }

    public TimeSpan? Duration => EndUtc is { } end ? end - StartUtc : null;
}
