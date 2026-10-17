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
    private readonly BackgroundLoop _loop = new();

    private bool _enabled;
    private TimeSpan _interval = TimeSpan.FromHours(4);

    public event EventHandler<SpeedTestResult>? RunCompleted;

    /// <summary>
    /// Asked just before each scheduled run; true skips that one run (the schedule carries on).
    /// Used to keep automatic tests off a metered connection: a test moves tens to hundreds of
    /// megabytes, which on a data plan is a cost the user did not ask for.
    /// </summary>
    public Func<bool>? SkipRun { get; set; }

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
            _loop.Stop();
            return;
        }

        if (!_loop.IsRunning)
        {
            _loop.Start(Body(_interval));
        }
        else if (changed)
        {
            _loop.Restart(Body(_interval));
        }
    }

    private Func<CancellationToken, Task> Body(TimeSpan interval) =>
        cancellationToken => BackgroundLoop.EveryAsync(interval, RunOnceAsync, cancellationToken);

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (SkipRun?.Invoke() == true)
        {
            return;
        }

        try
        {
            SpeedTestResult result = await _client.RunAsync(null, cancellationToken).ConfigureAwait(false);
            RunCompleted?.Invoke(this, result);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Best-effort background job - a failed automatic run shouldn't stop future ones; just
            // skip this cycle and try again next interval.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _loop.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
