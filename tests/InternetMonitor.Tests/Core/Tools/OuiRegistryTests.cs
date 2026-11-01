using System.Net;
using System.Net.Http.Headers;
using InternetMonitor.Core.Tools.LanScan;
using InternetMonitor.Tests.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools;

/// <summary>
/// Which manufacturer a MAC address belongs to, from the IEEE list: the built-in snapshot answers
/// at once, the IEEE's CSV with its quoted names parses, a daily refresh is a conditional request
/// that takes a changed list and leaves an unchanged one, and a downloaded list outranks the snapshot.
/// </summary>
public sealed class OuiRegistryTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"im-oui-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    /// <summary>Just over the plausibility floor, so a played-back list counts as the registry.</summary>
    private static string BigCsv(string firstRows)
    {
        var sb = new System.Text.StringBuilder("Registry,Assignment,Organization Name,Organization Address\n");
        sb.Append(firstRows);
        for (int i = 0; i < 20_100; i++)
        {
            sb.Append($"MA-L,F{i:X5},Maker {i},Somewhere\n");
        }

        return sb.ToString();
    }

    [Fact]
    public async Task The_built_in_snapshot_knows_a_common_prefix_whatever_the_spelling_and_an_unknown_one_is_null()
    {
        await using var registry = new OuiRegistry(new FakeHttpHandler(_ => FakeHttpHandler.Status(HttpStatusCode.NotFound)), _cacheDir);

        string? intel = registry.Lookup("08:b4:d2:d2:27:79");
        Assert.NotNull(intel);
        Assert.Contains("Intel", intel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(intel, registry.Lookup("08-B4-D2-00-00-01"));
        Assert.Equal(intel, registry.Lookup("08B4D2000001"));
        Assert.Null(registry.Lookup("not a mac"));
        Assert.Null(registry.CachedAtUtc);
    }

    [Fact]
    public void The_ieee_csv_parses_with_quoted_names_and_doubled_quotes()
    {
        Dictionary<string, string> parsed = OuiRegistry.ParseCsv(
            "Registry,Assignment,Organization Name,Organization Address\r\n" +
            "MA-L,00000C,\"Cisco Systems, Inc\",170 West Tasman Drive San Jose CA US\r\n" +
            "MA-L,AABBCC,\"Acme \"\"Widgets\"\" Ltd\",Nowhere\r\n" +
            "MA-L,bad,No,Where\r\n");

        Assert.Equal("Cisco Systems, Inc", parsed["00000C"]);
        Assert.Equal("Acme \"Widgets\" Ltd", parsed["AABBCC"]);
        Assert.Equal(2, parsed.Count);
    }

    [Fact]
    public async Task A_refresh_asks_conditionally_takes_a_changed_list_and_leaves_an_unchanged_one()
    {
        var etagsSeen = new List<string?>();
        var http = new FakeHttpHandler(request =>
        {
            etagsSeen.Add(request.Headers.IfNoneMatch.FirstOrDefault()?.Tag);
            if (request.Headers.IfNoneMatch.Any(t => t.Tag == "\"v1\""))
            {
                return FakeHttpHandler.Status(HttpStatusCode.NotModified);
            }

            HttpResponseMessage response = FakeHttpHandler.Text(HttpStatusCode.OK, BigCsv("MA-L,08B4D2,Fresh Name From IEEE,Somewhere\n"), "text/csv");
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return response;
        });

        await using (var registry = new OuiRegistry(http, _cacheDir))
        {
            Assert.True(await registry.RefreshOnceAsync(CancellationToken.None));
            Assert.Equal("Fresh Name From IEEE", registry.Lookup("08:B4:D2:00:00:01"));
            Assert.NotNull(registry.CachedAtUtc);
            Assert.True(File.Exists(Path.Combine(_cacheDir, "oui.tsv")));

            Assert.False(await registry.RefreshOnceAsync(CancellationToken.None));
        }

        Assert.Equal([null, "\"v1\""], etagsSeen);

        // A restart reads the cache back, and the newer list still outranks the snapshot.
        await using var restarted = new OuiRegistry(http, _cacheDir);
        Assert.Equal("Fresh Name From IEEE", restarted.Lookup("08:B4:D2:00:00:01"));
        Assert.NotNull(restarted.CachedAtUtc);
    }

    [Fact]
    public async Task Something_that_is_not_the_registry_is_refused()
    {
        var http = new FakeHttpHandler(_ => FakeHttpHandler.Text(HttpStatusCode.OK, "Registry,Assignment,Organization Name,Organization Address\nMA-L,AABBCC,Only One,Here\n", "text/csv"));
        await using var registry = new OuiRegistry(http, _cacheDir);

        Assert.False(await registry.RefreshOnceAsync(CancellationToken.None));
        Assert.Null(registry.Lookup("AA:BB:CC:00:00:01"));
        Assert.False(File.Exists(Path.Combine(_cacheDir, "oui.tsv")));
    }
}
