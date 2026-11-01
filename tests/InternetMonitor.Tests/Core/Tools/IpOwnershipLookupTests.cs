using System.Net;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Tests.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools;

/// <summary>
/// Who owns a hop's address, without the internet: RIPEstat's two answers are played back, and the
/// lookup's caching per /24 is checked by counting what it asked.
/// </summary>
[Collection("Localization")]
public sealed class IpOwnershipLookupTests
{
    private const string GoogleOverview = """
        {"status":"ok","data":{"announced":true,"asns":[{"asn":15169,"holder":"GOOGLE - Google LLC"}],"resource":"8.8.8.0/24","block":{"resource":"8.0.0.0/8","desc":"Administered by ARIN","name":"IANA IPv4 Address Space Registry"}}}
        """;

    private const string GoogleCountry = """
        {"status":"ok","data":{"located_resources":[{"resource":"8.8.8.0/24","location":"US"}]}}
        """;

    private sealed class RipeStat
    {
        public Dictionary<string, string> Overview { get; } = [];

        public Dictionary<string, string> Country { get; } = [];

        public FakeHttpHandler Http { get; }

        public RipeStat()
        {
            Http = new FakeHttpHandler(request =>
            {
                string resource = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["resource"] ?? string.Empty;
                Dictionary<string, string> table = request.RequestUri.AbsolutePath.Contains("/prefix-overview/", StringComparison.Ordinal) ? Overview : Country;
                return table.TryGetValue(resource, out string? body)
                    ? FakeHttpHandler.Text(HttpStatusCode.OK, body, "application/json")
                    : FakeHttpHandler.Status(HttpStatusCode.NotFound);
            });
        }

        public IpOwnershipLookup Lookup() => new(Http);
    }

    [Fact]
    public async Task A_private_or_ipv6_address_is_not_looked_up_at_all()
    {
        var ripe = new RipeStat();
        using IpOwnershipLookup lookup = ripe.Lookup();

        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("10.0.0.1"), CancellationToken.None));
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("2001:db8::1"), CancellationToken.None));
        Assert.Empty(ripe.Http.Requests);
    }

    [Fact]
    public async Task Ripestat_gives_the_as_its_holder_the_prefix_and_the_country_and_a_neighbour_is_served_from_the_cache()
    {
        var ripe = new RipeStat();
        ripe.Overview["8.8.8.8"] = GoogleOverview;
        ripe.Country["8.8.8.8"] = GoogleCountry;
        using IpOwnershipLookup lookup = ripe.Lookup();

        IpOwner? owner = await lookup.LookupAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None);

        Assert.Equal(new IpOwner("15169", "GOOGLE - Google LLC", null, "US", "8.8.8.0/24"), owner);
        Assert.Equal(2, ripe.Http.Requests.Count);
        Assert.All(ripe.Http.Requests, u => Assert.Contains("sourceapp=internetmonitor", u.Query, StringComparison.Ordinal));
        Assert.All(ripe.Http.Requests, u => Assert.Equal("stat.ripe.net", u.Host));
        Assert.Equal("AS15169  GOOGLE - Google LLC   US", owner!.Describe());

        // The same /24 again: served from the cache, nothing asked.
        IpOwner? neighbour = await lookup.LookupAsync(IPAddress.Parse("8.8.8.200"), CancellationToken.None);
        Assert.Same(owner, neighbour);
        Assert.Equal(2, ripe.Http.Requests.Count);
    }

    [Fact]
    public async Task A_holder_with_a_trailing_country_loses_it_and_one_failing_answer_still_yields_the_other()
    {
        var ripe = new RipeStat();
        ripe.Overview["1.0.0.7"] = """{"status":"ok","data":{"asns":[{"asn":"13335","holder":"CLOUDFLARENET, US"}],"resource":"1.0.0.0/24"}}""";
        // No country answer at all for this one.
        using IpOwnershipLookup lookup = ripe.Lookup();

        IpOwner? owner = await lookup.LookupAsync(IPAddress.Parse("1.0.0.7"), CancellationToken.None);

        Assert.Equal(("13335", "CLOUDFLARENET", null, "1.0.0.0/24"), (owner!.AsNumber, owner.AsName, owner.CountryCode, owner.Prefix));
    }

    [Fact]
    public async Task Nothing_known_or_a_status_that_is_not_ok_is_not_cached_so_the_next_trace_asks_again()
    {
        var ripe = new RipeStat();
        ripe.Overview["9.9.9.9"] = """{"status":"error","data":{"asns":[{"asn":19281,"holder":"QUAD9-AS-1"}]}}""";
        using IpOwnershipLookup lookup = ripe.Lookup();

        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("9.9.9.9"), CancellationToken.None));
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("9.9.9.9"), CancellationToken.None));

        Assert.Equal(4, ripe.Http.Requests.Count);
    }

    [Fact]
    public async Task The_cache_survives_a_restart()
    {
        var first = new RipeStat();
        first.Overview["4.2.2.2"] = """{"status":"ok","data":{"asns":[{"asn":3356,"holder":"LEVEL3"}],"resource":"4.0.0.0/9"}}""";
        first.Country["4.2.2.2"] = """{"status":"ok","data":{"located_resources":[{"resource":"4.0.0.0/9","location":"us"}]}}""";
        using (IpOwnershipLookup lookup = first.Lookup())
        {
            Assert.NotNull(await lookup.LookupAsync(IPAddress.Parse("4.2.2.2"), CancellationToken.None));
        }

        var second = new RipeStat();
        using IpOwnershipLookup restarted = second.Lookup();
        IpOwner? owner = await restarted.LookupAsync(IPAddress.Parse("4.2.2.7"), CancellationToken.None);

        Assert.Equal(("LEVEL3", "US"), (owner!.AsName, owner.CountryCode));
        Assert.Empty(second.Http.Requests);
    }
}
