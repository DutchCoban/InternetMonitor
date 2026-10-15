using InternetMonitor.Core.History;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Reports;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

public sealed class LineQualityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_clean_burst_has_no_loss_and_jitter_is_the_mean_step_between_replies()
    {
        LineQualitySample sample = LineQualitySampler.Compute([10, 12, 10, 14], At)!;

        Assert.Equal(0, sample.LossPercent);
        Assert.Equal((2 + 2 + 4) / 3.0, sample.JitterMs!.Value, 6);
        Assert.Equal(11, sample.MedianMs);
    }

    [Fact]
    public void Lost_echoes_count_as_loss_and_are_skipped_for_jitter()
    {
        LineQualitySample sample = LineQualitySampler.Compute([10, null, 20, null, null, 20, 10, 10, 10, 10], At)!;

        Assert.Equal(30, sample.LossPercent);

        // Replies in order: 10, 20, 20, 10, 10, 10, 10 - steps 10, 0, 10, 0, 0, 0.
        Assert.Equal(20 / 6.0, sample.JitterMs!.Value, 6);
    }

    [Fact]
    public void One_reply_gives_loss_but_no_jitter()
    {
        LineQualitySample sample = LineQualitySampler.Compute([null, 15, null, null], At)!;

        Assert.Equal(75, sample.LossPercent);
        Assert.Null(sample.JitterMs);
    }

    [Fact]
    public void A_burst_with_no_reply_at_all_is_not_a_sample()
    {
        Assert.Null(LineQualitySampler.Compute([null, null, null], At));
        Assert.Null(LineQualitySampler.Compute([], At));
    }

    [Fact]
    public void The_report_averages_loss_and_jitter_per_local_day()
    {
        DateTimeOffset day1 = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset day2 = day1.AddDays(1);
        var series = new Dictionary<string, IReadOnlyList<MetricPoint>>
        {
            [SeriesNames.PacketLoss] = [new(day1, 0, null), new(day1.AddMinutes(1), 10, null), new(day2, 0, null), new(day2.AddMinutes(1), 0, null)],
            [SeriesNames.Jitter] = [new(day1, 2, null), new(day1.AddMinutes(1), 4, null), new(day2, 1, null)],
        };

        ProviderReportData data = ProviderReportData.Compute(
            [],
            id => series.TryGetValue(id, out IReadOnlyList<MetricPoint>? points) ? points : [],
            day1.AddHours(-1),
            day2.AddHours(1),
            TimeZoneInfo.Utc);

        Assert.Equal(2, data.QualityByDay.Count);
        DayQuality first = data.QualityByDay[0];
        Assert.Equal(5, first.LossPercent);
        Assert.Equal(3, first.JitterMs);
        Assert.Equal(1, first.BurstsWithLoss);
        Assert.Equal(2, first.Bursts);
        Assert.Equal(2.5, data.AverageLossPercent);
    }
}
