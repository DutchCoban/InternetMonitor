using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

public sealed class WlanInfoTests
{
    [Theory]
    [InlineData(-40, 100)]
    [InlineData(-50, 100)]
    [InlineData(-56, 88)]
    [InlineData(-80, 40)]
    [InlineData(-100, 0)]
    [InlineData(-110, 0)]
    public void Signal_percentage_is_derived_from_rssi_the_way_windows_scales_it(int dbm, int percent)
    {
        Assert.Equal(percent, WlanInfo.SignalFromRssi(dbm));
    }

    [Theory]
    [InlineData(1, 2412, "2.4 GHz")]
    [InlineData(13, 2472, "2.4 GHz")]
    [InlineData(14, 2484, "2.4 GHz")]
    [InlineData(48, 5240, "5 GHz")]
    [InlineData(149, 5745, "5 GHz")]
    public void The_band_follows_from_the_channel_when_the_frequency_is_withheld(int channel, int mhz, string band)
    {
        Assert.Equal(mhz, WlanInfo.FrequencyOfChannel(channel));
        Assert.Equal(band, (WifiInfo.Denied with { FrequencyMHz = mhz }).Band);
    }

    [Fact]
    public void A_channel_in_neither_band_gives_no_frequency()
    {
        Assert.Null(WlanInfo.FrequencyOfChannel(0));
        Assert.Null(WlanInfo.FrequencyOfChannel(233));
    }
}
