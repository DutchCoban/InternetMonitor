using InternetMonitor.Core.History;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>How per-cycle traffic readings fold into one stored point: a time-weighted average and the fastest cycle.</summary>
public sealed class TrafficAccumulatorTests
{
    [Fact]
    public void The_average_is_weighted_by_each_cycles_length_and_the_peak_is_the_fastest_cycle()
    {
        var traffic = new TrafficAccumulator();
        traffic.Add(new ThroughputSample(100, 10, null, 3));
        traffic.Add(new ThroughputSample(0, 0, null, 3));
        traffic.Add(new ThroughputSample(10, 1, null, 9));

        TrafficInterval interval = traffic.Current!.Value;

        // (100*3 + 0*3 + 10*9) / 15 = 26
        Assert.Equal(26, interval.AvgDownMbps, 6);
        Assert.Equal(2.6, interval.AvgUpMbps, 6);
        Assert.Equal(100, interval.PeakDownMbps);
        Assert.Equal(10, interval.PeakUpMbps);
        Assert.Equal(15, interval.Seconds);
    }

    [Fact]
    public void A_sample_without_an_interval_counts_as_one_second()
    {
        var traffic = new TrafficAccumulator();
        traffic.Add(new ThroughputSample(30, 3, null));
        traffic.Add(new ThroughputSample(10, 1, null));

        Assert.Equal(20, traffic.Current!.Value.AvgDownMbps, 6);
    }

    [Fact]
    public void Nothing_added_is_null_and_a_reset_starts_over()
    {
        var traffic = new TrafficAccumulator();
        Assert.Null(traffic.Current);

        traffic.Add(new ThroughputSample(100, 10, null, 3));
        traffic.Reset();
        Assert.Null(traffic.Current);

        traffic.Add(new ThroughputSample(4, 2, null, 3));
        Assert.Equal(4, traffic.Current!.Value.PeakDownMbps);
    }
}
