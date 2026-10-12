using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using InternetMonitor.Configuration;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Tools.Traceroute;

/// <summary>
/// Finds out who an address belongs to: which autonomous system announces it, what that network
/// is called, and which country it is registered in.
///
/// Two free, keyless sources, in order:
///
///  1. Team Cymru's IP-to-ASN service, over DNS. A TXT query for
///     &lt;reversed-ip&gt;.origin.asn.cymru.com returns the AS number, the announced prefix, the
///     country and the registry; a second query for AS&lt;n&gt;.asn.cymru.com returns the AS name.
///     It exists for exactly this purpose, needs no account, and is a UDP round trip rather than
///     an HTTPS request - which matters when this runs during a network problem.
///  2. RDAP at the regional registries, over HTTPS, for the network name when Cymru gives no AS
///     name. rdap.org redirects to whichever registry is authoritative.
///
/// Private, loopback and carrier-grade-NAT addresses are answered locally and never leave the
/// machine. Everything else is cached on disk for a month, keyed by /24, so a repeated traceroute
/// along the same route is instant and sends nothing.
///
/// This is the only part of the app that tells a third party anything about your route, which is
/// why it is a setting the user can turn off (AppSettings.TracerouteOwnerLookup).
/// </summary>
public sealed class IpOwnershipLookup : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RdapTimeout = TimeSpan.FromSeconds(4);

    private static readonly IPAddress[] Resolvers =
    [
        IPAddress.Parse("9.9.9.9"),
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("8.8.8.8"),
    ];

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly ConcurrentDictionary<string, string> _asNames = new();
    private readonly HttpClient _http;
    private readonly bool _useRdap;
    private bool _dirty;

    public IpOwnershipLookup(bool useRdap = true)
    {
        _useRdap = useRdap;
        _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestHeaders = { { "Accept", "application/rdap+json" }, { "User-Agent", "InternetMonitor/2.0" } },
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

        string key = PrefixKey(address);
        if (_cache.TryGetValue(key, out CacheEntry? cached) && DateTimeOffset.UtcNow - cached.StoredUtc < CacheLifetime)
        {
            return cached.Owner;
        }

        IpOwner? owner = await QueryCymruAsync(address, cancellationToken).ConfigureAwait(false);

        if (_useRdap && (owner is null || owner.NetworkName is null))
        {
            IpOwner? rdap = await QueryRdapAsync(address, cancellationToken).ConfigureAwait(false);
            if (rdap is not null)
            {
                owner = owner is null
                    ? rdap
                    : owner with
                    {
                        NetworkName = owner.NetworkName ?? rdap.NetworkName,
                        CountryCode = owner.CountryCode ?? rdap.CountryCode,
                    };
            }
        }

        if (owner is { HasAnything: true })
        {
            _cache[key] = new CacheEntry(owner, DateTimeOffset.UtcNow);
            _dirty = true;
        }

        return owner;
    }

    /// <summary>
    /// origin.asn.cymru.com answers with "15169 | 8.8.8.0/24 | US | arin | 1992-12-01".
    /// asn.cymru.com answers with "15169 | US | arin | 1992-12-01 | GOOGLE, US".
    /// </summary>
    private async Task<IpOwner?> QueryCymruAsync(IPAddress address, CancellationToken cancellationToken)
    {
        byte[] bytes = address.GetAddressBytes();
        string query = $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.origin.asn.cymru.com";

        string? answer = await QueryTxtAsync(query, cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }

        string[] fields = answer.Split('|', StringSplitOptions.TrimEntries);
        if (fields.Length < 3)
        {
            return null;
        }

        // The first field can list several AS numbers when a prefix is announced by more than one.
        string asNumber = fields[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        string prefix = fields[1];
        string country = fields[2];

        string? asName = asNumber.Length > 0
            ? await LookupAsNameAsync(asNumber, cancellationToken).ConfigureAwait(false)
            : null;

        return new IpOwner(
            asNumber.Length > 0 ? asNumber : null,
            asName,
            null,
            country.Length > 0 ? country : null,
            prefix.Length > 0 ? prefix : null);
    }

    private async Task<string?> LookupAsNameAsync(string asNumber, CancellationToken cancellationToken)
    {
        if (_asNames.TryGetValue(asNumber, out string? cached))
        {
            return cached;
        }

        string? answer = await QueryTxtAsync($"AS{asNumber}.asn.cymru.com", cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return null;
        }

        string[] fields = answer.Split('|', StringSplitOptions.TrimEntries);
        if (fields.Length < 5)
        {
            return null;
        }

        // The name field is "GOOGLE, US" - the trailing country duplicates a field we already
        // have, so it is trimmed off.
        string name = fields[4];
        int comma = name.LastIndexOf(", ", StringComparison.Ordinal);
        if (comma > 0 && name.Length - comma <= 5)
        {
            name = name[..comma];
        }

        _asNames[asNumber] = name;
        return name;
    }

    /// <summary>
    /// Asks each resolver in turn until one answers. Cymru's zone is not served by every resolver
    /// equally reliably, and this runs while the network may be degraded, so a single attempt
    /// would silently produce a route with no ownership on it.
    /// </summary>
    private static async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        foreach (IPAddress resolver in Resolvers)
        {
            DnsQueryResult result = await DnsUdpClient
                .QueryAsync(resolver, name, DnsRecordType.Txt, DnsTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success && result.TextRecords.Count > 0)
            {
                return result.TextRecords[0];
            }
        }

        return null;
    }

    private async Task<IpOwner?> QueryRdapAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RdapTimeout);

            using HttpResponseMessage response = await _http
                .GetAsync($"https://rdap.org/ip/{address}", timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            JsonNode? root = JsonNode.Parse(json);

            string? name = root?["name"]?.GetValue<string>();
            string? country = root?["country"]?.GetValue<string>();

            return name is null && country is null ? null : new IpOwner(null, null, name, country, null);
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
        }
    }

    public void Dispose()
    {
        SaveCache();
        _http.Dispose();
    }
}
