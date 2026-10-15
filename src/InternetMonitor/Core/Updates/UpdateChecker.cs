using System.Text.Json;

namespace InternetMonitor.Core.Updates;

/// <summary>What one check against GitHub found. The URLs are null when no newer release exists.</summary>
public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl, string? AssetDownloadUrl);

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
/// already use elsewhere in v2 - and <see cref="UpdateCheckResult.AssetDownloadUrl"/> is a new
/// field, resolved from the same JSON response's "assets" array so a caller that wants to
/// self-update has a download link without a second HTTP round trip.
/// </summary>
public sealed class UpdateChecker : IAsyncDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/DutchCoban/InternetMonitor/releases/latest";

    /// <summary>The release asset v1/v2's own publish process produces - see the plan this shipped from for why a single, unversioned name is assumed.</summary>
    private const string AssetName = "InternetMonitor.exe";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public event EventHandler<UpdateCheckResult>? CheckCompleted;

    public UpdateChecker(Version currentVersion)
    {
        _currentVersion = currentVersion;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
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
            Stop();
            return;
        }

        if (_loopTask is null)
        {
            Start();
        }
    }

    private void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_loopCts.Token);
    }

    private void Stop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        await CheckOnceAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
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

            if (!doc.RootElement.TryGetProperty("tag_name", out JsonElement tagElement) || tagElement.GetString() is not { } tagName)
            {
                return null;
            }

            string? releaseUrl = doc.RootElement.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() : null;
            string? assetDownloadUrl = FindAssetDownloadUrl(doc.RootElement);

            // Release tags are "v1.1.3" - strip the leading v/V before parsing as a Version.
            string versionText = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out Version? latestVersion))
            {
                return null;
            }

            var result = new UpdateCheckResult(latestVersion > _currentVersion, latestVersion.ToString(), releaseUrl, assetDownloadUrl);
            CheckCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Prefers the asset literally named <see cref="AssetName"/> (every release so far has exactly
    /// one, named this), falling back to the first asset ending in ".exe" so a renamed asset still
    /// resolves instead of silently losing the self-update path.
    /// </summary>
    private static string? FindAssetDownloadUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement nameElement) || nameElement.GetString() is not { } name)
            {
                continue;
            }

            if (!asset.TryGetProperty("browser_download_url", out JsonElement urlElement) || urlElement.GetString() is not { } url)
            {
                continue;
            }

            if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            if (fallback is null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                fallback = url;
            }
        }

        return fallback;
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _httpClient.Dispose();
        _loopCts?.Dispose();
    }
}
