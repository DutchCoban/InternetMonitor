using System.Text.Json;

namespace InternetMonitor.Core.Updates;

/// <summary>
/// What one check against GitHub found. The URLs are null when no newer release exists, when the
/// release lacks the asset, or when a URL did not point where a GitHub release URL should.
/// </summary>
/// <param name="SignatureDownloadUrl">The <c>.sig</c> asset - see <see cref="ReleaseSignature"/>. Without it the newer release is only reported, never installed.</param>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version? Latest,
    string? ReleaseUrl,
    string? AssetDownloadUrl,
    string? SignatureDownloadUrl)
{
    public string? LatestVersion => Latest?.ToString();
}

/// <summary>
/// Checks the GitHub releases feed for a newer published version than the one currently running.
/// Ported from v1's identically-named class (<c>InternetMonitor/Network/UpdateChecker.cs</c>),
/// same shape throughout: checks once immediately, then once a day (GitHub's unauthenticated rate
/// limit is 60 requests/hour per IP, so this stays far under it), plus exposes
/// <see cref="CheckOnceAsync"/> directly for a manual "Check for updates" trigger. Best-effort
/// throughout: a failed check (offline, GitHub down, rate-limited) must never disrupt monitoring,
/// so every failure mode just yields no result rather than throwing.
///
/// Two differences from the v1 template: <see cref="Reconfigure"/> replaces v1's bare
/// Start()/Stop() pair - this is the same live-reconfigurable-background-loop shape
/// <see cref="Monitors.KumaPusher.Reconfigure"/> and <see cref="Monitors.NetworkHealthMonitor.Reconfigure"/>
/// already use elsewhere in v2 - and the asset URLs are resolved from the same JSON response's
/// "assets" array so a caller that wants to self-update has its download links without a second
/// HTTP round trip.
///
/// The response is data from the network, not something to act on blindly: only URLs on GitHub's
/// own hosts, over HTTPS, are passed on (<see cref="IsTrustedReleaseUrl"/>,
/// <see cref="IsTrustedAssetUrl"/>), and only the asset with exactly the expected name counts - a
/// release with a differently named executable is reported, not installed.
/// </summary>
public sealed class UpdateChecker : IAsyncDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/DutchCoban/InternetMonitor/releases/latest";

    /// <summary>The release asset the publish process produces. Exactly this name: no "first .exe" fallback.</summary>
    public const string AssetName = "InternetMonitor.exe";

    /// <summary>A release description is a few KB; anything beyond this is not the API answering.</summary>
    private const long MaxResponseBytes = 1024 * 1024;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;
    private readonly Monitors.BackgroundLoop _loop = new();

    public event EventHandler<UpdateCheckResult>? CheckCompleted;

    public UpdateChecker(Version currentVersion)
    {
        _currentVersion = currentVersion;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = MaxResponseBytes };
        // GitHub's API rejects requests with no User-Agent header.
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("InternetMonitor");
    }

    /// <summary>
    /// Starts or stops the background loop to match <paramref name="enabled"/>. Idempotent -
    /// calling this again with the same value is a no-op, matching how Settings saves (and so
    /// re-applies) on every toggle flip, not just a final "Apply".
    /// </summary>
    public void Reconfigure(bool enabled)
    {
        if (!enabled)
        {
            _loop.Stop();
            return;
        }

        // Once straight away, then daily.
        _loop.Start(token => Monitors.BackgroundLoop.EveryAsync(CheckInterval, CheckOnceAsync, token, firstTickNow: true));
    }

    /// <summary>
    /// Runs one check immediately, raises <see cref="CheckCompleted"/> on success, and returns the
    /// same result for a caller (e.g. a manual "Check for updates" click) that wants it directly
    /// rather than via the event. Returns null on any failure - offline, GitHub unreachable,
    /// unexpected response shape - never throws.
    /// </summary>
    public async Task<UpdateCheckResult?> CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            UpdateCheckResult? result = ParseRelease(doc.RootElement, _currentVersion);
            if (result is not null)
            {
                CheckCompleted?.Invoke(this, result);
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one release from the API's JSON. Null when there is no parseable tag; otherwise a
    /// result whose URLs are filtered to GitHub's own hosts. Pure, so the filtering is testable.
    /// </summary>
    public static UpdateCheckResult? ParseRelease(JsonElement release, Version currentVersion)
    {
        if (!release.TryGetProperty("tag_name", out JsonElement tagElement) || tagElement.GetString() is not { } tagName)
        {
            return null;
        }

        // Release tags are "v1.1.3" - strip the leading v/V before parsing as a Version.
        if (!Version.TryParse(tagName.TrimStart('v', 'V'), out Version? latest))
        {
            return null;
        }

        string? releaseUrl = release.TryGetProperty("html_url", out JsonElement urlElement) && urlElement.GetString() is { } html && IsTrustedReleaseUrl(html)
            ? html
            : null;

        (string? asset, string? signature) = FindAssetUrls(release);
        return new UpdateCheckResult(latest > currentVersion, latest, releaseUrl, asset, signature);
    }

    /// <summary>The release page: HTTPS on github.com, nothing else is handed to the browser.</summary>
    public static bool IsTrustedReleaseUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A download link: HTTPS on github.com (the API's "browser_download_url") or on the host it
    /// redirects assets to. Anything else is not downloaded, let alone executed.
    /// </summary>
    public static bool IsTrustedAssetUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase));

    private static (string? Asset, string? Signature) FindAssetUrls(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        string? asset = null;
        string? signature = null;
        foreach (JsonElement entry in assets.EnumerateArray())
        {
            if (!entry.TryGetProperty("name", out JsonElement nameElement) || nameElement.GetString() is not { } name
                || !entry.TryGetProperty("browser_download_url", out JsonElement urlElement) || urlElement.GetString() is not { } url
                || !IsTrustedAssetUrl(url))
            {
                continue;
            }

            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                asset = url;
            }
            else if (string.Equals(name, ReleaseSignature.SignatureAssetName, StringComparison.OrdinalIgnoreCase))
            {
                signature = url;
            }
        }

        return (asset, signature);
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
