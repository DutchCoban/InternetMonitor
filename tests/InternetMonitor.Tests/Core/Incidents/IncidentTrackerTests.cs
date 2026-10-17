using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Tests.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

/// <summary>
/// An incident opens after three failing cycles, dated from the first of them, closes after two
/// good ones with its duration, keeps its first cause and notes a changed one in the timeline,
/// records step transitions while it is open, and is picked up again after a restart.
/// </summary>
[Collection("Localization")]
public sealed class IncidentTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);

    private static StepSnapshot Healthy(DateTimeOffset at) => new(
        [.. Enum.GetValues<StepId>().Select(id => FakeCheck.Result(id, StepState.Ok) with { TimestampUtc = at })], at);

    private static StepSnapshot Broken(DateTimeOffset at, StepId failing)
    {
        List<StepResult> results = [];
        foreach (StepId id in Enum.GetValues<StepId>())
        {
            StepResult result = id < failing
                ? FakeCheck.Result(id, StepState.Ok)
                : id == failing
                    ? FakeCheck.Result(id, StepState.Failed, $"{id} is down")
                    : FakeCheck.Result(id, StepState.Ok).AsSkipped("skipped", null, "step.skipped");
            results.Add(result with { TimestampUtc = at });
        }

        return new StepSnapshot(results, at);
    }

    private static (IncidentTracker Tracker, IncidentStore Store) Fresh()
    {
        var store = new IncidentStore();
        store.Clear();
        return (new IncidentTracker(store, new ChangeDetector()), store);
    }

    private static void Observe(IncidentTracker tracker, StepSnapshot snapshot) => tracker.Observe(snapshot, RootCauseAnalyzer.Analyze(snapshot));

    [Fact]
    public void Three_failing_cycles_open_an_incident_dated_from_the_first_and_two_good_ones_close_it()
    {
        (IncidentTracker tracker, IncidentStore store) = Fresh();
        var opened = new List<Incident>();
        var closed = new List<Incident>();
        tracker.IncidentOpened += opened.Add;
        tracker.IncidentClosed += closed.Add;

        Observe(tracker, Broken(T0, StepId.Gateway));
        Observe(tracker, Broken(T0.AddSeconds(15), StepId.Gateway));
        Assert.Null(tracker.ActiveIncident);

        Observe(tracker, Broken(T0.AddSeconds(30), StepId.Gateway));
        Incident incident = Assert.Single(opened);
        Assert.Same(incident, tracker.ActiveIncident);
        Assert.Equal(T0, incident.StartUtc);
        Assert.Equal(RootCause.Gateway, incident.Cause);
        Assert.Equal(IncidentStatus.Active, incident.Status);
        Assert.Single(store.All);

        Observe(tracker, Healthy(T0.AddSeconds(45)));
        Assert.NotNull(tracker.ActiveIncident);
        Assert.Empty(closed);

        Observe(tracker, Healthy(T0.AddSeconds(60)));
        Assert.Null(tracker.ActiveIncident);
        Incident done = Assert.Single(closed);
        Assert.Equal(IncidentStatus.Resolved, done.Status);
        Assert.Equal(T0.AddSeconds(60), done.EndUtc);
        Assert.Equal(TimeSpan.FromSeconds(60), done.Duration);
        Assert.Equal(IncidentStatus.Resolved, Assert.Single(new IncidentStore().All).Status);
    }

    [Fact]
    public void A_single_good_cycle_between_failures_does_not_reset_an_open_incident_but_does_reset_the_count_before_one()
    {
        (IncidentTracker tracker, _) = Fresh();

        Observe(tracker, Broken(T0, StepId.PublicIp));
        Observe(tracker, Broken(T0.AddSeconds(15), StepId.PublicIp));
        Observe(tracker, Healthy(T0.AddSeconds(30)));
        Observe(tracker, Broken(T0.AddSeconds(45), StepId.PublicIp));
        Observe(tracker, Broken(T0.AddSeconds(60), StepId.PublicIp));
        Assert.Null(tracker.ActiveIncident);

        Observe(tracker, Broken(T0.AddSeconds(75), StepId.PublicIp));
        Assert.NotNull(tracker.ActiveIncident);
        Assert.Equal(T0.AddSeconds(45), tracker.ActiveIncident.StartUtc);
    }

    [Fact]
    public void A_changed_cause_is_noted_in_the_timeline_and_the_first_cause_stays()
    {
        (IncidentTracker tracker, _) = Fresh();
        for (int i = 0; i < 3; i++)
        {
            Observe(tracker, Broken(T0.AddSeconds(15 * i), StepId.Gateway));
        }

        Observe(tracker, Broken(T0.AddSeconds(45), StepId.Dns));

        Incident incident = tracker.ActiveIncident!;
        Assert.Equal(RootCause.Gateway, incident.Cause);
        Assert.Contains(incident.Timeline, e => e.Text.StartsWith("Cause changed to", StringComparison.Ordinal));
    }

    [Fact]
    public void Step_transitions_are_noted_only_while_an_incident_is_open_only_when_they_change_and_at_most_two_hundred_times()
    {
        (IncidentTracker tracker, _) = Fresh();

        tracker.NoteStepChange(FakeCheck.Result(StepId.Dns, StepState.Failed));
        Assert.Null(tracker.ActiveIncident);

        for (int i = 0; i < 3; i++)
        {
            Observe(tracker, Broken(T0.AddSeconds(15 * i), StepId.Gateway));
        }

        int before = tracker.ActiveIncident!.Timeline.Count;
        tracker.NoteStepChange(FakeCheck.Result(StepId.Dns, StepState.Ok));
        tracker.NoteStepChange(FakeCheck.Result(StepId.Dns, StepState.Ok));
        Assert.Equal(before + 1, tracker.ActiveIncident.Timeline.Count);
        Assert.Contains("DNS recovered", tracker.ActiveIncident.Timeline[^1].Text);

        for (int i = 0; i < 500; i++)
        {
            tracker.NoteStepChange(FakeCheck.Result(StepId.Target, i % 2 == 0 ? StepState.Failed : StepState.Ok));
        }

        Assert.Equal(200, tracker.ActiveIncident.Timeline.Count);
    }

    [Fact]
    public void An_open_incident_in_the_store_is_resumed_by_a_new_tracker()
    {
        (IncidentTracker first, IncidentStore store) = Fresh();
        for (int i = 0; i < 3; i++)
        {
            Observe(first, Broken(T0.AddSeconds(15 * i), StepId.Gateway));
        }

        var resumed = new IncidentTracker(new IncidentStore(), new ChangeDetector());

        Assert.NotNull(resumed.ActiveIncident);
        Assert.Equal(first.ActiveIncident!.Id, resumed.ActiveIncident.Id);
        store.Clear();
    }

    [Theory]
    [InlineData(null, "-")]
    [InlineData(42, "42s")]
    [InlineData(125, "2m 5s")]
    [InlineData(3720, "1h 2m")]
    [InlineData(90000, "1d 1h")]
    public void Durations_read_naturally(int? seconds, string expected) =>
        Assert.Equal(expected, IncidentTracker.FormatDuration(seconds is { } s ? TimeSpan.FromSeconds(s) : null));
}
