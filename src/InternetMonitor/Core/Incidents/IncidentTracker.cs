using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Incidents;

/// <summary>
/// Turns a run of failing cycles into one incident, and keeps a timeline of what happened while it
/// was open.
///
/// Debounced in both directions: two consecutive bad cycles before an incident opens, two good
/// ones before it closes. v1 used exactly these thresholds and the reasoning holds - a single
/// failed cycle during a Wi-Fi roam or a DHCP renewal is not an outage, and recording it as one
/// fills the list with noise that hides the real events.
///
/// Keyed on the overall root cause rather than on individual steps, so "gateway and internet and
/// DNS all failed" is one incident with one cause rather than three competing records.
/// </summary>
public sealed class IncidentTracker
{
    private const int OpenThreshold = 2;
    private const int ResolveThreshold = 2;

    /// <summary>
    /// A long outage that flaps could otherwise grow an unbounded timeline, and with it an
    /// unbounded incidents.json rewritten on every change.
    /// </summary>
    private const int MaxTimelineEntries = 200;

    private readonly IncidentStore _store;
    private readonly ChangeDetector _changes;
    private readonly Dictionary<StepId, StepState> _stepStates = [];

    private Incident? _active;
    private int _consecutiveBad;
    private int _consecutiveGood;

    public IncidentTracker(IncidentStore store, ChangeDetector changes)
    {
        _store = store;
        _changes = changes;

        // Resume an incident the previous run left open rather than opening a second one for the
        // same ongoing problem. v1 did this too, and without it a crash during an outage produces
        // two overlapping records of it.
        _active = _store.Active;
    }

    /// <summary>Raised when an incident opens, so the app can capture evidence that is only worth gathering during a fault.</summary>
    public event Action<Incident>? IncidentOpened;

    public event Action<Incident>? IncidentClosed;

    public Incident? ActiveIncident => _active;

    public void Observe(StepSnapshot snapshot, RootCauseResult diagnosis)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _changes.Observe(snapshot);

        if (diagnosis.IsProblem)
        {
            _consecutiveGood = 0;
            _consecutiveBad++;

            if (_active is null && _consecutiveBad >= OpenThreshold)
            {
                Open(snapshot, diagnosis);
            }
            else if (_active is { } active && active.Cause != diagnosis.Cause)
            {
                // The problem changed character while open - a dead gateway became a dead link,
                // say. That is one incident evolving, not two incidents, and the timeline is
                // where that belongs.
                active.Timeline.Add(new TimelineEntry(
                    snapshot.TakenUtc,
                    loc.Format("incident.timeline.causeChanged", loc.Get($"cause.{diagnosis.Cause.Key()}"))));

                // The incident keeps the cause it opened with. An outage that starts as a dead
                // gateway and becomes a dead link is one event whose story is in the timeline;
                // rewriting the classification halfway would make the list disagree with the
                // history, and "what was it first?" is the more useful question.
                UpdateActive(snapshot, diagnosis);
            }

            return;
        }

        _consecutiveBad = 0;
        if (_active is null)
        {
            return;
        }

        _consecutiveGood++;
        if (_consecutiveGood >= ResolveThreshold)
        {
            Close(snapshot);
        }
    }

    private void Open(StepSnapshot snapshot, RootCauseResult diagnosis)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        StepResult? failure = snapshot.FirstFailure;

        var incident = new Incident
        {
            Id = _store.NextId(snapshot.TakenUtc),
            Cause = diagnosis.Cause,

            // Dated from the first bad cycle, not from the one that crossed the threshold:
            // the outage started when it started, and reporting it as starting later would
            // understate every duration by one interval.
            StartUtc = snapshot.TakenUtc,
            Status = IncidentStatus.Active,
            Headline = failure?.Headline ?? loc.Get($"cause.{diagnosis.Cause.Key()}"),
            Explanation = PlainLanguage.Describe(diagnosis.Cause, snapshot),
            AffectedSteps = diagnosis.AffectedSteps,
            StepsAtStart = [.. snapshot.All.OrderBy(r => (int)r.Step).Select(StepSnapshotEntry.From)],
            RecentChanges = [.. _changes.Recent(snapshot.TakenUtc).Select(c => $"{c.AtUtc.ToLocalTime():HH:mm:ss}  {c.Describe()}")],
        };

        incident.Timeline.Add(new TimelineEntry(
            snapshot.TakenUtc,
            loc.Format("incident.timeline.opened", incident.Headline)));

        _active = incident;
        _store.Add(incident);
        IncidentOpened?.Invoke(incident);
    }

    private void UpdateActive(StepSnapshot snapshot, RootCauseResult diagnosis)
    {
        if (_active is not { } active)
        {
            return;
        }

        active.Headline = snapshot.FirstFailure?.Headline ?? active.Headline;
        active.AffectedSteps = diagnosis.AffectedSteps;
        active.Explanation = PlainLanguage.Describe(diagnosis.Cause, snapshot);
        _store.Update(active);
    }

    private void Close(StepSnapshot snapshot)
    {
        if (_active is not { } active)
        {
            return;
        }

        LocalizationManager loc = LocalizationManager.Instance;

        active.EndUtc = snapshot.TakenUtc;
        active.Status = IncidentStatus.Resolved;
        active.StepsAtResolution = [.. snapshot.All.OrderBy(r => (int)r.Step).Select(StepSnapshotEntry.From)];
        active.Timeline.Add(new TimelineEntry(
            snapshot.TakenUtc,
            loc.Format("incident.timeline.resolved", FormatDuration(active.Duration))));

        _store.Update(active);
        _active = null;
        _consecutiveGood = 0;

        IncidentClosed?.Invoke(active);
    }

    /// <summary>
    /// Records a step-level transition into the open incident's timeline.
    ///
    /// Only genuine transitions are recorded. The pipeline republishes every step's result on
    /// every cycle, so without remembering the previous state per step this appends six identical
    /// lines every few seconds and the timeline becomes a log rather than a story - which is what
    /// the first version of this did, producing forty lines in a twelve-second outage.
    /// </summary>
    public void NoteStepChange(StepResult result)
    {
        if (!result.State.IsResolved())
        {
            return;
        }

        lock (_stepStates)
        {
            if (_stepStates.TryGetValue(result.Step, out StepState previous) && previous == result.State)
            {
                return;
            }

            _stepStates[result.Step] = result.State;

            // A step is only worth a timeline entry while an incident is open - but its state is
            // tracked either way, so the first entry after one opens is a real change rather than
            // a restatement of what was already true.
            if (_active is not { } active || active.Timeline.Count >= MaxTimelineEntries)
            {
                return;
            }

            LocalizationManager loc = LocalizationManager.Instance;
            string stepName = loc.Get($"step.{result.Step.Key()}.name");

            string text = result.State switch
            {
                StepState.Ok => loc.Format("incident.timeline.stepRecovered", stepName, result.Headline),
                StepState.Failed => loc.Format("incident.timeline.stepFailed", stepName, result.Headline),
                StepState.Warning => loc.Format("incident.timeline.stepDegraded", stepName, result.Headline),
                _ => string.Empty,
            };

            if (text.Length > 0)
            {
                active.Timeline.Add(new TimelineEntry(result.TimestampUtc, text));
            }
        }
    }

    public static string FormatDuration(TimeSpan? duration) => duration switch
    {
        null => "-",
        { TotalMinutes: < 1 } d => $"{d.TotalSeconds:F0}s",
        { TotalHours: < 1 } d => $"{d.Minutes}m {d.Seconds}s",
        { TotalDays: < 1 } d => $"{d.Hours}h {d.Minutes}m",
        { } d => $"{(int)d.TotalDays}d {d.Hours}h",
    };
}
