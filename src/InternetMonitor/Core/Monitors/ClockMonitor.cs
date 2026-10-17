using InternetMonitor.Net;

namespace InternetMonitor.Core.Monitors;

/// <summary>The last clock check: the server asked, how far this clock is from it (null when the server could not be reached), and when.</summary>
public sealed record ClockStatus(DateTimeOffset CheckedUtc, string Server, TimeSpan? Offset, string? Error)
{
    /// <summary>Past this, secure connections start failing on certificate validity dates.</summary>
    public static readonly TimeSpan DriftThreshold = TimeSpan.FromSeconds(60);

    public bool Drifted => Offset is { } offset && offset.Duration() >= DriftThreshold;
}

/// <summary>
/// Asks a time server once an hour how far this computer's clock is off. A clock that is minutes
/// wrong fails every TLS handshake - the site looks down while nothing on the line is - and the
/// target step says so when it sees a TLS failure (<see cref="Latest"/> is read there).
///
/// One UDP question an hour to a server the user can change; <c>pool.ntp.org</c> by default.
/// </summary>
public sealed class ClockMonitor : IAsyncDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Func<string> _server;
    private readonly BackgroundLoop _loop = new();

    public ClockMonitor(Func<string> server) => _server = server;

    /// <summary>The most recent check, readable from anywhere - the target step consults it the way <c>SpeedTestActivity</c> is consulted.</summary>
    public static ClockStatus? Latest { get; private set; }

    /// <summary>Raised after every check, on the loop's thread; UI subscribers must marshal.</summary>
    public event Action<ClockStatus>? Changed;

    public void Start() => _loop.Start(token => RunAsync(FirstCheckDelay, token));

    /// <summary>The server changed in Settings: check it soon rather than at the next hourly tick.</summary>
    public void Reconfigure() => _loop.Restart(token => RunAsync(TimeSpan.FromSeconds(2), token));

    private async Task RunAsync(TimeSpan firstDelay, CancellationToken cancellationToken)
    {
        await Task.Delay(firstDelay, cancellationToken).ConfigureAwait(false);
        await BackgroundLoop.EveryAsync(Interval, CheckOnceAsync, cancellationToken, firstTickNow: true).ConfigureAwait(false);
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        string server = _server().Trim();
        if (server.Length == 0)
        {
            server = "pool.ntp.org";
        }

        SntpResult? result = await SntpClient.QueryAsync(server, Timeout, cancellationToken).ConfigureAwait(false);
        var status = result is null
            ? new ClockStatus(DateTimeOffset.UtcNow, server, null, "no answer")
            : new ClockStatus(DateTimeOffset.UtcNow, server, result.Offset, null);

        Latest = status;
        Changed?.Invoke(status);
    }

    public ValueTask DisposeAsync() => _loop.DisposeAsync();
}
