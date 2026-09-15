namespace InternetMonitor.Configuration;

/// <summary>
/// A named continuous-ping target for the multi-target ping comparison feature. Each enabled
/// target gets its own 1-second-cadence <see cref="Network.Probes.PingLatencyProbe"/> instance and
/// its own history key (see DiagnosticsCoordinator.PingProbeId), so it automatically becomes its
/// own double-click-able history chart on the Diagnostics screen - no per-target UI plumbing
/// needed beyond adding it to this list.
/// </summary>
public sealed class PingTargetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
