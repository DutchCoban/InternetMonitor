namespace InternetMonitor.Network;

public sealed record ConnectivityCheckResult(bool IsSuccess, TimeSpan Duration);

public sealed class ConnectivityStateChangedEventArgs : EventArgs
{
    public required ConnectivityState OldState { get; init; }
    public required ConnectivityState NewState { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Set when NewState is Outage or SuspectedOutage: when the current outage episode began.
    /// Null once the episode has fully resolved back to Connected.
    /// </summary>
    public DateTimeOffset? OutageStartedUtc { get; init; }
}

public sealed class ConnectivityCheckCompletedEventArgs : EventArgs
{
    public required bool IsSuccess { get; init; }
    public required DateTimeOffset TimestampUtc { get; init; }
}
