using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>What one finished cycle leaves in the history, series by series, and how the outage line marks an incident's edges.</summary>
public sealed class HistoryRecorderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"im-recorder-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static StepResult Result(StepId id, StepState state, double? chart, StepFacts? facts = null) =>
        new(id, state, id.ToString(), null, chart, TimeSpan.Zero, T0, [], facts);

    private static TargetEndpointResult Endpoint(string id, bool reachable, double totalMs) =>
        new("n", TargetKind.Url, "https://x/", reachable, reachable ? 200 : null, null, HttpStage.None, null,
            reachable ? new HttpTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(totalMs)) : null,
            IPAddress.Loopback, null) { EndpointId = id };

    [Fact]
    public async Task Every_series_a_cycle_can_feed_is_written_and_skipped_or_unmeasured_steps_are_not()
    {
        await using var store = new MetricStore(TimeSpan.FromDays(1), _directory);
        await store.Loaded;

        var wifi = new WifiInfo("Home", "aa:bb:cc:dd:ee:ff", 70, -55, 36, 5180, "Wi-Fi 6", 600, 600, false);
        var link = new LinkFacts(PhysicalLinkState.Unknown, OperationalStatus.Up, true, "Wi-Fi", wifi, new ConnectionCostInfo(true, true, false, false));
        var targets = new TargetFacts([Endpoint("e1", true, 123.4), Endpoint("e2", false, 0)], Ipv6Reachable: true);
        var snapshot = new StepSnapshot(
        [
            Result(StepId.Link, StepState.Ok, 1, link),
            Result(StepId.Gateway, StepState.Ok, 12),
            Result(StepId.Dns, StepState.Ok, 5).AsSkipped("skipped", null, "step.skipped"),
            Result(StepId.PublicIp, StepState.Ok, null),
            Result(StepId.Target, StepState.Ok, 123.4, targets),
        ], T0)
        {
            Throughput = new ThroughputSample(12.3456, 1.5, 0.2),
        };

        HistoryRecorder.Record(store, snapshot);

        Assert.Equal([12], store.Get(SeriesNames.For(StepId.Gateway)!).Select(p => p.Value));
        Assert.Empty(store.Get(SeriesNames.For(StepId.Dns)!));
        Assert.Empty(store.Get(SeriesNames.For(StepId.PublicIp)!));
        Assert.Equal([1], store.Get(SeriesNames.For(StepId.Link)!).Select(p => p.Value));

        MetricPoint signal = Assert.Single(store.Get(SeriesNames.WifiSignal));
        Assert.Equal((70, "aa:bb:cc:dd:ee:ff"), (signal.Value, signal.Metadata));
        Assert.Equal([1], store.Get(SeriesNames.Ipv6).Select(p => p.Value));
        Assert.Equal([123], store.Get(SeriesNames.ForEndpoint("e1")).Select(p => p.Value));
        Assert.Empty(store.Get(SeriesNames.ForEndpoint("e2")));
        Assert.Equal([1], store.Get(SeriesNames.Metered).Select(p => p.Value));
        Assert.Equal([12.35], store.Get(SeriesNames.TrafficDown).Select(p => p.Value));
        Assert.Equal([1.5], store.Get(SeriesNames.TrafficUp).Select(p => p.Value));
    }

    [Fact]
    public async Task A_cycle_without_a_target_step_or_traffic_writes_nothing_for_them()
    {
        await using var store = new MetricStore(TimeSpan.FromDays(1), _directory);
        await store.Loaded;

        HistoryRecorder.Record(store, new StepSnapshot([Result(StepId.Gateway, StepState.Failed, null)], T0));

        Assert.Empty(store.SeriesIds);
    }

    [Fact]
    public async Task The_outage_line_records_each_change_at_once_and_samples_in_between()
    {
        await using var store = new MetricStore(TimeSpan.FromDays(1), _directory);
        await store.Loaded;
        var incident = new Incident { Id = "INC-20261018-090030.000", Cause = RootCause.Gateway, StartUtc = T0 };

        string? state = HistoryRecorder.RecordOutage(store, null, T0, string.Empty);
        Assert.Null(state);
        state = HistoryRecorder.RecordOutage(store, null, T0.AddSeconds(3), state);
        state = HistoryRecorder.RecordOutage(store, incident, T0.AddSeconds(6), state);
        Assert.Equal("gateway|INC-20261018-090030.000", state);
        state = HistoryRecorder.RecordOutage(store, incident, T0.AddSeconds(9), state);
        state = HistoryRecorder.RecordOutage(store, null, T0.AddSeconds(12), state);
        Assert.Null(state);

        // Each change is written at its exact moment; the samples in between keep to the store's
        // own interval among themselves (the one at +9 s falls within 15 s of the one at +3 s).
        IReadOnlyList<MetricPoint> outage = store.Get(SeriesNames.Outage);
        Assert.Equal([0, 0, 1, 0], outage.Select(p => p.Value));
        Assert.Equal([T0, T0.AddSeconds(3), T0.AddSeconds(6), T0.AddSeconds(12)], outage.Select(p => p.Timestamp));
        Assert.Equal("gateway|INC-20261018-090030.000", outage[2].Metadata);
        Assert.Null(outage[3].Metadata);
    }
}
