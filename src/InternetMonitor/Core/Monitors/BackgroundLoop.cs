namespace InternetMonitor.Core.Monitors;

/// <summary>
/// One cancellable background task, with the start/stop/restart shape every periodic worker in
/// this app shares: the Kuma pusher, the update checker, the speed-test scheduler, the line-quality
/// sampler, the two network-health loops and the history flush. Each used to carry its own
/// <see cref="CancellationTokenSource"/>/<see cref="Task"/> pair and the same dozen lines around
/// them; this is those lines, once.
///
/// <see cref="Stop"/> cancels without waiting. A live reconfigure stops the old body and starts a
/// new one in the same call, and the old one winds down on its own - which is why a body must keep
/// everything it uses (its timer above all) local rather than in a field the new body also touches.
/// <see cref="DisposeAsync"/> does wait, so shutdown is orderly.
/// </summary>
public sealed class BackgroundLoop : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _task;

    public bool IsRunning => _task is not null;

    /// <summary>Starts <paramref name="body"/> unless something is already running.</summary>
    public void Start(Func<CancellationToken, Task> body)
    {
        if (_task is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _task = RunAsync(body, _cts.Token);
    }

    public void Restart(Func<CancellationToken, Task> body)
    {
        Stop();
        Start(body);
    }

    /// <summary>Cancels the running body, if any, without waiting for it to finish.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _task = null;
    }

    /// <summary>
    /// Calls <paramref name="tick"/> every <paramref name="interval"/> until cancelled - first
    /// straight away when <paramref name="firstTickNow"/> is set, for a worker whose result should
    /// show up the moment it is switched on rather than a full interval later.
    /// </summary>
    public static async Task EveryAsync(TimeSpan interval, Func<CancellationToken, Task> tick, CancellationToken cancellationToken, bool firstTickNow = false)
    {
        if (firstTickNow)
        {
            await tick(cancellationToken).ConfigureAwait(false);
        }

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await tick(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        try
        {
            await body(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The normal end of a loop: Stop() or DisposeAsync().
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_task is not null)
        {
            await _task.ConfigureAwait(false);
        }

        _cts?.Dispose();
        _cts = null;
        _task = null;
    }
}
