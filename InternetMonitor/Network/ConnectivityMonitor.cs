namespace InternetMonitor.Network;

/// <summary>
/// Polls connectivity on a fixed interval and drives a debounced state machine so brief
/// network blips don't get reported as outages. Raises events on a thread-pool thread -
/// consumers are responsible for marshaling to their own UI thread. Has no dependency on
/// System.Windows.Forms or any other UI framework.
/// </summary>
public sealed class ConnectivityMonitor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int ConsecutiveFailuresForOutage = 2;
    private const int ConsecutiveSuccessesForRecovery = 1;

    private readonly HttpClient _httpClient;
    private readonly ConnectivityTest _test;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _checkInFlight;

    private ConnectivityState _state = ConnectivityState.Unknown;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private DateTimeOffset? _outageEpisodeStartedUtc;

    public event EventHandler<ConnectivityStateChangedEventArgs>? StateChanged;
    public event EventHandler<ConnectivityCheckCompletedEventArgs>? CheckCompleted;

    public ConnectivityState CurrentState => _state;
    public DateTimeOffset? LastCheckUtc { get; private set; }
    public DateTimeOffset? OutageStartedUtc => _outageEpisodeStartedUtc;

    public ConnectivityMonitor()
    {
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _test = new ConnectivityTest(_httpClient);
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

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_checkInFlight)
                {
                    continue;
                }

                _checkInFlight = true;
                try
                {
                    ConnectivityCheckResult result = await _test.RunAsync(cancellationToken).ConfigureAwait(false);
                    LastCheckUtc = DateTimeOffset.UtcNow;
                    CheckCompleted?.Invoke(this, new ConnectivityCheckCompletedEventArgs
                    {
                        IsSuccess = result.IsSuccess,
                        TimestampUtc = LastCheckUtc.Value,
                    });
                    Advance(result.IsSuccess);
                }
                finally
                {
                    _checkInFlight = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private void Advance(bool success)
    {
        ConnectivityState previous = _state;

        if (success)
        {
            _consecutiveFailures = 0;
            _consecutiveSuccesses++;

            if (_state == ConnectivityState.Unknown)
            {
                _state = ConnectivityState.Connected;
                _outageEpisodeStartedUtc = null;
            }
            else if (_state != ConnectivityState.Connected && _consecutiveSuccesses >= ConsecutiveSuccessesForRecovery)
            {
                _state = ConnectivityState.Connected;
                _outageEpisodeStartedUtc = null;
            }
        }
        else
        {
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;

            if (_state is ConnectivityState.Connected or ConnectivityState.Unknown)
            {
                _state = ConnectivityState.SuspectedOutage;
                _outageEpisodeStartedUtc ??= DateTimeOffset.UtcNow;
            }
            else if (_state == ConnectivityState.SuspectedOutage && _consecutiveFailures >= ConsecutiveFailuresForOutage)
            {
                _state = ConnectivityState.Outage;
            }
            // Already Outage: stays Outage. Re-notification suppression is the UI layer's job.
        }

        if (_state != previous)
        {
            StateChanged?.Invoke(this, new ConnectivityStateChangedEventArgs
            {
                OldState = previous,
                NewState = _state,
                TimestampUtc = DateTimeOffset.UtcNow,
                OutageStartedUtc = _outageEpisodeStartedUtc,
            });
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

        _timer?.Dispose();
        _httpClient.Dispose();
        _loopCts?.Dispose();
    }
}
