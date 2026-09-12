namespace InternetMonitor.Network.Probes;

public enum ProbeStatus
{
    Unknown,
    Checking,
    Ok,
    Warning,
    Error,
    Blocked,
}

/// <summary>
/// Common surface every probe result exposes for generic rendering (details table, JSONL
/// logging, export). Probes still return their own richer typed result so the diagnosis
/// engine can reason over strongly-typed fields rather than parsing strings back out of
/// <see cref="ToDetails"/>.
/// </summary>
public interface IProbeResult
{
    string ProbeId { get; }
    ProbeStatus Status { get; }
    string Summary { get; }
    TimeSpan Duration { get; }
    DateTimeOffset TimestampUtc { get; }
    string? ErrorDetail { get; }

    /// <summary>
    /// A single natural numeric measurement for sparkline charting (e.g. latency in ms, time
    /// offset in seconds) - null for probes with no meaningful continuous value (e.g. the
    /// network-adapter check, which is state rather than a measurement).
    /// </summary>
    double? ChartValue { get; }

    IReadOnlyDictionary<string, string> ToDetails();
}

/// <summary>
/// A single, independent, self-contained check. Probes only report what they observed -
/// they never decide "internet is down"; that interpretation is the diagnosis engine's job
/// (see Network/Diagnosis/DiagnosisEngine.cs), which reasons over the combination of results.
/// </summary>
public interface IProbe
{
    string Id { get; }
    string Category { get; }
    Task<IProbeResult> RunAsync(CancellationToken cancellationToken);
}
