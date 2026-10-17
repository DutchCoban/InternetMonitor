using System.Text.Json;
using InternetMonitor.Core.Updates;
using Xunit;

namespace InternetMonitor.Tests.Core.Updates;

/// <summary>
/// The release feed is data from the network. Only GitHub's own HTTPS URLs and exactly the
/// expected asset names may come out of it, whatever the JSON says.
/// </summary>
public sealed class UpdateCheckerTests
{
    private static readonly Version Current = new(2, 2, 2);

    private static UpdateCheckResult? Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return UpdateChecker.ParseRelease(doc.RootElement, Current);
    }

    private const string Release = """
        {
          "tag_name": "v2.3.0",
          "html_url": "https://github.com/DutchCoban/InternetMonitor/releases/tag/v2.3.0",
          "assets": [
            { "name": "InternetMonitor.exe", "browser_download_url": "https://github.com/DutchCoban/InternetMonitor/releases/download/v2.3.0/InternetMonitor.exe" },
            { "name": "InternetMonitor.exe.sig", "browser_download_url": "https://github.com/DutchCoban/InternetMonitor/releases/download/v2.3.0/InternetMonitor.exe.sig" }
          ]
        }
        """;

    [Fact]
    public void A_normal_release_yields_every_url()
    {
        UpdateCheckResult? result = Parse(Release);

        Assert.NotNull(result);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new Version(2, 3, 0), result.Latest);
        Assert.Equal("2.3.0", result.LatestVersion);
        Assert.EndsWith("/InternetMonitor.exe", result.AssetDownloadUrl);
        Assert.EndsWith("/InternetMonitor.exe.sig", result.SignatureDownloadUrl);
        Assert.StartsWith("https://github.com/", result.ReleaseUrl);
    }

    [Fact]
    public void The_running_version_or_an_older_one_is_not_an_update()
    {
        Assert.False(Parse(Release.Replace("v2.3.0", "v2.2.2"))!.IsUpdateAvailable);
        Assert.False(Parse(Release.Replace("v2.3.0", "v2.1.9"))!.IsUpdateAvailable);
        Assert.Null(Parse("""{ "tag_name": "latest" }"""));
    }

    [Fact]
    public void A_release_page_off_github_or_over_plain_http_is_dropped()
    {
        Assert.Null(Parse(Release.Replace("https://github.com/DutchCoban/InternetMonitor/releases/tag", "https://evil.example/releases/tag"))!.ReleaseUrl);
        Assert.Null(Parse(Release.Replace("https://github.com/DutchCoban/InternetMonitor/releases/tag", "http://github.com/DutchCoban/InternetMonitor/releases/tag"))!.ReleaseUrl);
        Assert.Null(Parse(Release.Replace("https://github.com/DutchCoban/InternetMonitor/releases/tag", "file:///C:/Windows/System32/calc.exe?"))!.ReleaseUrl);
    }

    [Fact]
    public void An_asset_off_github_is_not_a_download()
    {
        UpdateCheckResult? result = Parse(Release.Replace("https://github.com/DutchCoban/InternetMonitor/releases/download", "https://github.com.evil.example/download"));

        Assert.NotNull(result);
        Assert.Null(result.AssetDownloadUrl);
        Assert.Null(result.SignatureDownloadUrl);
    }

    [Fact]
    public void Only_the_exactly_named_executable_counts()
    {
        UpdateCheckResult? result = Parse(Release.Replace("\"name\": \"InternetMonitor.exe\"", "\"name\": \"Setup.exe\""));

        Assert.NotNull(result);
        Assert.True(result.IsUpdateAvailable);
        Assert.Null(result.AssetDownloadUrl);
        Assert.NotNull(result.SignatureDownloadUrl);
    }

    [Fact]
    public void A_release_without_a_signature_asset_is_reported_but_has_nothing_to_install()
    {
        UpdateCheckResult? result = Parse(Release.Replace("\"name\": \"InternetMonitor.exe.sig\"", "\"name\": \"checksums.txt\""));

        Assert.NotNull(result);
        Assert.NotNull(result.AssetDownloadUrl);
        Assert.Null(result.SignatureDownloadUrl);
    }

    [Theory]
    [InlineData("https://github.com/x/y/releases/download/v1/InternetMonitor.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/abc", true)]
    [InlineData("https://release-assets.github.com/abc", true)]
    [InlineData("http://github.com/x/y/releases/download/v1/InternetMonitor.exe", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    [InlineData("ftp://github.com/x", false)]
    [InlineData("not a url", false)]
    public void Asset_urls_are_trusted_only_on_github_hosts_over_https(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateChecker.IsTrustedAssetUrl(url));
    }
}
