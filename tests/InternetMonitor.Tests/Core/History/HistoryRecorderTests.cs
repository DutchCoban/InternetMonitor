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

        HistoryRecorder.Record(store, snapshot, new TrafficAccumulator());

        Assert.Equal([12], store.Get(SeriesNames.For(StepId.Gateway)!).Select(p => p.Value));
        Assert.Empty(store.Get(SeriesNames.For(StepId.Dns)!));
        Assert.Empty(store.Get(SeriesNames.DnsFailures));
        Assert.Empty(store.Get(SeriesNames.For(StepId.PublicIp)!));
        Assert.Equal([1], store.Get(SeriesNames.For(StepId.Link)!).Select(p => p.Value));

        MetricPoint signal = Assert.Single(store.Get(SeriesNames.WifiSignal));
        Assert.Equal((70, "aa:bb:cc:dd:ee:ff"), (signal.Value, signal.Metadata));
        Assert.Equal([1], store.Get(SeriesNames.Ipv6).Select(p => p.Value));
        Assert.Equal([123], store.Get(SeriesNames.ForEndpoint("e1")).Select(p => p.Value));
        Assert.Empty(store.Get(SeriesNames.ForEndpoint("e2")));
        Assert.Equal([1], store.Get(SeriesNames.Metered).Select(p => p.Value));
        MetricPoint down = Assert.Single(store.Get(SeriesNames.TrafficDown));
        Assert.Equal((12.35, "peak=12.35"), (down.Value, down.Metadata));
        Assert.Equal([1.5], store.Get(SeriesNames.TrafficUp).Select(p => p.Value));
    }

    [Fact]
    public async Task Foreground_cycles_fold_into_one_traffic_point_per_interval_with_the_fastest_cycle_as_its_peak()
    {
        await using var store = new MetricStore(TimeSpan.FromDays(1), _directory) { MinimumSampleInterval = TimeSpan.FromSeconds(15) };
        await store.Loaded;
        var traffic = new TrafficAccumulator();
        double[] rates = [10, 10, 100, 10, 10, 10, 10];

        for (int i = 0; i < rates.Length; i++)
        {
            var snapshot = new StepSnapshot([], T0.AddSeconds(3 * i)) { Throughput = new ThroughputSample(rates[i], rates[i] / 10, null, 3) };
            HistoryRecorder.Record(store, snapshot, traffic);
        }

        // The first cycle is stored on its own (nothing before it); the point 15 s later averages
        // the five cycles since, the 100 among them, and keeps that 100 as the peak.
        IReadOnlyList<MetricPoint> points = store.Get(SeriesNames.TrafficDown);
        Assert.Equal([T0, T0.AddSeconds(15)], points.Select(p => p.Timestamp));
        Assert.Equal((10, "peak=10.00"), (points[0].Value, points[0].Metadata));
        Assert.Equal((28, "peak=100.00"), (points[1].Value, points[1].Metadata));
        Assert.Equal(2.8, Assert.Single(store.Get(SeriesNames.TrafficUp), p => p.Timestamp == T0.AddSeconds(15)).Value);

        // A cycle without a reading starts the interval over: the next stored point covers only what came after it.
        HistoryRecorder.Record(store, new StepSnapshot([], T0.AddSeconds(21)), traffic);
        HistoryRecorder.Record(store, new StepSnapshot([], T0.AddSeconds(24)) { Throughput = new ThroughputSample(50, 5, null, 3) }, traffic);
        HistoryRecorder.Record(store, new StepSnapshot([], T0.AddSeconds(30)) { Throughput = new ThroughputSample(70, 7, null, 6) }, traffic);
        MetricPoint third = store.Get(SeriesNames.TrafficDown)[2];
        Assert.Equal((T0.AddSeconds(30), 63.33, "peak=70.00"), (third.Timestamp, third.Value, third.Metadata));
    }

    [Fact]
    public async Task A_failed_dns_step_is_recorded_as_an_unanswered_lookup_with_the_steps_conclusion()
    {
        await using var store = new MetricStore(TimeSpan.FromDays(1), _directory);
        await store.Loaded;
        StepResult failed = Result(StepId.Dns, StepState.Failed, null) with { HeadlineKey = "step.dns.ownResolverFailed" };

        HistoryRecorder.Record(store, new StepSnapshot([failed], T0), new TrafficAccumulator());
        HistoryRecorder.Record(store, new StepSnapshot([Result(StepId.Dns, StepState.Ok, 31) with { TimestampUtc = T0.AddSeconds(30) }], T0.AddSeconds(30)), new TrafficAccumulator());

        MetricPoint point = Assert.Single(store.Get(SeriesNames.DnsFailures));
        Assert.Equal((T0, 1, "ownResolverFailed"), (point.Timestamp, point.Value, point.Metadata));
        Assert.DoesNotContain(store.Get(SeriesNames.For(StepId.Dns)!), p => p.Timestamp == T0);
        Assert.Equal([31], store.Get(SeriesNames.For(StepId.Dns)!).Select(p => p.Value));
    }

    [Fact]
    public async Task A_cycle_without_a_target_step_or_traffic_writes_nothing_for_them()
    {
        await using var store = new MetricStore(TimeSpan.FromDays(1), _directory);
        await store.Loaded;

        HistoryRecorder.Record(store, new StepSnapshot([Result(StepId.Gateway, StepState.Failed, null)], T0), new TrafficAccumulator());

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
