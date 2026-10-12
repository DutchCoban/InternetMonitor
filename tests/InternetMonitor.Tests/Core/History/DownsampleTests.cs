using InternetMonitor.Core.History;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>
/// Regression coverage for a real bug found by inspection: viewing a month-long Network adapter
/// history, a short 22-minute "connected" burst right at the very start of the series was missing
/// entirely from the chart - not thinned, not drawn thin, just absent.
///
/// The cause was an integer overflow, not a logic error. BucketState averaged each bucket's
/// timestamp by summing the raw .NET tick count of every sample that landed in it. A tick count
/// for a date in the 2020s is already around 6*10^17; summing more than about fourteen of them
/// overflows a 64-bit <see cref="long"/> (max ~9.2*10^18), which wraps around to a value that
/// either lands outside <see cref="DateTime"/>'s representable range (an exception) or lands
/// somewhere unrelated to the data it was supposed to represent (a misplaced or invisible
/// segment) - and almost any bucket with more than a handful of samples hit it.
/// </summary>
public sealed class DownsampleTests
{
    [Fact]
    public void BucketState_keeps_a_short_burst_separate_from_a_much_later_cluster()
    {
        DateTimeOffset burstStart = new(2026, 9, 16, 20, 48, 0, TimeSpan.Zero);
        DateTimeOffset burstEnd = burstStart.AddMinutes(22);
        DateTimeOffset clusterStart = new(2026, 10, 12, 17, 31, 0, TimeSpan.Zero);
        DateTimeOffset viewEnd = clusterStart.AddHours(3);

        List<MetricPoint> points =
        [
            .. Series(burstStart, burstEnd),
            .. Series(clusterStart, viewEnd),
        ];

        IReadOnlyList<MetricPoint> buckets = Downsample.BucketState(points, burstStart, viewEnd, bucketCount: 116);

        // Two separate events, nearly a month apart, must produce two separate slices - not be
        // merged, and not have one of them silently disappear.
        Assert.True(buckets.Count >= 2, "expected the burst and the cluster to produce separate slices");
        Assert.True(buckets[0].Timestamp < clusterStart, "the first slice should represent the early burst");
        Assert.True(buckets[^1].Timestamp > burstEnd, "the last slice should represent the later cluster");
    }

    [Fact]
    public void BucketState_does_not_overflow_when_many_samples_land_in_one_slice()
    {
        DateTimeOffset start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddHours(1);

        // One sample a second for an hour, into just four buckets: 900 samples per bucket, far
        // past the ~14-sample point where the old tick-sum accumulator overflowed.
        List<MetricPoint> points = Series(start, end, TimeSpan.FromSeconds(1));

        IReadOnlyList<MetricPoint> buckets = Downsample.BucketState(points, start, end, bucketCount: 4);

        Assert.Equal(4, buckets.Count);
        Assert.All(buckets, b => Assert.InRange(b.Timestamp, start, end));
    }

    [Fact]
    public void BucketState_reports_a_slice_with_only_down_readings_as_fully_down()
    {
        DateTimeOffset start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddMinutes(10);

        List<MetricPoint> points = [.. Enumerable.Range(0, 20).Select(i => new MetricPoint(start.AddSeconds(i * 15), 0.0, null))];

        IReadOnlyList<MetricPoint> buckets = Downsample.BucketState(points, start, end, bucketCount: 1);

        Assert.Equal(0.0, Assert.Single(buckets).Value);
    }

    [Fact]
    public void BucketState_reports_a_slice_with_mixed_readings_as_partial()
    {
        DateTimeOffset start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddMinutes(10);

        List<MetricPoint> points =
        [
            new MetricPoint(start.AddSeconds(15), 1.0, null),
            new MetricPoint(start.AddSeconds(30), 0.0, null), // one down reading amid otherwise-up ones
            new MetricPoint(start.AddSeconds(45), 1.0, null),
        ];

        IReadOnlyList<MetricPoint> buckets = Downsample.BucketState(points, start, end, bucketCount: 1);

        Assert.Equal(0.5, Assert.Single(buckets).Value);
    }

    [Fact]
    public void BucketState_leaves_out_slices_with_no_samples_instead_of_inventing_a_state()
    {
        DateTimeOffset start = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset end = start.AddHours(4);

        // Samples only in the first quarter of the range - the other three quarters are a genuine
        // gap (the app was closed, say) and must not appear as either connected or disconnected.
        List<MetricPoint> points = Series(start, start.AddHours(1));

        IReadOnlyList<MetricPoint> buckets = Downsample.BucketState(points, start, end, bucketCount: 4);

        MetricPoint only = Assert.Single(buckets);
        Assert.True(only.Timestamp < start.AddHours(1));
    }

    private static List<MetricPoint> Series(DateTimeOffset from, DateTimeOffset to, TimeSpan? interval = null)
    {
        TimeSpan step = interval ?? TimeSpan.FromSeconds(15);
        var points = new List<MetricPoint>();
        for (DateTimeOffset t = from; t <= to; t += step)
        {
            points.Add(new MetricPoint(t, 1.0, null));
        }

        return points;
    }
}
