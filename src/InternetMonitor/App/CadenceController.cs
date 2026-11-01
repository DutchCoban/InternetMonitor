using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using Microsoft.Win32;

namespace InternetMonitor.App;

/// <summary>
/// Decides when the pipeline runs: a short interval while a window is open, a longer one in the
/// tray, straight away when the network changes or the machine wakes, and not at all while the
/// user has paused it. The loop itself is one timer; everything else is a reason to wake it early.
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
    private DateTimeOffset? _pausedUntil;
    private bool _paused;

    public CadenceController(StepPipeline pipeline, Func<AppSettings> settings)
    {
        _pipeline = pipeline;
        _settings = settings;

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Raised on whichever thread changed it when monitoring is paused or resumed; UI subscribers must marshal.</summary>
    public event Action? PauseChanged;

    /// <summary>On a metered connection (mobile data, a phone's hotspot) the background interval is stretched by this much.</summary>
    public const int MeteredSlowdown = 4;

    /// <summary>
    /// Whether the connection being monitored is metered right now. Step 2 knows; the host wires it
    /// in. While nobody is watching, a metered connection is checked four times less often: every
    /// cycle costs a few kilobytes (a TLS handshake among them), which is nothing on a line and
    /// something on a data plan.
    /// </summary>
    public Func<bool> IsMetered { get; set; } = static () => false;

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

    public bool IsPaused => _paused;

    /// <summary>When the pause ends by itself, or null for "until resumed" (and when not paused).</summary>
    public DateTimeOffset? PausedUntil => _paused ? _pausedUntil : null;

    public void Start() => _loop ??= RunLoopAsync(_shutdown.Token);

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

    /// <summary>
    /// Stops running cycles for <paramref name="duration"/>, or until <see cref="Resume"/> when
    /// null. Nothing is measured meanwhile - no history, no incidents - so a planned router restart
    /// does not end up in the record; a report shows the time as not measured. An incident that was
    /// open stays open until the first cycle after the pause settles it.
    /// </summary>
    public void Pause(TimeSpan? duration)
    {
        _pausedUntil = duration is { } span ? DateTimeOffset.UtcNow + span : null;
        _paused = true;
        PauseChanged?.Invoke();
    }

    public void Resume()
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        _pausedUntil = null;
        PauseChanged?.Invoke();
        RunNow();
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

            if (_paused)
            {
                if (_pausedUntil is { } until && DateTimeOffset.UtcNow >= until)
                {
                    Resume();
                }
                else
                {
                    continue;
                }
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
        if (_foreground)
        {
            return TimeSpan.FromSeconds(settings.ForegroundIntervalSeconds);
        }

        int seconds = settings.BackgroundIntervalSeconds;
        return TimeSpan.FromSeconds(IsMetered() ? seconds * MeteredSlowdown : seconds);
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
