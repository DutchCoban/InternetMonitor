namespace InternetMonitor.Core.Steps;

/// <summary>One labelled measurement, for the diagnostics table and the incident evidence.</summary>
public readonly record struct Measurement(string Label, string Value);

/// <summary>
/// What one step concluded in one cycle.
///
/// <see cref="Headline"/> and <see cref="Detail"/> are already localized - steps look their text up
/// through the localization manager rather than returning codes for the UI to translate, which is
/// how v1 did it and is the reason the diagnosis layer has no hardcoded language anywhere.
///
/// <see cref="ChartValue"/> is the one natural number this step contributes to history, or null
/// for steps that are a state rather than a measurement.
/// </summary>
public sealed record StepResult(
    StepId Step,
    StepState State,
    string Headline,
    string? Detail,
    double? ChartValue,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<Measurement> Measurements,
    StepFacts? Facts)
{
    public static StepResult Pending(StepId step) => new(
        step, StepState.Pending, string.Empty, null, null, TimeSpan.Zero, DateTimeOffset.UtcNow, [], null);

    public static StepResult Running(StepId step) => new(
        step, StepState.Running, string.Empty, null, null, TimeSpan.Zero, DateTimeOffset.UtcNow, [], null);

    /// <summary>
    /// Re-labels a failed result as skipped, keeping everything that was measured. Used when a
    /// step turns out to have been doomed by an earlier failure: the numbers are still worth
    /// having in the incident evidence, they just should not be presented as a second problem.
    /// </summary>
    public StepResult AsSkipped(string headline, string? detail) =>
        this with { State = StepState.Skipped, Headline = headline, Detail = detail };
}

/// <summary>
/// Every step's latest result, as one immutable value. This is what the root-cause analyser reads,
/// what an incident snapshots, and what the UI renders - all from the same object, so there is no
/// way for them to disagree about what was true at a given moment.
/// </summary>
public sealed class StepSnapshot
{
    private readonly Dictionary<StepId, StepResult> _results;

    public StepSnapshot(IEnumerable<StepResult> results, DateTimeOffset takenUtc)
    {
        _results = results.ToDictionary(r => r.Step);
        TakenUtc = takenUtc;
    }

    public DateTimeOffset TakenUtc { get; }

    public IReadOnlyCollection<StepResult> All => _results.Values;

    public StepResult? this[StepId step] => _results.GetValueOrDefault(step);

    /// <summary>Typed access to one step's facts, or null when the step did not run or produced a different shape.</summary>
    public T? FactsFor<T>(StepId step)
        where T : StepFacts => _results.GetValueOrDefault(step)?.Facts as T;

    public StepState StateOf(StepId step) => _results.GetValueOrDefault(step)?.State ?? StepState.Pending;

    /// <summary>The first step, in dependency order, that actually failed. The root cause lives here.</summary>
    public StepResult? FirstFailure => _results.Values
        .Where(r => r.State.IsFailure())
        .OrderBy(r => (int)r.Step)
        .FirstOrDefault();

    public bool AnyFailure => _results.Values.Any(r => r.State.IsFailure());

    public bool AnyWarning => _results.Values.Any(r => r.State == StepState.Warning);
}
