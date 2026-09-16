using System.Diagnostics;
using System.Text.Json;

namespace InternetMonitor.Network.Probes;

/// <summary>
/// A single Wi-Fi signal history point's SSID/BSSID/link speed, as they were at that exact
/// moment - not just whatever's current now. Encoded/decoded as a JSON blob wrapped in
/// <see cref="Uri.EscapeDataString(string)"/> (same escaping <see cref="Diagnosis.ProbeHistory"/>
/// already uses for probe-id-to-filename safety) so an SSID containing a comma or newline can
/// never corrupt the single-line CSV format that opaque metadata string is stored as.
/// </summary>
public sealed record WifiHistoryPointMetadata(string Ssid, string Bssid, int LinkSpeedMbps, string PhyType)
{
    public string Encode() => Uri.EscapeDataString(JsonSerializer.Serialize(this));

    public static WifiHistoryPointMetadata? Decode(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WifiHistoryPointMetadata>(Uri.UnescapeDataString(encoded));
        }
        catch (JsonException)
        {
            // A corrupted/unexpected metadata field shouldn't break the whole history chart -
            // just show that point without its extra context, same as a point with none at all.
            return null;
        }
    }
}

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
