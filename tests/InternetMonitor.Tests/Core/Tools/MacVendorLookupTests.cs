using System.Net;
using InternetMonitor.Core.Tools.LanScan;
using InternetMonitor.Tests.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools;

/// <summary>Which vendor a MAC address belongs to, with both providers played back: normalisation, caching, the fallback and an unknown prefix.</summary>
[Collection("Localization")]
public sealed class MacVendorLookupTests
{
    /// <summary>Answers per OUI: what api.macvendors.com says (null = 404) and what macvendorlookup.com says (null = 204).</summary>
    private static FakeHttpHandler Providers(Dictionary<string, (string? MacVendors, string? MacVendorLookup)> table) => new(request =>
    {
        string path = request.RequestUri!.AbsolutePath;
        string host = request.RequestUri.Host;
        string oui = host == "api.macvendors.com" ? path.TrimStart('/') : path.Split('/')[3];
        (string? macVendors, string? macVendorLookup) = table.GetValueOrDefault(oui);

        if (host == "api.macvendors.com")
        {
            return macVendors is null ? FakeHttpHandler.Status(HttpStatusCode.NotFound) : FakeHttpHandler.Text(HttpStatusCode.OK, macVendors);
        }

        return macVendorLookup is null
            ? FakeHttpHandler.Status(HttpStatusCode.NoContent)
            : FakeHttpHandler.Text(HttpStatusCode.OK, $$"""[{"company":"{{macVendorLookup}}"}]""", "application/json");
    });

    [Fact]
    public async Task The_prefix_is_normalised_and_a_known_vendor_is_cached_whatever_the_spelling()
    {
        FakeHttpHandler http = Providers(new() { ["AABBC1"] = ("Acme Inc", "Acme Inc") });
        using var lookup = new MacVendorLookup(http);

        Assert.Equal("Acme Inc", await lookup.LookupAsync("aa:bb:c1:dd:ee:ff", CancellationToken.None));
        Uri asked = Assert.Single(http.Requests);
        Assert.Contains("AABBC1", asked.AbsolutePath, StringComparison.Ordinal);

        Assert.Equal("Acme Inc", await lookup.LookupAsync("AA-BB-C1-00-00-01", CancellationToken.None));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_prefix_the_first_provider_does_not_know_is_asked_of_the_other()
    {
        // The providers take turns going first, per new prefix: macvendorlookup.com for the first
        // one, api.macvendors.com for the second. Each prefix here is unknown to whichever goes first.
        FakeHttpHandler http = Providers(new()
        {
            ["0011A2"] = ("Fallback Ltd", null),
            ["0011A3"] = (null, "Other Way Round"),
        });
        using var lookup = new MacVendorLookup(http);

        Assert.Equal("Fallback Ltd", await lookup.LookupAsync("00:11:a2:00:00:01", CancellationToken.None));
        Assert.Equal("Other Way Round", await lookup.LookupAsync("00:11:a3:00:00:01", CancellationToken.None));

        Assert.Equal(2, http.Requests.Count(u => u.AbsolutePath.Contains("0011A2", StringComparison.Ordinal)));
        Assert.Equal(2, http.Requests.Count(u => u.AbsolutePath.Contains("0011A3", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_unknown_prefix_is_null_and_remembered_and_a_short_address_is_never_sent()
    {
        FakeHttpHandler http = Providers([]);
        using var lookup = new MacVendorLookup(http);

        Assert.Null(await lookup.LookupAsync("02:00:00:11:22:33", CancellationToken.None));
        Assert.Equal(2, http.Requests.Count);
        Assert.Null(await lookup.LookupAsync("02:00:00:44:55:66", CancellationToken.None));
        Assert.Equal(2, http.Requests.Count);

        Assert.Null(await lookup.LookupAsync("02:00", CancellationToken.None));
        Assert.Equal(2, http.Requests.Count);
    }
}
