namespace InternetMonitor.Core.Monitors;

/// <summary>What the last push did, for the Settings page's status line.</summary>
public sealed record KumaPushStatus(DateTimeOffset AtUtc, bool Failed, string Detail);

/// <summary>
/// Sends an unconditional periodic heartbeat GET to a fully-formed Uptime Kuma push-monitor URL.
/// Deliberately independent of everything else in the app - no dependency on the step pipeline or
/// its state - matching the reference implementation this is a push monitor for: push, then sleep,
/// forever. "Active" is defined purely as "a valid URL is configured"; there is no separate
/// enabled flag, carried over unchanged from v1's identically-named pusher.
///
/// Valid means an absolute https address, checked here and not only in the Settings page: the URL
/// carries the monitor's token, and a hand-edited or imported settings.json with a plain http URL
/// would otherwise send that token in the clear every minute.
/// </summary>
public sealed class KumaPusher : IAsyncDisposable
{
    public const int MinimumIntervalSeconds = 60;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly BackgroundLoop _loop = new();

    private string _url = string.Empty;
    private TimeSpan _interval = TimeSpan.FromSeconds(MinimumIntervalSeconds);

    /// <summary>Whether the most recent push succeeded.</summary>
    public bool LastPushFailed => LastPush?.Failed ?? false;

    /// <summary>The most recent push, or null before the first one.</summary>
    public KumaPushStatus? LastPush { get; private set; }

    /// <summary>Raised after every push, on the loop's own thread; UI subscribers must marshal.</summary>
    public event Action<KumaPushStatus>? PushCompleted;

    /// <summary>Whether a push loop is running - that is, whether a usable URL is configured.</summary>
    public bool IsActive => _loop.IsRunning;

    /// <summary>The only URLs the pusher will call: absolute, and https so the token in them stays encrypted.</summary>
    public static bool IsUsableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Applies settings and starts/stops/restarts the loop as needed. Idempotent: calling this
    /// again with the same URL and interval while already running is a no-op, which matters
    /// because Settings saves on every keystroke-equivalent change, not just on a final "Apply".
    /// </summary>
    public void Reconfigure(string url, int intervalSeconds)
    {
        string trimmedUrl = url.Trim();
        if (!IsUsableUrl(trimmedUrl))
        {
            trimmedUrl = string.Empty;
        }

        var newInterval = TimeSpan.FromSeconds(Math.Max(MinimumIntervalSeconds, intervalSeconds));
        bool changed = trimmedUrl != _url || newInterval != _interval;
        _url = trimmedUrl;
        _interval = newInterval;

        if (string.IsNullOrEmpty(_url))
        {
            _loop.Stop();
            return;
        }

        if (!_loop.IsRunning)
        {
            _loop.Start(Body(_url, _interval));
        }
        else if (changed)
        {
            _loop.Restart(Body(_url, _interval));
        }
    }

    /// <summary>
    /// Push immediately, then wait - the push-then-sleep semantics of every Uptime Kuma client
    /// rather than wait-then-tick, so a fresh push URL shows up in Kuma right away instead of after
    /// a full interval. The URL and interval are captured here: a live reconfigure starts a new
    /// body without waiting for the old one, and the old one must keep pushing to what it was given.
    /// </summary>
    private Func<CancellationToken, Task> Body(string url, TimeSpan interval) =>
        cancellationToken => BackgroundLoop.EveryAsync(interval, token => PushOnceAsync(url, token), cancellationToken, firstTickNow: true);

    private async Task PushOnceAsync(string url, CancellationToken cancellationToken)
    {
        KumaPushStatus status;
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            status = new KumaPushStatus(DateTimeOffset.UtcNow, !response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or NotSupportedException)
        {
            // InvalidOperationException and NotSupportedException cover a URL HttpClient will not
            // send to - belt and braces next to the check in Reconfigure - so a bad URL fails this
            // one push rather than silently ending the loop.
            status = new KumaPushStatus(DateTimeOffset.UtcNow, true, ex is OperationCanceledException ? "timed out" : ex.Message);
        }

        LastPush = status;
        PushCompleted?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
