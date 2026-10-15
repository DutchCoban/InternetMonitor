using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Tools.SpeedTest;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

public sealed class SpeedTestAwarenessTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 15, 19, 47, 0, TimeSpan.Zero);

    [Fact]
    public void A_test_counts_for_any_window_it_overlaps()
    {
        SpeedTestActivity.Clear();
        SpeedTestActivity.Begin(T);
        SpeedTestActivity.End(T.AddSeconds(20));

        Assert.True(SpeedTestActivity.During(T.AddSeconds(10), T.AddMinutes(5)));
        Assert.True(SpeedTestActivity.During(T.AddMinutes(-5), T.AddSeconds(1)));
        Assert.False(SpeedTestActivity.During(T.AddMinutes(1), T.AddMinutes(6)));
        Assert.False(SpeedTestActivity.During(T.AddMinutes(-6), T.AddMinutes(-1)));
        SpeedTestActivity.Clear();
    }

    [Fact]
    public void A_test_still_running_counts_up_to_now()
    {
        SpeedTestActivity.Clear();
        SpeedTestActivity.Begin(T);

        Assert.True(SpeedTestActivity.During(T.AddMinutes(10), T.AddMinutes(15)));
        SpeedTestActivity.Clear();
    }

    [Theory]
    [InlineData("tcpRetransmissions", true, "networkHealth.tcpRetransmissions.speedTest.cause")]
    [InlineData("packetLoss", true, "networkHealth.packetLoss.speedTest.cause")]
    [InlineData("tcpRetransmissions", false, "networkHealth.tcpRetransmissions.cause")]
    [InlineData("ttlExceeded", true, "networkHealth.ttlExceeded.cause")]
    public void Retransmissions_and_loss_during_a_speed_test_get_their_own_explanation(string condition, bool speedTest, string expectedCause)
    {
        (string cause, string todo) = NetworkHealthMonitor.ExplanationKeys(condition, speedTest);

        Assert.Equal(expectedCause, cause);
        Assert.Equal(expectedCause.Replace(".cause", ".todo"), todo);
    }
}
