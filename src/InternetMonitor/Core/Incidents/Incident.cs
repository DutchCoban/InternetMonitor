using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.Incidents;

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
    public required string Id { get; init; }

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

    /// <summary>A traceroute captured when the incident opened, when the cause made one worth having.</summary>
    public string? TraceroutePath { get; set; }

    /// <summary>
    /// Which network-health condition opened this, e.g. "tcpRetransmissions" - null for every
    /// pipeline-driven incident. Lets the AI export group these by condition without parsing
    /// <see cref="Headline"/> text.
    /// </summary>
    public string? ConditionKey { get; init; }

    public TimeSpan? Duration => EndUtc is { } end ? end - StartUtc : null;
}
