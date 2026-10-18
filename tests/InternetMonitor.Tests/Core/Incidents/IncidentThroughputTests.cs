using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

/// <summary>
/// The traffic picture around an incident: where the four windows lie, what they summarise, how
/// capacity turns rates into a share, when the after-window counts as measured, how a stored
/// record is completed from history, and what the lines for an investigator say.
/// </summary>
[Collection("Localization")]
public sealed class IncidentThroughputTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(3);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>Traffic every 15 s from 25 minutes before the start to 10 minutes after the end: a 20 Mbps hum, a burst to 125 Mbps one minute before the fault, nothing during it, 40 Mbps after.</summary>
    private static Dictionary<string, IReadOnlyList<MetricPoint>> Story()
    {
        var down = new List<MetricPoint>();
        var up = new List<MetricPoint>();
        for (DateTimeOffset t = Start.AddMinutes(-25); t <= End.AddMinutes(10); t += Interval)
        {
            double rate = t < Start.AddMinutes(-1) ? 20
                : t < Start ? 125
                : t < End ? 0
                : 40;
            double peak = t == Start.AddSeconds(-45) ? 140 : rate;
            down.Add(new MetricPoint(t, rate, TrafficHistory.Metadata(peak)));
            up.Add(new MetricPoint(t, rate / 10, TrafficHistory.Metadata(peak / 10)));
        }

        return new Dictionary<string, IReadOnlyList<MetricPoint>>
        {
            [SeriesNames.TrafficDown] = down,
            [SeriesNames.TrafficUp] = up,
            [SeriesNames.SpeedTestDownload] = [new MetricPoint(Start.AddHours(-2), 0.3, null), new MetricPoint(Start.AddHours(-1), 200, null), new MetricPoint(Start.AddHours(1), 300, null)],
            [SeriesNames.SpeedTestUpload] = [new MetricPoint(Start.AddHours(-1), 20, null)],
        };
    }

    private static Func<string, IReadOnlyList<MetricPoint>> History(Dictionary<string, IReadOnlyList<MetricPoint>> series) =>
        id => series.GetValueOrDefault(id, []);

    private static Incident Resolved() => new() { Id = "INC-1", Cause = RootCause.Internet, StartUtc = Start, EndUtc = End, Status = IncidentStatus.Resolved };

    [Fact]
    public void The_windows_lie_where_the_owner_put_them_and_are_half_open()
    {
        IncidentThroughput traffic = IncidentThroughputSummary.Compute(Resolved(), History(Story()), 200, 20, ThroughputCapacitySource.SpeedTest, End.AddMinutes(10), Interval);

        Assert.Equal((Start.AddMinutes(-20), Start.AddMinutes(-5)), (traffic.Baseline!.FromUtc, traffic.Baseline.ToUtc));
        Assert.Equal((Start.AddMinutes(-5), Start), (traffic.Before!.FromUtc, traffic.Before.ToUtc));
        Assert.Equal((Start, End), (traffic.During!.FromUtc, traffic.During.ToUtc));
        Assert.Equal((End, End.AddMinutes(5)), (traffic.After!.FromUtc, traffic.After.ToUtc));

        Assert.Equal(60, traffic.Baseline.Samples);   // 15 min / 15 s, the point at exactly -5 min belongs to the next window
        Assert.Equal(20, traffic.Before.Samples);
        Assert.Equal(12, traffic.During.Samples);
        Assert.Equal(20, traffic.After.Samples);
        Assert.False(traffic.AfterPending);
    }

    [Fact]
    public void Each_window_reports_its_average_its_stored_peak_and_when_the_peak_was()
    {
        IncidentThroughput traffic = IncidentThroughputSummary.Compute(Resolved(), History(Story()), 200, 20, ThroughputCapacitySource.SpeedTest, End.AddMinutes(10), Interval);

        Assert.Equal(20, traffic.Baseline!.AverageDownMbps, 6);
        Assert.Equal(2, traffic.Baseline.AverageUpMbps, 6);
        Assert.Equal(20, traffic.Baseline.PeakDownMbps);

        // 16 points at 20, 4 at 125 → 41; the stored peak of 140 wins over the point values.
        Assert.Equal(41, traffic.Before!.AverageDownMbps, 6);
        Assert.Equal(140, traffic.Before.PeakDownMbps);
        Assert.Equal(Start.AddSeconds(-45), traffic.Before.PeakAtUtc);

        Assert.Equal(0, traffic.During!.AverageDownMbps);
        Assert.Equal(40, traffic.After!.AverageDownMbps, 6);
    }

    [Fact]
    public void Utilization_is_the_busier_direction_against_capacity_and_null_without_one()
    {
        Func<string, IReadOnlyList<MetricPoint>> history = History(Story());

        ThroughputWindow with = IncidentThroughputSummary.Window(history, Start.AddMinutes(-5), Start, 200, 20)!;
        Assert.Equal(0.205, with.AverageUtilization!.Value, 6);   // 41/200 vs 4.1/20
        Assert.Equal(0.7, with.PeakUtilization!.Value, 6);       // 140/200 vs 14/20

        ThroughputWindow upOnly = IncidentThroughputSummary.Window(history, Start.AddMinutes(-5), Start, null, 10)!;
        Assert.Equal(1.4, upOnly.PeakUtilization!.Value, 6);     // 14/10: the busier direction, past 100%

        ThroughputWindow without = IncidentThroughputSummary.Window(history, Start.AddMinutes(-5), Start, null, null)!;
        Assert.Null(without.AverageUtilization);
        Assert.Null(without.PeakUtilization);

        Assert.Null(IncidentThroughputSummary.Window(history, Start.AddHours(-3), Start.AddHours(-2), 200, 20));
        Assert.Null(IncidentThroughputSummary.Window(history, Start, Start, 200, 20));
    }

    [Fact]
    public void The_after_window_waits_until_it_has_fully_elapsed_and_an_open_incident_has_none()
    {
        Func<string, IReadOnlyList<MetricPoint>> history = History(Story());

        IncidentThroughput early = IncidentThroughputSummary.Compute(Resolved(), history, 200, 20, ThroughputCapacitySource.SpeedTest, End.AddMinutes(5), Interval);
        Assert.Null(early.After);
        Assert.True(early.AfterPending);
        Assert.False(IncidentThroughputSummary.AfterWindowDue(Resolved(), End.AddMinutes(5), Interval));
        Assert.True(IncidentThroughputSummary.AfterWindowDue(Resolved(), End.AddMinutes(5).Add(Interval), Interval));

        Incident open = Resolved() with { EndUtc = null, Status = IncidentStatus.Active };
        IncidentThroughput ongoing = IncidentThroughputSummary.Compute(open, history, 200, 20, ThroughputCapacitySource.SpeedTest, Start.AddMinutes(2), Interval);
        Assert.Equal((Start, Start.AddMinutes(2)), (ongoing.During!.FromUtc, ongoing.During.ToUtc));
        Assert.Null(ongoing.After);
        Assert.True(ongoing.AfterPending);
    }

    [Fact]
    public void Capacity_comes_from_the_last_real_speed_test_at_or_before_the_start()
    {
        (double? down, double? up) = IncidentThroughputSummary.SpeedTestCapacityBefore(History(Story()), Start);
        Assert.Equal((200, 20), (down, up));

        // Before the only real download test: the failed 0.3 Mbit/s result counts for nothing.
        (double? none, _) = IncidentThroughputSummary.SpeedTestCapacityBefore(History(Story()), Start.AddMinutes(-90));
        Assert.Null(none);
    }

    [Fact]
    public void Resolve_fills_only_what_the_stored_record_lacks_and_keeps_its_capacity()
    {
        Incident incident = Resolved();
        var before = new ThroughputWindow(Start.AddMinutes(-5), Start, 20, 99, 9, 99, 9, Start.AddMinutes(-1), 0.5, 0.5);
        incident.Throughput = new IncidentThroughput(null, before, null, null, 198, 18, ThroughputCapacitySource.LinkSpeed) { AfterPending = true };

        IncidentThroughput resolved = IncidentThroughputSummary.Resolve(incident, History(Story()), End.AddMinutes(10), Interval)!;

        Assert.Same(before, resolved.Before);
        Assert.NotNull(resolved.Baseline);
        Assert.NotNull(resolved.During);
        Assert.NotNull(resolved.After);
        Assert.False(resolved.AfterPending);
        Assert.Equal(ThroughputCapacitySource.LinkSpeed, resolved.CapacitySource);
        Assert.Equal(198, resolved.CapacityDownMbps);
        Assert.Equal(4.0 / 18, resolved.After!.AverageUtilization!.Value, 6);   // upload 4/18 is the busier direction, not download 40/198

        Incident bare = Resolved();
        IncidentThroughput fromHistory = IncidentThroughputSummary.Resolve(bare, History(Story()), End.AddMinutes(10), Interval)!;
        Assert.Equal(ThroughputCapacitySource.SpeedTest, fromHistory.CapacitySource);
        Assert.Equal(200, fromHistory.CapacityDownMbps);

        Incident ancient = Resolved() with { StartUtc = Start.AddDays(-40), EndUtc = Start.AddDays(-40).AddMinutes(3) };
        Assert.Null(IncidentThroughputSummary.Resolve(ancient, History(Story()), End, Interval));

        Assert.True(IncidentThroughputSummary.NeedsCompletion(incident with { Throughput = incident.Throughput }, End.AddMinutes(10), Interval));
        Assert.False(IncidentThroughputSummary.NeedsCompletion(bare, End.AddMinutes(10), Interval));
    }

    [Fact]
    public void The_lines_for_an_investigator_name_each_window_the_peak_moment_the_capacity_and_the_limit()
    {
        IncidentThroughput traffic = IncidentThroughputSummary.Compute(Resolved(), History(Story()), 200, 20, ThroughputCapacitySource.SpeedTest, End.AddMinutes(10), Interval);

        IReadOnlyList<string> lines = traffic.Describe(LocalizationManager.GetEnglish, LocalizationManager.FormatEnglish);

        Assert.Equal(6, lines.Count);
        Assert.StartsWith("Normal (the 15 min before that): average 20.0 / 2.0 Mbit/s (10% of capacity), peak 20.0 / 2.0 Mbit/s (10%) (60 samples)", lines[0]);
        Assert.Contains("5 min before: average 41.0 / 4.1 Mbit/s (21% of capacity), peak 140.0 / 14.0 Mbit/s (70%) at ", lines[1]);
        Assert.EndsWith(" (20 samples)", lines[1]);
        Assert.StartsWith("During: average 0.0 / 0.0 Mbit/s", lines[2]);
        Assert.StartsWith("5 min after: average 40.0 / 4.0 Mbit/s", lines[3]);
        Assert.Equal("Capacity: 200.0 / 20.0 Mbit/s, from the last speed test", lines[4]);
        Assert.Equal("Only this computer's traffic is visible; other devices on the same line are not.", lines[5]);

        IncidentThroughput pending = traffic with { After = null, AfterPending = true, CapacitySource = ThroughputCapacitySource.None, Baseline = null };
        IReadOnlyList<string> partial = pending.Describe(LocalizationManager.GetEnglish, LocalizationManager.FormatEnglish);
        Assert.Equal("Normal (the 15 min before that): no measurements", partial[0]);
        Assert.Equal("5 min after: not measured yet", partial[3]);
        Assert.Equal("Capacity unknown - run a speed test to see utilization", partial[4]);
    }
}
