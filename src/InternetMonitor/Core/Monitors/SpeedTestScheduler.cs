using InternetMonitor.Core.Tools.SpeedTest;

namespace InternetMonitor.Core.Monitors;

/// <summary>
/// Runs a <see cref="SpeedTestClient"/> test on its own schedule when enabled - the automatic half
/// of the Speed Test tool, whose manual "Run now" half lives entirely in <c>ToolsPage</c>. Same
/// <see cref="Reconfigure"/>-driven live-restart shape as <see cref="KumaPusher"/>: idempotent,
/// since Settings saves (and so re-applies this) on every toggle flip, not just a final "Apply".
///
/// Unlike <see cref="KumaPusher"/>, enabling this never runs an immediate test - carried over from
/// v1's own reasoning (<c>DiagnosticsCoordinator.RunAutoSpeedTestLoopAsync</c>'s doc comment):
/// the manual "Run now" button already covers "test right now", so turning auto-run on only ever
/// schedules a *future* run. A speed test also moves real data, unlike a Kuma heartbeat, so firing
/// one off the instant a checkbox is ticked would be a surprising side effect of a settings change.
/// </summary>
public sealed class SpeedTestScheduler : IAsyncDisposable
{
    private readonly SpeedTestClient _client = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _enabled;
    private TimeSpan _interval = TimeSpan.FromHours(4);

    public event EventHandler<SpeedTestResult>? RunCompleted;

    /// <summary>
    /// Applies settings and starts/stops/restarts the loop as needed. Idempotent: calling this
    /// again with the same enabled/interval while already running is a no-op.
    /// </summary>
    public void Reconfigure(bool enabled, int intervalHours)
    {
        var newInterval = TimeSpan.FromHours(Math.Max(1, intervalHours));
        bool changed = enabled != _enabled || newInterval != _interval;
        _enabled = enabled;
        _interval = newInterval;

        if (!_enabled)
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
        if (!_enabled || _loopTask is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = RunLoopAsync(_interval, _loopCts.Token);
    }

    private void Stop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
        _loopTask = null;
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // Scoped locally (not a shared field), same reasoning as KumaPusher's own loop timer: a
        // live Reconfigure() cancels this loop and starts a fresh one without awaiting the old
        // one's shutdown, so a shared timer field touched by both would race.
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    SpeedTestResult result = await _client.RunAsync(null, cancellationToken).ConfigureAwait(false);
                    RunCompleted?.Invoke(this, result);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    // Best-effort background job - a failed automatic run shouldn't stop future
                    // ones; just skip this cycle and try again next interval.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/reconfigure path.
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

        _client.Dispose();
        _loopCts?.Dispose();
    }
}
