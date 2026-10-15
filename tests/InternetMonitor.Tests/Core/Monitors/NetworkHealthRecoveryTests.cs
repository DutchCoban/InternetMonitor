using InternetMonitor.Configuration;
using InternetMonitor.Core.Monitors;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

public sealed class NetworkHealthRecoveryTests
{
    private static readonly DateTimeOffset Opened = new(2026, 10, 15, 19, 47, 28, TimeSpan.Zero);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(30);

    // 380 per 5 minutes is 38 per 30-second sample.
    private static readonly NetworkHealthThreshold Retransmissions = new(true, 380, 5);

    [Fact]
    public void Two_calm_samples_after_the_burst_mean_back_to_normal()
    {
        (DateTimeOffset, int)[] samples = [(Opened.AddSeconds(-30), 900), (Opened, 140), (Opened.AddSeconds(30), 6), (Opened.AddSeconds(60), 4)];

        Assert.True(NetworkHealthMonitor.BackToNormal(samples, isRate: false, Retransmissions, Opened, Poll));
    }

    [Fact]
    public void One_calm_sample_is_not_enough()
    {
        (DateTimeOffset, int)[] samples = [(Opened, 900), (Opened.AddSeconds(30), 6)];

        Assert.False(NetworkHealthMonitor.BackToNormal(samples, isRate: false, Retransmissions, Opened, Poll));
    }

    [Fact]
    public void A_sample_still_above_the_normal_pace_keeps_it_open()
    {
        (DateTimeOffset, int)[] samples = [(Opened.AddSeconds(30), 6), (Opened.AddSeconds(60), 60)];

        Assert.False(NetworkHealthMonitor.BackToNormal(samples, isRate: false, Retransmissions, Opened, Poll));
    }

    [Fact]
    public void Packet_loss_is_compared_as_a_rate()
    {
        var loss = new NetworkHealthThreshold(true, 20, 5);
        (DateTimeOffset, int)[] calm = [(Opened.AddSeconds(30), 10), (Opened.AddSeconds(60), 0)];
        (DateTimeOffset, int)[] lossy = [(Opened.AddSeconds(30), 10), (Opened.AddSeconds(60), 34)];

        Assert.True(NetworkHealthMonitor.BackToNormal(calm, isRate: true, loss, Opened, Poll));
        Assert.False(NetworkHealthMonitor.BackToNormal(lossy, isRate: true, loss, Opened, Poll));
    }
}
