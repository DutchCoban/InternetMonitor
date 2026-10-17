using InternetMonitor.Core.Monitors;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

/// <summary>
/// The push URL carries a token, so only an https address may ever start the loop - whatever a
/// hand-edited or imported settings file says.
/// </summary>
public sealed class KumaPusherTests
{
    [Theory]
    [InlineData("https://kuma.example/api/push/abc123?status=up", true)]
    [InlineData("  https://kuma.example/api/push/abc123  ", true)]
    [InlineData("http://kuma.example/api/push/abc123", false)]
    [InlineData("file:///C:/push", false)]
    [InlineData("ftp://kuma.example/push", false)]
    [InlineData("kuma.example/api/push/abc123", false)]
    [InlineData("", false)]
    public void Only_an_absolute_https_url_is_usable(string url, bool usable)
    {
        Assert.Equal(usable, KumaPusher.IsUsableUrl(url));
    }

    [Theory]
    [InlineData("http://kuma.example/api/push/abc123")]
    [InlineData("file:///C:/push")]
    [InlineData("not a url")]
    public async Task An_unusable_url_does_not_start_the_loop(string url)
    {
        await using var pusher = new KumaPusher();

        pusher.Reconfigure(url, 60);

        Assert.False(pusher.IsActive);
    }

    [Fact]
    public async Task Reconfiguring_from_a_usable_url_to_an_unusable_one_stops_the_loop()
    {
        await using var pusher = new KumaPusher();

        // 127.0.0.1 on a port nothing listens on: the push fails fast, which is fine - this is
        // about whether the loop exists, not whether a push lands.
        pusher.Reconfigure("https://127.0.0.1:9/api/push/test", 60);
        Assert.True(pusher.IsActive);

        pusher.Reconfigure("http://127.0.0.1:9/api/push/test", 60);
        Assert.False(pusher.IsActive);
    }
}
