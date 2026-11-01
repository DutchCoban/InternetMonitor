using System.Collections.Frozen;
using System.Net;
using System.Reflection;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Monitors;

namespace InternetMonitor.Core.Tools.LanScan;

/// <summary>
/// Which manufacturer a MAC address belongs to, answered from the IEEE's own registry rather than
/// by asking a lookup service: a snapshot of the MA-L list ships inside the executable (refreshed
/// at every release by <c>tools/Release/Update-OuiList.ps1</c>), and once a day the app asks the
/// IEEE whether the list has changed and, if so, downloads the new one into its cache folder.
///
/// Nothing about the user's devices ever leaves the machine this way - the earlier design sent
/// each device's address prefix to two lookup services in the United States - and the list is
/// never older than a day, where a lookup service is only as fresh as its own copy. The daily
/// check is a conditional GET: an unchanged list costs a few hundred bytes, a changed one about
/// 4 MB. The IEEE is an American standards body, but its registry is the one source every lookup
/// service copies from; there is no European copy to prefer, and the download tells it nothing.
///
/// The refresh has its own switch (<see cref="AppSettings.LanScanVendorLookup"/>); off, the
/// built-in snapshot and whatever was cached before still answer, just without getting newer.
/// </summary>
public sealed class OuiRegistry : IAsyncDisposable
{
    public const string ListUrl = "https://standards-oui.ieee.org/oui/oui.csv";

    /// <summary>The list is about 4 MB; anything far beyond that is not the registry.</summary>
    private const long MaxListBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan FirstRefreshDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly BackgroundLoop _loop = new();
    private readonly Lazy<FrozenDictionary<string, string>> _builtIn = new(LoadBuiltIn);
    private volatile FrozenDictionary<string, string>? _downloaded;

    public OuiRegistry()
        : this(new HttpClientHandler(), AppPaths.CacheDir)
    {
    }

    /// <summary>The same registry over a substituted HTTP handler and cache folder, so the tests can play the IEEE without a network.</summary>
    internal OuiRegistry(HttpMessageHandler handler, string cacheDir)
    {
        _cacheDir = cacheDir;
        _http = new HttpClient(handler) { Timeout = HttpTimeout, MaxResponseContentBufferSize = MaxListBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InternetMonitor/2.8");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("(+https://github.com/DutchCoban/InternetMonitor)");
        LoadCache();
    }

    /// <summary>When the cached list was downloaded, or null when only the built-in snapshot is there.</summary>
    public DateTimeOffset? CachedAtUtc { get; private set; }

    /// <summary>The manufacturer for an address, or null when its prefix is not in the registry.</summary>
    public string? Lookup(string macAddress)
    {
        string oui = OuiKey(macAddress);
        if (oui.Length == 0)
        {
            return null;
        }

        // The downloaded list is newer than the snapshot, so it wins where both know the prefix.
        return _downloaded is { } downloaded && downloaded.TryGetValue(oui, out string? fresh)
            ? fresh
            : _builtIn.Value.GetValueOrDefault(oui);
    }

    /// <summary>Starts or stops the daily refresh. Idempotent, like every other Reconfigure in this app.</summary>
    public void Reconfigure(bool refreshDaily)
    {
        if (!refreshDaily)
        {
            _loop.Stop();
            return;
        }

        _loop.Start(async token =>
        {
            // Not in the first seconds after start-up, when the connection is being judged.
            await Task.Delay(FirstRefreshDelay, token).ConfigureAwait(false);
            await BackgroundLoop.EveryAsync(RefreshInterval, async t => await RefreshOnceAsync(t).ConfigureAwait(false), token, firstTickNow: true).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Asks the IEEE whether the list changed since the cached copy (its ETag), downloads it when it
    /// did, and swaps it in. True when a new list was taken; false when unchanged or unreachable -
    /// a failed refresh only means the list stays as it was.
    /// </summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ListUrl);
            if (ReadEtag() is { Length: > 0 } etag)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified || !response.IsSuccessStatusCode)
            {
                return false;
            }

            string csv = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Dictionary<string, string> parsed = ParseCsv(csv);
            if (parsed.Count < 20_000)
            {
                // Whatever this was, it was not the registry.
                return false;
            }

            _downloaded = parsed.ToFrozenDictionary();
            CachedAtUtc = DateTimeOffset.UtcNow;
            SaveCache(parsed, response.Headers.ETag?.ToString());
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The IEEE file is CSV with a header: Registry,Assignment,Organization Name,Organization Address.
    /// Only the assignment and the name are kept; a quoted field may hold commas and doubled quotes.
    /// </summary>
    internal static Dictionary<string, string> ParseCsv(string csv)
    {
        var result = new Dictionary<string, string>(40_000, StringComparer.Ordinal);
        foreach (string line in csv.Split('\n'))
        {
            List<string> fields = SplitCsvLine(line.TrimEnd('\r'));
            if (fields.Count < 3)
            {
                continue;
            }

            string oui = OuiKey(fields[1]);
            string name = fields[2].Trim();
            if (oui.Length == 6 && name.Length > 0)
            {
                result[oui] = name;
            }
        }

        return result;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var fields = new List<string>(4);
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return fields;
    }

    /// <summary>The snapshot and the cache share one shape: "OUI&lt;tab&gt;name", one per line.</summary>
    private static Dictionary<string, string> ParseTsv(TextReader reader)
    {
        var result = new Dictionary<string, string>(40_000, StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            int tab = line.IndexOf('\t');
            if (tab == 6 && line.Length > 7)
            {
                result[line[..6]] = line[7..];
            }
        }

        return result;
    }

    private static FrozenDictionary<string, string> LoadBuiltIn()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("InternetMonitor.Assets.oui.tsv");
        if (stream is null)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        using var reader = new StreamReader(stream);
        return ParseTsv(reader).ToFrozenDictionary();
    }

    /// <summary>The first six hex digits of the address, regardless of separators - the OUI, and nothing device-specific.</summary>
    internal static string OuiKey(string macAddress)
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

    private string CachePath => Path.Combine(_cacheDir, "oui.tsv");

    private string EtagPath => Path.Combine(_cacheDir, "oui.etag");

    private string? ReadEtag()
    {
        try
        {
            return File.Exists(EtagPath) ? File.ReadAllText(EtagPath).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return;
            }

            using var reader = new StreamReader(CachePath);
            Dictionary<string, string> cached = ParseTsv(reader);
            if (cached.Count > 0)
            {
                _downloaded = cached.ToFrozenDictionary();
                CachedAtUtc = File.GetLastWriteTimeUtc(CachePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or damaged cache just means the snapshot answers until the next refresh.
        }
    }

    private void SaveCache(Dictionary<string, string> list, string? etag)
    {
        try
        {
            AppPaths.TryEnsureDirectory(_cacheDir);
            File.WriteAllLines(CachePath, list.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "\t" + p.Value));
            if (etag is { Length: > 0 })
            {
                File.WriteAllText(EtagPath, etag);
            }
            else
            {
                File.Delete(EtagPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the list is in memory for this run and is downloaded again next time.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}
