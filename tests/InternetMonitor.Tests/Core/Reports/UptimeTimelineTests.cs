using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Reports;

[Collection("Localization")]
public sealed class UptimeTimelineTests
{
    // Mid-block on purpose: the last block is the quarter-hour under way.
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 7, 0, TimeSpan.Zero);

    [Fact]
    public void Ninety_six_blocks_ending_with_the_quarter_under_way()
    {
        UptimeOverview overview = Compute(Samples(Now.AddHours(-30), Now));

        Assert.Equal(UptimeTimeline.BlockCount, overview.Blocks.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero), overview.Blocks[^1].StartUtc);
        Assert.All(overview.Blocks, b => Assert.Equal(UptimeState.Ok, b.State));
    }

    [Fact]
    public void A_block_the_app_ran_for_less_than_half_of_is_not_measured()
    {
        DateTimeOffset block = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        List<MetricPoint> samples =
        [
            .. Samples(Now.AddHours(-30), block),
            .. Samples(block.AddMinutes(10), Now),
        ];

        UptimeOverview overview = Compute(samples);

        Assert.Equal(UptimeState.NotMeasured, BlockAt(overview, block).State);
        Assert.Equal(UptimeState.Ok, BlockAt(overview, block.AddMinutes(15)).State);
    }

    [Fact]
    public void An_outage_colours_its_blocks_and_names_its_incident_ahead_of_a_warning()
    {
        DateTimeOffset start = new(2026, 10, 15, 10, 5, 0, TimeSpan.Zero);
        Incident outage = Incident("INC-A", RootCause.Dns, start, TimeSpan.FromMinutes(20));
        Incident warning = Incident("INC-B", RootCause.NetworkHealth, start.AddMinutes(-4), TimeSpan.FromMinutes(14), conditionKey: "packetLoss");

        UptimeOverview overview = Compute(Samples(Now.AddHours(-30), Now), outage, warning);

        UptimeBlock first = BlockAt(overview, new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(UptimeState.Outage, first.State);
        Assert.Equal("INC-A", first.IncidentId);
        Assert.Equal(UptimeState.Outage, BlockAt(overview, start.AddMinutes(10)).State);
        Assert.Equal(UptimeState.Ok, BlockAt(overview, start.AddMinutes(40)).State);
    }

    [Fact]
    public void A_login_page_is_a_warning_not_an_outage()
    {
        DateTimeOffset start = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        UptimeOverview overview = Compute(Samples(Now.AddHours(-30), Now), Incident("INC-P", RootCause.CaptivePortal, start, TimeSpan.FromMinutes(5)));

        Assert.Equal(UptimeState.Warning, BlockAt(overview, start).State);
    }

    [Fact]
    public void An_outage_over_midnight_counts_towards_both_days()
    {
        DateTimeOffset start = new(2026, 10, 14, 23, 50, 0, TimeSpan.Zero);
        UptimeOverview overview = Compute(Samples(Now.AddDays(-8), Now), Incident("INC-N", RootCause.Internet, start, TimeSpan.FromMinutes(30)));

        UptimeDay yesterday = Assert.Single(overview.Days, d => d.Day == new DateOnly(2026, 10, 14));
        UptimeDay today = Assert.Single(overview.Days, d => d.Day == new DateOnly(2026, 10, 15));

        Assert.Equal(UptimeTimeline.DayCount, overview.Days.Count);
        Assert.Equal(UptimeState.Outage, yesterday.State);
        Assert.Equal(1 - (10.0 / (24 * 60)), yesterday.Availability!.Value, 3);
        Assert.Equal(UptimeState.Outage, today.State);
    }

    [Fact]
    public void A_day_without_measurements_is_not_measured()
    {
        UptimeOverview overview = Compute(Samples(Now.AddHours(-30), Now));

        Assert.Equal(UptimeState.NotMeasured, overview.Days[0].State);
    }

    private static UptimeBlock BlockAt(UptimeOverview overview, DateTimeOffset at) =>
        overview.Blocks.Single(b => at >= b.StartUtc && at < b.EndUtc);

    private static UptimeOverview Compute(IReadOnlyList<MetricPoint> adapter, params Incident[] incidents) =>
        UptimeTimeline.Compute(
            incidents,
            id => id == SeriesNames.For(StepId.Adapter) ? adapter : [],
            Now,
            TimeZoneInfo.Utc,
            300);

    private static List<MetricPoint> Samples(DateTimeOffset from, DateTimeOffset to)
    {
        var points = new List<MetricPoint>();
        for (DateTimeOffset t = from; t < to; t += TimeSpan.FromSeconds(15))
        {
            points.Add(new MetricPoint(t, 1, null));
        }

        return points;
    }

    private static Incident Incident(string id, RootCause cause, DateTimeOffset start, TimeSpan duration, string? conditionKey = null) => new()
    {
        Id = id,
        Cause = cause,
        StartUtc = start,
        EndUtc = start + duration,
        Status = IncidentStatus.Resolved,
        ConditionKey = conditionKey,
    };
}
