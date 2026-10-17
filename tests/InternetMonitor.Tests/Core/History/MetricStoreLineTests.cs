using InternetMonitor.Core.History;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>
/// A damaged line in a history file must be skipped, never thrown over: an exception while
/// loading would leave every report and export waiting on a load that never completes.
/// </summary>
public sealed class MetricStoreLineTests
{
    [Fact]
    public void A_well_formed_line_parses()
    {
        var at = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        Assert.True(MetricStore.TryParseLine($"{at.UtcTicks},12.5,gateway|INC-1", out MetricPoint point));
        Assert.Equal(at, point.Timestamp);
        Assert.Equal(12.5, point.Value);
        Assert.Equal("gateway|INC-1", point.Metadata);

        Assert.True(MetricStore.TryParseLine($"{at.UtcTicks},1", out point));
        Assert.Null(point.Metadata);
    }

    [Theory]
    [InlineData("9223372036854775807,1")]
    [InlineData("-5,1")]
    [InlineData("99999999999999999999,1")]
    [InlineData("638000000000000000,abc")]
    [InlineData("not a line")]
    [InlineData("")]
    public void A_damaged_line_is_skipped_without_an_exception(string line)
    {
        Assert.False(MetricStore.TryParseLine(line, out _));
    }
}
