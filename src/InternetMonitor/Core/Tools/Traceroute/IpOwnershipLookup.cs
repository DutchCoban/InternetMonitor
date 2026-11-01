using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using InternetMonitor.Configuration;

namespace InternetMonitor.Core.Tools.Traceroute;

/// <summary>
/// Finds out who an address belongs to: which autonomous system announces it, what that network
/// is called, and which country it is registered in.
///
/// One source: RIPEstat, the RIPE NCC's public data service in Amsterdam, which carries routing
/// and registry data for all five regional registries, not only Europe's. Two keyless HTTPS
/// questions per address, asked together - <c>prefix-overview</c> for the announcing AS, its
/// holder and the prefix, and <c>rir-stats-country</c> for the registration country - each
/// tagged with the <c>sourceapp</c> name RIPEstat asks every application to send. Up to 2.7.0 the
/// same answer came from Team Cymru's DNS service and the rdap.org redirector, both in the
/// United States; RIPEstat gives the same fields from a European operator with a thirty-year record.
///
/// Private, loopback and carrier-grade-NAT addresses are answered locally and never leave the
/// machine. Everything else is cached on disk for a month, keyed by /24, so a repeated traceroute
/// along the same route is instant and sends nothing. The cache file and its shape are the same
/// as before, so an existing cache stays valid.
///
/// This is the only part of the app that tells a third party anything about your route, which is
/// why it is a setting the user can turn off (AppSettings.TracerouteOwnerLookup).
/// </summary>
public sealed class IpOwnershipLookup : IDisposable
{
    public const string ApiBase = "https://stat.ripe.net/data/";
    private const string SourceApp = "internetmonitor";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(4);

    /// <summary>A RIPEstat answer is a few KB; the client refuses anything larger rather than buffering it.</summary>
    private const long MaxResponseBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly HttpClient _http;

    // ponytail: four lookups in flight at once is a polite rate for a trace of thirty hops; the
    // pacing gate from OuiRegistry's predecessor can come back if RIPEstat ever answers 429.
    private readonly SemaphoreSlim _gate = new(4, 4);
    private bool _dirty;

    public IpOwnershipLookup()
        : this(new HttpClientHandler())
    {
    }

    /// <summary>The same lookup over a substituted HTTP handler, so the tests can answer without a network.</summary>
    internal IpOwnershipLookup(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
            DefaultRequestHeaders = { { "Accept", "application/json" }, { "User-Agent", "InternetMonitor/2.8" } },
        };

        LoadCache();
    }

    private sealed record CacheEntry(IpOwner Owner, DateTimeOffset StoredUtc);

    public async Task<IpOwner?> LookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (HopAnnotator.IsUnroutable(address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        // Cached per prefix rather than per address: every hop in one provider's block has the
        // same owner, so one lookup serves them all.
        string key = PrefixKey(address);
        if (_cache.TryGetValue(key, out CacheEntry? cached) && DateTimeOffset.UtcNow - cached.StoredUtc < CacheLifetime)
        {
            return cached.Owner;
        }

        IpOwner? owner;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Task<(string? AsNumber, string? AsName, string? Prefix)?> overview = QueryPrefixOverviewAsync(address, cancellationToken);
            Task<string?> country = QueryCountryAsync(address, cancellationToken);
            await Task.WhenAll(overview, country).ConfigureAwait(false);

            (string? asNumber, string? asName, string? prefix) = overview.Result ?? (null, null, null);
            owner = asNumber is null && asName is null && country.Result is null
                ? null
                : new IpOwner(asNumber, asName, null, country.Result, prefix);
        }
        finally
        {
            _gate.Release();
        }

        // An empty answer is not cached, so a lookup that failed for a network reason is retried
        // on the next trace instead of being remembered for a month.
        if (owner is { HasAnything: true })
        {
            _cache[key] = new CacheEntry(owner, DateTimeOffset.UtcNow);
            _dirty = true;
        }

        return owner;
    }

    /// <summary>
    /// prefix-overview: <c>data.asns</c> lists the announcing systems as {asn, holder}, and
    /// <c>data.resource</c> is the announced prefix the address was aligned to. A holder reads
    /// "GOOGLE - Google LLC" or "SURFNET-NL SURF B.V."; a trailing ", US"-style country is dropped,
    /// since the country comes from the registry question.
    /// </summary>
    private async Task<(string? AsNumber, string? AsName, string? Prefix)?> QueryPrefixOverviewAsync(IPAddress address, CancellationToken cancellationToken)
    {
        JsonNode? data = await GetDataAsync("prefix-overview", address, cancellationToken).ConfigureAwait(false);
        if (data is null)
        {
            return null;
        }

        JsonNode? first = data["asns"] is JsonArray { Count: > 0 } asns ? asns[0] : null;
        string? asNumber = first?["asn"]?.ToString();
        string? holder = first?["holder"]?.GetValue<string>();
        string? prefix = data["resource"]?.GetValue<string>();

        if (holder is not null)
        {
            int comma = holder.LastIndexOf(", ", StringComparison.Ordinal);
            if (comma > 0 && holder.Length - comma <= 5)
            {
                holder = holder[..comma];
            }
        }

        return asNumber is null && holder is null && prefix is null
            ? null
            : (string.IsNullOrEmpty(asNumber) ? null : asNumber, string.IsNullOrWhiteSpace(holder) ? null : holder.Trim(), prefix is { Length: > 0 } && prefix.Contains('/') ? prefix : null);
    }

    /// <summary>rir-stats-country: <c>data.located_resources[0].location</c> is the two-letter country the registry has the block in.</summary>
    private async Task<string?> QueryCountryAsync(IPAddress address, CancellationToken cancellationToken)
    {
        JsonNode? data = await GetDataAsync("rir-stats-country", address, cancellationToken).ConfigureAwait(false);
        string? location = data?["located_resources"] is JsonArray { Count: > 0 } located ? located[0]?["location"]?.GetValue<string>() : null;
        return location is { Length: 2 } ? location.ToUpperInvariant() : null;
    }

    /// <summary>One RIPEstat data call; its <c>data</c> object when the call succeeded, null on any failure, timeout or a non-ok status.</summary>
    private async Task<JsonNode?> GetDataAsync(string call, IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HttpTimeout);

            using HttpResponseMessage response = await _http
                .GetAsync($"{ApiBase}{call}/data.json?resource={address}&sourceapp={SourceApp}", timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            JsonNode? root = JsonNode.Parse(json);
            return root?["status"]?.GetValue<string>() == "ok" ? root["data"] : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string PrefixKey(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return $"{b[0]}.{b[1]}.{b[2]}.0/24";
    }

    private string CachePath => Path.Combine(AppPaths.CacheDir, "asn.json");

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            Dictionary<string, CacheEntry>? stored =
                JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath));

            if (stored is null)
            {
                return;
            }

            foreach ((string key, CacheEntry entry) in stored)
            {
                if (DateTimeOffset.UtcNow - entry.StoredUtc < CacheLifetime)
                {
                    _cache[key] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt cache just means the next trace does its lookups again.
        }
    }

    /// <summary>
    /// Writes the cache when something was added since the last save. Best effort: a cache that could
    /// not be written is simply looked up again next time.
    /// </summary>
    public void SaveCache()
    {
        if (!_dirty)
        {
            return;
        }

        try
        {
            AppPaths.TryEnsureDirectory(AppPaths.CacheDir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Left dirty, so the next save tries again.
        }
    }

    public void Dispose()
    {
        SaveCache();
        _http.Dispose();
        _gate.Dispose();
    }
}
