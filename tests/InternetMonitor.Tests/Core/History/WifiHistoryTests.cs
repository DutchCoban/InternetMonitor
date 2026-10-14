using InternetMonitor.Core.History;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

public sealed class WifiHistoryTests
{
    // A real v1 line's metadata, as found in an upgraded user's wifi-signal.csv.
    private const string V1 =
        "%7B%22Ssid%22%3A%22HeBo%22%2C%22Bssid%22%3A%2260%3A22%3A32%3AEF%3AF0%3AA3%22%2C%22LinkSpeedMbps%22%3A287%2C%22PhyType%22%3A%22HE%20%28802.11ax%29%22%7D";

    [Fact]
    public void V1_json_and_v2_plain_metadata_name_the_same_access_point()
    {
        Assert.Equal("60:22:32:ef:f0:a3", WifiHistory.AccessPointOf(V1));
        Assert.Equal("60:22:32:ef:f0:a3", WifiHistory.AccessPointOf("60:22:32:ef:f0:a3"));
    }

    [Fact]
    public void A_v1_link_speed_change_is_not_a_different_access_point()
    {
        string otherSpeed = V1.Replace("287", "144");
        Assert.Equal(WifiHistory.AccessPointOf(V1), WifiHistory.AccessPointOf(otherSpeed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%7Bnot json")]
    public void Missing_or_unreadable_metadata_has_no_access_point(string? metadata) =>
        Assert.Null(WifiHistory.AccessPointOf(metadata));
}
