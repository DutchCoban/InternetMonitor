using InternetMonitor.Logging;

namespace InternetMonitor.Network;

/// <summary>
/// Sends an unconditional periodic heartbeat GET to a fully-preformed Uptime Kuma push-monitor
/// URL. Deliberately has no dependency on <see cref="ConnectivityMonitor"/> or
/// <see cref="SecondaryStatusMonitor"/> - it is a plain timer, matching the reference
/// implementation this replaces (push, then sleep, forever). "Active" is defined purely as
/// "a non-empty URL is configured" - there is no separate enabled flag.
/// </summary>
public sealed class UptimeKumaPusher : IAsyncDisposable
{
    public const int MinimumIntervalSeconds = 60;

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SimpleFileLogger _logger;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string _url = string.Empty;
    private TimeSpan _interval = TimeSpan.FromSeconds(MinimumIntervalSeconds);
    private bool _lastPushFailed;

    public UptimeKumaPusher(SimpleFileLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Applies settings and starts/stops/restarts the loop as needed. The caller (settings
    /// persistence) is expected to have already clamped <paramref name="intervalSeconds"/> to
    /// at least <see cref="MinimumIntervalSeconds"/> - this method trusts that rather than
    /// re-clamping in a third place.
    /// </summary>
    public void Reconfigure(string url, int intervalSeconds)
    {
        string trimmedUrl = url.Trim();
        var newInterval = TimeSpan.FromSeconds(intervalSeconds);
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
        // Push immediately, then wait - matches the legacy while(true){push(); sleep();}
        // semantics rather than PeriodicTimer's default wait-then-tick.
        await PushOnceAsync(url, cancellationToken).ConfigureAwait(false);

        // Scoped locally (not a shared field): a live Reconfigure() cancels this loop and
        // starts a fresh one without awaiting the old one's shutdown, so a shared timer field
        // touched by both the old and new loop would race. Cancelling the token alone is
        // enough to unblock WaitForNextTickAsync, so this loop cleans up its own timer.
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
            if (!response.IsSuccessStatusCode)
            {
                LogFailureOnce($"KUMA_PUSH_FAILED status={(int)response.StatusCode}");
            }
            else
            {
                _lastPushFailed = false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            // InvalidOperationException covers a malformed push URL (e.g. from a hand-edited
            // settings.json) - without this, a bad URL would permanently kill the push loop
            // instead of just failing this one push.
            LogFailureOnce($"KUMA_PUSH_ERROR {ex.GetType().Name}");
        }
    }

    private void LogFailureOnce(string message)
    {
        if (_lastPushFailed)
        {
            return;
        }

        _logger.Log(message);
        _lastPushFailed = true;
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
