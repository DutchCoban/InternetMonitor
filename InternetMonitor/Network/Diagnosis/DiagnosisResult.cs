using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network.Diagnosis;

public enum DiagnosisClassification
{
    Healthy,
    Network,
    IpConfiguration,
    Gateway,
    Internet,
    Dns,
    Https,
    Application,
    TimeSync,
    FirewallSuspected,
}

/// <summary>
/// <paramref name="Severity"/> is the single source of truth for how "bad" this diagnosis is -
/// every UI surface that needs to render a status color (tray icon, diagram segments) should key
/// off this rather than re-deriving it from <see cref="Classification"/>, since two returns with
/// the same classification (see TimeSync: drift vs. unreachable) can carry different severities.
/// </summary>
public sealed record DiagnosisResult(
    DiagnosisClassification Classification,
    string Headline,
    string Explanation,
    IReadOnlyList<string> AffectedProbeIds,
    ProbeStatus Severity);
