namespace InternetMonitor.Core.Monitors;

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

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string _url = string.Empty;
    private TimeSpan _interval = TimeSpan.FromSeconds(MinimumIntervalSeconds);

    /// <summary>Whether the most recent push succeeded - for a future status indicator; not acted on internally.</summary>
    public bool LastPushFailed { get; private set; }

    /// <summary>Whether a push loop is running - that is, whether a usable URL is configured.</summary>
    public bool IsActive => _loopTask is not null;

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
            Stop();
            return;
        }

        if (_loopTask is null)
        {
            Start();
        }
        else if (changed)
        {
            Stop();
            Start();
        }
    }

    private void Start()
    {
        if (string.IsNullOrEmpty(_url) || _loopTask is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_url, _interval, _loopCts.Token);
    }

    private void Stop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    private async Task RunLoopAsync(string url, TimeSpan interval, CancellationToken cancellationToken)
    {
        // Push immediately, then wait - matches the push-then-sleep semantics of every Uptime Kuma
        // client rather than PeriodicTimer's default wait-then-tick, so a fresh push URL shows up
        // in Kuma right away instead of after a full interval.
        await PushOnceAsync(url, cancellationToken).ConfigureAwait(false);

        // Scoped locally (not a shared field): a live Reconfigure() cancels this loop and starts a
        // fresh one without awaiting the old one's shutdown, so a shared timer field touched by
        // both the old and new loop would race. Cancelling the token alone is enough to unblock
        // WaitForNextTickAsync, so this loop cleans up its own timer.
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PushOnceAsync(url, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/reconfigure path.
        }
    }

    private async Task PushOnceAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            LastPushFailed = !response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or NotSupportedException)
        {
            // InvalidOperationException and NotSupportedException cover a URL HttpClient will not
            // send to - belt and braces next to the check in Reconfigure - so a bad URL fails this
            // one push rather than silently ending the loop.
            LastPushFailed = true;
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
