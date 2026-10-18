using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

/// <summary>
/// incidents.json round-trips every field, survives v1 records and damage, and the time-based id
/// never repeats - the evidence an outage leaves behind must come back exactly as recorded.
/// </summary>
[Collection("Localization")]
public sealed class IncidentStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 17, 19, 38, 16, 729, TimeSpan.Zero);

    private static Incident Full() => new()
    {
        Id = "INC-20261017-213816.729",
        Cause = RootCause.Internet,
        StartUtc = Start,
        EndUtc = Start.AddMinutes(4),
        Status = IncidentStatus.Resolved,
        Headline = "No public address reachable",
        Explanation = new Explanation("what", "cause", "todo"),
        AffectedSteps = [StepId.PublicIp, StepId.Dns, StepId.Target],
        Timeline = { new TimelineEntry(Start, "Incident opened"), new TimelineEntry(Start.AddMinutes(4), "Resolved after 4 min") },
        StepsAtStart = [new StepSnapshotEntry(StepId.PublicIp, StepState.Failed, "No public address reachable", "detail", new Dictionary<string, string> { ["Latency"] = "- ms" })],
        RecentChanges = ["Gateway changed from 10.0.0.1 to 10.0.0.2"],
        ConditionKey = null,
        Subject = "{eth}",
        Note = "Ticket 4711",
        Traceroute = new IncidentTraceroute("9.9.9.9", Start, false, [new IncidentHop(1, "10.0.0.1", "router.lan", null, 1.5), new IncidentHop(2, null, null, null, null)]),
        Throughput = new IncidentThroughput(
            new ThroughputWindow(Start.AddMinutes(-20), Start.AddMinutes(-5), 60, 20.5, 2.1, 24, 2.5, Start.AddMinutes(-12), 0.1, 0.12),
            new ThroughputWindow(Start.AddMinutes(-5), Start, 20, 41, 4.1, 140, 14, Start.AddSeconds(-45), 0.205, 0.7),
            new ThroughputWindow(Start, Start.AddMinutes(4), 16, 0, 0, 0, 0, Start, 0, 0),
            null,
            200,
            20,
            ThroughputCapacitySource.SpeedTest)
        {
            AfterPending = true,
        },
    };

    [Fact]
    public void Every_field_comes_back_after_a_restart()
    {
        var store = new IncidentStore();
        store.Clear();
        Incident incident = Full();
        store.Add(incident);

        Incident loaded = Assert.Single(new IncidentStore().All);

        Assert.Equal(incident.Id, loaded.Id);
        Assert.Equal(incident.Uuid, loaded.Uuid);
        Assert.Equal(RootCause.Internet, loaded.Cause);
        Assert.Equal(incident.StartUtc, loaded.StartUtc);
        Assert.Equal(incident.EndUtc, loaded.EndUtc);
        Assert.Equal(IncidentStatus.Resolved, loaded.Status);
        Assert.Equal(incident.Headline, loaded.Headline);
        Assert.Equal(incident.Explanation, loaded.Explanation);
        Assert.Equal(incident.AffectedSteps, loaded.AffectedSteps);
        Assert.Equal(incident.Timeline, loaded.Timeline);
        Assert.Equal(incident.RecentChanges, loaded.RecentChanges);
        Assert.Equal("Ticket 4711", loaded.Note);
        Assert.Equal("{eth}", loaded.Subject);
        Assert.Equal(incident.Traceroute!.Target, loaded.Traceroute!.Target);
        Assert.Equal(incident.Traceroute.Hops, loaded.Traceroute.Hops);
        Assert.Equal(incident.Throughput, loaded.Throughput);
        Assert.True(loaded.Throughput!.AfterPending);
        Assert.Null(loaded.Throughput.After);
        StepSnapshotEntry step = Assert.Single(loaded.StepsAtStart);
        Assert.Equal(StepId.PublicIp, step.Step);
        Assert.Equal("- ms", step.Measurements["Latency"]);
    }

    [Fact]
    public void Update_replaces_the_record_and_clear_empties_the_file()
    {
        var store = new IncidentStore();
        store.Clear();
        Incident open = Full() with { EndUtc = null, Status = IncidentStatus.Active };
        store.Add(open);

        store.Update(open with { EndUtc = Start.AddMinutes(9), Status = IncidentStatus.Resolved });
        Incident loaded = Assert.Single(new IncidentStore().All);
        Assert.Equal(IncidentStatus.Resolved, loaded.Status);
        Assert.Equal(Start.AddMinutes(9), loaded.EndUtc);

        store.Clear();
        Assert.Empty(new IncidentStore().All);
        Assert.Equal("[]", File.ReadAllText(AppPaths.IncidentsFile));
    }

    [Fact]
    public void A_subject_is_left_out_of_the_file_when_there_is_none()
    {
        var store = new IncidentStore();
        store.Clear();
        store.Add(Full() with { Subject = null });

        Assert.DoesNotContain("\"Subject\"", File.ReadAllText(AppPaths.IncidentsFile));
        Assert.Null(Assert.Single(new IncidentStore().All).Subject);
    }

    [Fact]
    public void Two_incidents_in_the_same_millisecond_get_different_ids()
    {
        var store = new IncidentStore();
        store.Clear();

        string first = store.NextId(Start);
        store.Add(Full() with { Id = first });
        string second = store.NextId(Start);

        Assert.NotEqual(first, second);
        Assert.Equal(IncidentStore.IdFor(Start), first);
        Assert.Equal(IncidentStore.IdFor(Start.AddMilliseconds(1)), second);
    }

    [Fact]
    public void A_v1_record_loads_and_a_broken_one_is_skipped_and_an_old_record_gets_a_uuid()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.IncidentsFile, """
            [
              { "Id": "INC-20250101-0001", "Classification": "Gateway", "StartUtc": "2025-01-01T10:00:00+00:00", "EndUtc": null,
                "Diagnosis": "Router did not answer", "SnapshotAtStart": { "gateway": { "Summary": "10.0.0.1 timed out" } } },
              "not an incident at all",
              { "Id": "INC-20261001-0002", "Cause": "Dns", "StartUtc": "2026-10-01T08:00:00+00:00", "EndUtc": "2026-10-01T08:05:00+00:00",
                "Status": "Resolved", "Headline": "DNS failed" }
            ]
            """);

        IReadOnlyList<Incident> all = new IncidentStore().All;

        Assert.Equal(2, all.Count);
        Incident legacy = all.Single(i => i.Id == "INC-20250101-0001");
        Assert.Equal(RootCause.Gateway, legacy.Cause);
        Assert.Equal(IncidentStatus.Active, legacy.Status);
        Assert.Equal("Router did not answer", legacy.Headline);
        Assert.Contains("gateway: 10.0.0.1 timed out", legacy.RecentChanges);
        Incident current = all.Single(i => i.Id == "INC-20261001-0002");
        Assert.Equal(RootCause.Dns, current.Cause);
        Assert.NotEqual(Guid.Empty, current.Uuid);
    }

    [Fact]
    public void A_damaged_file_means_no_incidents_not_a_crash()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.IncidentsFile, "{ this is not json");

        Assert.Empty(new IncidentStore().All);
    }
}
