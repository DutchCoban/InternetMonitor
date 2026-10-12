using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using Microsoft.Win32;

namespace InternetMonitor.App;

/// <summary>
/// Decides how often the pipeline runs, and kicks it immediately when something happens that makes
/// the current reading stale.
///
/// Two rates, because the two situations are genuinely different. With a window open the user is
/// watching, and a dashboard that updates every fifteen seconds does not feel live - that runs at
/// the foreground rate (3s by default). With only the tray icon alive nobody is looking, so the
/// background rate (15s, v1's interval) is plenty and costs a fraction of the traffic.
///
/// On top of that, three events make any existing reading worthless and trigger a cycle at once,
/// because waiting out a timer to notice them is exactly the lag that made v1 feel slow:
/// an address change, an availability change, and waking from sleep. The last one matters most in
/// practice - a laptop opened at a different desk is the single most common way for every answer
/// on screen to be wrong.
///
/// Note what is deliberately *not* here: history recording. At the foreground rate the pipeline
/// runs five times as often, and letting that drive history would make retention and chart density
/// depend on whether a window happened to be open. MetricStore coalesces instead - see phase 4.
/// </summary>
public sealed class CadenceController : IDisposable
{
    private readonly StepPipeline _pipeline;
    private readonly Func<AppSettings> _settings;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private volatile bool _foreground;
    private Task? _loop;
    private bool _disposed;

    public CadenceController(StepPipeline pipeline, Func<AppSettings> settings)
    {
        _pipeline = pipeline;
        _settings = settings;

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// True while a window the user can see is open. Set by the shell; the loop picks it up on its
    /// next wait rather than being restarted, so toggling it is free.
    /// </summary>
    public bool Foreground
    {
        get => _foreground;
        set
        {
            if (_foreground == value)
            {
                return;
            }

            _foreground = value;

            // Switching to foreground should feel instant rather than waiting out whatever
            // remains of a background interval.
            if (value)
            {
                RunNow();
            }
        }
    }

    public void Start() => _loop ??= RunLoopAsync(_shutdown.Token);

    /// <summary>Requests a cycle immediately. Safe to call from any thread, and cheap to call often.</summary>
    public void RunNow()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // A bounded semaphore with capacity one collapses a burst of wake requests into a
            // single extra cycle, which is what we want when several events fire at once (an
            // address change and an availability change usually arrive together).
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending - nothing to add.
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // One cycle straight away: the tray icon should mean something within a second of launch,
        // not after the first interval has elapsed.
        await SafeRunCycleAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan interval = CurrentInterval();
            try
            {
                await _wake.WaitAsync(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await SafeRunCycleAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SafeRunCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pipeline.RunCycleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private TimeSpan CurrentInterval()
    {
        AppSettings settings = _settings();
        return TimeSpan.FromSeconds(_foreground ? settings.ForegroundIntervalSeconds : settings.BackgroundIntervalSeconds);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => RunNow();

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => RunNow();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            RunNow();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _shutdown.Cancel();
        _shutdown.Dispose();
        _wake.Dispose();
    }
}
