using System.Text.Json;

namespace InternetMonitor.Network;

public sealed record UpdateCheckResult(bool IsUpdateAvailable, string? LatestVersion, string? ReleaseUrl);

/// <summary>
/// Checks the GitHub releases feed for a newer published version than the one currently running.
/// Self-contained, same "own loop, own lifecycle" shape as <see cref="UptimeKumaPusher"/> - checks
/// once immediately, then once a day (GitHub's unauthenticated rate limit is 60 requests/hour per
/// IP, so this stays far under it), plus exposes <see cref="CheckOnceAsync"/> directly for a
/// manual "Check for updates" trigger. Best-effort throughout: a failed check (offline, GitHub
/// down, rate-limited) must never disrupt monitoring, so every failure mode just yields no result
/// rather than throwing.
/// </summary>
public sealed class UpdateChecker : IAsyncDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/DutchCoban/InternetMonitor/releases/latest";
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

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_loopCts.Token);
    }

    /// <summary>Stops the background loop without disposing - lets Settings' "automatically check for updates" toggle be applied live, mirroring UptimeKumaPusher's Start/Stop shape.</summary>
    public void Stop()
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

    /// <summary>Runs one check immediately, raises <see cref="CheckCompleted"/> on success, and returns the same result for a caller (e.g. a manual "Check for updates" click) that wants it directly rather than via the event. Returns null on any failure - offline, GitHub unreachable, unexpected response shape - never throws.</summary>
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

            // Release tags are "v1.1.3" - strip the leading v/V before parsing as a Version.
            string versionText = tagName.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out Version? latestVersion))
            {
                return null;
            }

            var result = new UpdateCheckResult(latestVersion > _currentVersion, latestVersion.ToString(), releaseUrl);
            CheckCompleted?.Invoke(this, result);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or IOException)
        {
            return null;
        }
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
