using System.Diagnostics;

namespace InternetMonitor.Network.Probes;

public sealed record WifiSignalProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string? Ssid,
    string? Bssid,
    int? SignalQualityPercent,
    int? LinkSpeedMbps,
    string? PhyType,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => SignalQualityPercent;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string>();
        if (Ssid is not null) d["SSID"] = Ssid;
        if (Bssid is not null) d["BSSID"] = Bssid;
        if (SignalQualityPercent is not null) d["SignalQuality"] = $"{SignalQualityPercent}%";
        if (LinkSpeedMbps is not null) d["LinkSpeed"] = $"{LinkSpeedMbps} Mbps";
        if (PhyType is not null) d["PhyType"] = PhyType;
        return d;
    }
}

/// <summary>
/// On-demand-per-poll Wi-Fi signal reading (see <see cref="WifiInfo"/>) - only run by
/// <see cref="DiagnosticsCoordinator"/> when the active adapter is actually wireless, folded into
/// the main poll cycle rather than its own timer/loop since a WLAN query is cheap and the main
/// cycle's 15s cadence is plenty for a signal-quality trend.
/// </summary>
public sealed class WifiSignalProbe : IProbe
{
    public const string ProbeIdValue = "wifi-signal";
    private const int WeakSignalThresholdPercent = 30;

    public string Id => ProbeIdValue;
    public string Category => "Network";

    public Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        WifiSnapshot? snapshot = WifiInfo.TryGetCurrentConnection();
        sw.Stop();

        if (snapshot is null)
        {
            return Task.FromResult<IProbeResult>(new WifiSignalProbeResult(
                Id, ProbeStatus.Unknown, "Wi-Fi signal unavailable", sw.Elapsed, DateTimeOffset.UtcNow,
                null, null, null, null, null, "No wireless connection reported by the OS"));
        }

        ProbeStatus status = snapshot.SignalQualityPercent < WeakSignalThresholdPercent ? ProbeStatus.Warning : ProbeStatus.Ok;
        string summary = $"{snapshot.Ssid} ({snapshot.SignalQualityPercent}%, {snapshot.LinkSpeedMbps} Mbps)";

        return Task.FromResult<IProbeResult>(new WifiSignalProbeResult(
            Id, status, summary, sw.Elapsed, DateTimeOffset.UtcNow,
            snapshot.Ssid, snapshot.Bssid, snapshot.SignalQualityPercent, snapshot.LinkSpeedMbps, snapshot.PhyType));
    }
}
