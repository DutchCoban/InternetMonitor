using System.Net;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Tests.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools;

/// <summary>
/// Who owns a hop's address, without the internet: the TXT answers and the RDAP page are played
/// back, and the lookup's caching per /24 is checked by counting what it asked.
/// </summary>
[Collection("Localization")]
public sealed class IpOwnershipLookupTests
{
    private sealed class Answers
    {
        public Dictionary<string, string> Txt { get; } = [];

        public List<string> Asked { get; } = [];

        public FakeHttpHandler Http { get; } = new(request => request.RequestUri!.AbsolutePath.EndsWith("/ip/8.8.8.8", StringComparison.Ordinal)
            ? FakeHttpHandler.Text(HttpStatusCode.OK, """{"name":"GOGL","country":"US"}""", "application/rdap+json")
            : FakeHttpHandler.Status(HttpStatusCode.NotFound));

        public IpOwnershipLookup Lookup(bool useRdap = true) => new(useRdap, Http, (name, _) =>
        {
            Asked.Add(name);
            return Task.FromResult(Txt.GetValueOrDefault(name));
        });
    }

    [Fact]
    public async Task A_private_or_ipv6_address_is_not_looked_up_at_all()
    {
        var answers = new Answers();
        using IpOwnershipLookup lookup = answers.Lookup();

        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("10.0.0.1"), CancellationToken.None));
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("2001:db8::1"), CancellationToken.None));
        Assert.Empty(answers.Asked);
        Assert.Empty(answers.Http.Requests);
    }

    [Fact]
    public async Task Cymru_gives_the_as_and_country_and_rdap_fills_in_the_network_name()
    {
        var answers = new Answers();
        answers.Txt["8.8.8.8.origin.asn.cymru.com"] = "15169 | 8.8.8.0/24 | US | arin | 2023-12-28";
        answers.Txt["AS15169.asn.cymru.com"] = "15169 | US | arin | 2000-03-30 | GOOGLE, US";
        using IpOwnershipLookup lookup = answers.Lookup();

        IpOwner? owner = await lookup.LookupAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None);

        Assert.NotNull(owner);
        Assert.Equal(new IpOwner("15169", "GOOGLE", "GOGL", "US", "8.8.8.0/24"), owner);
        Assert.Equal(["8.8.8.8.origin.asn.cymru.com", "AS15169.asn.cymru.com"], answers.Asked);
        Assert.Equal("https://rdap.org/ip/8.8.8.8", Assert.Single(answers.Http.Requests).ToString());

        // The same /24 again: served from the cache, nothing asked.
        IpOwner? neighbour = await lookup.LookupAsync(IPAddress.Parse("8.8.8.200"), CancellationToken.None);
        Assert.Same(owner, neighbour);
        Assert.Equal(2, answers.Asked.Count);
        Assert.Single(answers.Http.Requests);
    }

    [Fact]
    public async Task Without_rdap_only_the_dns_side_is_asked_and_a_multi_as_answer_takes_the_first()
    {
        var answers = new Answers();
        answers.Txt["7.0.0.1.origin.asn.cymru.com"] = "13335 395747 | 1.0.0.0/24 | AU | apnic | 2011-08-11";
        answers.Txt["AS13335.asn.cymru.com"] = "13335 | US | arin | 2010-07-14 | CLOUDFLARENET, US";
        using IpOwnershipLookup lookup = answers.Lookup(useRdap: false);

        IpOwner? owner = await lookup.LookupAsync(IPAddress.Parse("1.0.0.7"), CancellationToken.None);

        Assert.Equal(("13335", "CLOUDFLARENET", "AU"), (owner!.AsNumber, owner.AsName, owner.CountryCode));
        Assert.Null(owner.NetworkName);
        Assert.Empty(answers.Http.Requests);
    }

    [Fact]
    public async Task Nothing_known_is_not_cached_so_the_next_trace_asks_again()
    {
        var answers = new Answers();
        using IpOwnershipLookup lookup = answers.Lookup();

        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("9.9.9.9"), CancellationToken.None));
        Assert.Null(await lookup.LookupAsync(IPAddress.Parse("9.9.9.9"), CancellationToken.None));

        Assert.Equal(2, answers.Asked.Count);
        Assert.Equal(2, answers.Http.Requests.Count);
    }

    [Fact]
    public async Task The_cache_survives_a_restart()
    {
        var first = new Answers();
        // Cymru is asked with the octets reversed, the way reverse DNS does it.
        first.Txt["2.2.2.4.origin.asn.cymru.com"] = "3356 | 4.0.0.0/9 | US | arin | 1992-12-01";
        first.Txt["AS3356.asn.cymru.com"] = "3356 | US | arin | 2000-03-10 | LEVEL3, US";
        using (IpOwnershipLookup lookup = first.Lookup(useRdap: false))
        {
            Assert.NotNull(await lookup.LookupAsync(IPAddress.Parse("4.2.2.2"), CancellationToken.None));
        }

        var second = new Answers();
        using IpOwnershipLookup restarted = second.Lookup(useRdap: false);
        IpOwner? owner = await restarted.LookupAsync(IPAddress.Parse("4.2.2.7"), CancellationToken.None);

        Assert.Equal("LEVEL3", owner!.AsName);
        Assert.Empty(second.Asked);
    }
}
