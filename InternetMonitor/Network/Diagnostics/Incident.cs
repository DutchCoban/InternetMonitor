using InternetMonitor.Network.Diagnosis;

namespace InternetMonitor.Network.Diagnostics;

public enum IncidentStatus
{
    Open,
    Active,
    Resolved,
}

/// <summary>
/// A single tracked outage/degradation episode, as opened, updated, and closed by
/// <see cref="IncidentTracker"/> and persisted by <see cref="IncidentStore"/>. Captures a
/// snapshot of every probe's result at both open and resolution time so a support ticket
/// created from it (see <see cref="DiagnosticReportBuilder"/>) has full context without needing
/// to reproduce the original conditions.
/// </summary>
public sealed class Incident
{
    public string Id { get; set; } = string.Empty;
    public DiagnosisClassification Classification { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public IncidentStatus Status { get; set; }
    public string Diagnosis { get; set; } = string.Empty;
    public List<string> AffectedProbeIds { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> SnapshotAtStart { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>>? SnapshotAtResolution { get; set; }

    public TimeSpan? Duration => EndUtc is null ? null : EndUtc - StartUtc;
}
