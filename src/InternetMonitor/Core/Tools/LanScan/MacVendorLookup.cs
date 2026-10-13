using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InternetMonitor.Configuration;

namespace InternetMonitor.Core.Tools.LanScan;

/// <summary>
/// Resolves a MAC address's manufacturer via two free, keyless lookup services, alternating which
/// one is tried first per OUI and falling back to the other when the first finds nothing.
///
/// Same shape and the same reasoning as
/// <see cref="Tools.Traceroute.IpOwnershipLookup"/> - the only other place in this app that tells a
/// third party anything about the user's own network, which is why that class is settings-gated,
/// constructed per-action, and disk-cached, and why this one is too
/// (<see cref="Configuration.AppSettings.LanScanVendorLookup"/>).
///
/// Cached by OUI (the first three bytes - a MAC's last three bytes are device-specific and never
/// sent anywhere), not by full address: a vendor assignment never changes, so unlike the ASN cache
/// this one has no expiry, and a typical LAN has a handful of distinct manufacturers rather than
/// one per device, so the cache alone eliminates most of the requests a scan would otherwise make.
/// </summary>
public sealed class MacVendorLookup : IDisposable
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(4);

    // api.macvendors.com documents a hard 1/second cap on its free tier; macvendorlookup.com
    // documents no hard number but asks for restraint - both margins are deliberately a bit
    // under what each provider actually allows.
    private static readonly TimeSpan MacVendorsMinInterval = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan MacVendorLookupMinInterval = TimeSpan.FromMilliseconds(600);

    private readonly ConcurrentDictionary<string, string?> _cache = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();
    private readonly HttpClient _http;
    private readonly Lock _rateLock = new();
    private DateTimeOffset _macVendorsNextAllowed = DateTimeOffset.MinValue;
    private DateTimeOffset _macVendorLookupNextAllowed = DateTimeOffset.MinValue;
    private int _nextProviderToggle;
    private bool _dirty;

    public MacVendorLookup()
    {
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add("User-Agent", "InternetMonitor/2.0");
        LoadCache();
    }

    /// <summary>
    /// Resolves a MAC address to a manufacturer name, or null if neither provider recognizes it.
    /// Concurrent lookups for the same not-yet-cached OUI share one in-flight request rather than
    /// each starting their own - a scan enriches every responding device at once, and several
    /// devices from the same manufacturer is the common case, not the exception.
    /// </summary>
    public Task<string?> LookupAsync(string macAddress, CancellationToken cancellationToken)
    {
        string oui = OuiKey(macAddress);
        if (oui.Length == 0)
        {
            return Task.FromResult<string?>(null);
        }

        if (_cache.TryGetValue(oui, out string? cached))
        {
            return Task.FromResult(cached);
        }

        return _inFlight.GetOrAdd(oui, key => ResolveAsync(key, cancellationToken));
    }

    private async Task<string?> ResolveAsync(string oui, CancellationToken cancellationToken)
    {
        try
        {
            // Alternate which provider goes first per new OUI - spreads load across both over the
            // course of a scan, and whichever goes second acts as a fallback for what the first
            // one does not recognize.
            bool macVendorsFirst = Interlocked.Increment(ref _nextProviderToggle) % 2 == 0;

            string? vendor = macVendorsFirst
                ? await QueryMacVendorsAsync(oui, cancellationToken).ConfigureAwait(false)
                    ?? await QueryMacVendorLookupAsync(oui, cancellationToken).ConfigureAwait(false)
                : await QueryMacVendorLookupAsync(oui, cancellationToken).ConfigureAwait(false)
                    ?? await QueryMacVendorsAsync(oui, cancellationToken).ConfigureAwait(false);

            _cache[oui] = vendor;
            _dirty = true;
            return vendor;
        }
        finally
        {
            _inFlight.TryRemove(oui, out _);
        }
    }

    /// <summary>Plain text response: the vendor name itself, or 404 when unrecognized, or 429 when rate-limited - either failure is treated the same, as "this provider has nothing".</summary>
    private async Task<string?> QueryMacVendorsAsync(string oui, CancellationToken cancellationToken)
    {
        await WaitForRateLimitAsync(Provider.MacVendors, cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HttpTimeout);

            using HttpResponseMessage response = await _http
                .GetAsync($"https://api.macvendors.com/{oui}", timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            string text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>JSON array of matching ranges; the first entry's "company" field is the vendor. 204 No Content means no match.</summary>
    private async Task<string?> QueryMacVendorLookupAsync(string oui, CancellationToken cancellationToken)
    {
        await WaitForRateLimitAsync(Provider.MacVendorLookup, cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HttpTimeout);

            using HttpResponseMessage response = await _http
                .GetAsync($"https://www.macvendorlookup.com/api/v2/{oui}/json", timeout.Token)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NoContent || !response.IsSuccessStatusCode)
            {
                return null;
            }

            string json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            JsonNode? root = JsonNode.Parse(json);
            string? company = root is JsonArray { Count: > 0 } array ? array[0]?["company"]?.GetValue<string>() : null;

            return string.IsNullOrWhiteSpace(company) ? null : company;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private enum Provider
    {
        MacVendors,
        MacVendorLookup,
    }

    /// <summary>
    /// Waits until this provider's next allowed slot, then reserves the following one - a simple
    /// shared pacing gate every concurrent caller funnels through, so the real request rate against
    /// one provider stays at or under its minimum interval no matter how many devices a scan is
    /// enriching at once.
    /// </summary>
    private async Task WaitForRateLimitAsync(Provider provider, CancellationToken cancellationToken)
    {
        TimeSpan minInterval = provider == Provider.MacVendors ? MacVendorsMinInterval : MacVendorLookupMinInterval;

        while (true)
        {
            TimeSpan delay;
            lock (_rateLock)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateTimeOffset nextAllowed = provider == Provider.MacVendors ? _macVendorsNextAllowed : _macVendorLookupNextAllowed;

                if (now >= nextAllowed)
                {
                    DateTimeOffset reserved = now + minInterval;
                    if (provider == Provider.MacVendors)
                    {
                        _macVendorsNextAllowed = reserved;
                    }
                    else
                    {
                        _macVendorLookupNextAllowed = reserved;
                    }

                    return;
                }

                delay = nextAllowed - now;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The first six hex digits of the address, regardless of separators - the OUI, and nothing device-specific.</summary>
    private static string OuiKey(string macAddress)
    {
        Span<char> buffer = stackalloc char[6];
        int count = 0;

        foreach (char c in macAddress)
        {
            if (count == 6)
            {
                break;
            }

            if (Uri.IsHexDigit(c))
            {
                buffer[count++] = char.ToUpperInvariant(c);
            }
        }

        return count == 6 ? new string(buffer) : string.Empty;
    }

    private static string CachePath => Path.Combine(AppPaths.CacheDir, "mac-vendors.json");

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            Dictionary<string, string?>? stored =
                JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(CachePath));

            if (stored is null)
            {
                return;
            }

            foreach ((string key, string? value) in stored)
            {
                _cache[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt cache just means this run resolves OUIs again.
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
