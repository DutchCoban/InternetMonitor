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

public sealed record DiagnosisResult(
    DiagnosisClassification Classification,
    string Headline,
    string Explanation,
    IReadOnlyList<string> AffectedProbeIds);
