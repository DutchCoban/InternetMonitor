using InternetMonitor.Core.History;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>The peak a traffic point carries in its metadata, and what an old point without one reads as.</summary>
public sealed class TrafficHistoryTests
{
    [Fact]
    public void The_peak_round_trips_invariantly()
    {
        string metadata = TrafficHistory.Metadata(125.456);

        Assert.Equal("peak=125.46", metadata);
        Assert.Equal(125.46, TrafficHistory.PeakOf(metadata));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("peak=")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("peak=abc")]
    public void Anything_else_is_not_a_peak(string? metadata)
    {
        Assert.Null(TrafficHistory.PeakOf(metadata));
    }

    [Fact]
    public void A_point_without_a_stored_peak_uses_its_own_value()
    {
        var old = new MetricPoint(DateTimeOffset.UnixEpoch, 12.5, null);
        var stored = new MetricPoint(DateTimeOffset.UnixEpoch, 12.5, "peak=40.00");

        Assert.Equal(12.5, TrafficHistory.PeakOf(old));
        Assert.Equal(40, TrafficHistory.PeakOf(stored));
    }
}
