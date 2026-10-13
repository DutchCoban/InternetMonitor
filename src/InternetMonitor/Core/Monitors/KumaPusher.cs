namespace InternetMonitor.Core.Monitors;

/// <summary>
/// Sends an unconditional periodic heartbeat GET to a fully-formed Uptime Kuma push-monitor URL.
/// Deliberately independent of everything else in the app - no dependency on the step pipeline or
/// its state - matching the reference implementation this is a push monitor for: push, then sleep,
/// forever. "Active" is defined purely as "a non-empty URL is configured"; there is no separate
/// enabled flag, carried over unchanged from v1's identically-named pusher.
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

    /// <summary>
    /// Applies settings and starts/stops/restarts the loop as needed. Idempotent: calling this
    /// again with the same URL and interval while already running is a no-op, which matters
    /// because Settings saves on every keystroke-equivalent change, not just on a final "Apply".
    /// </summary>
    public void Reconfigure(string url, int intervalSeconds)
    {
        string trimmedUrl = url.Trim();
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
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            // InvalidOperationException covers a malformed push URL (e.g. from a hand-edited
            // settings.json) - without this, a bad URL would permanently kill the push loop instead
            // of just failing this one push.
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
