using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.History;

/// <summary>
/// Maps a step onto the history series it records into.
///
/// Four of these names are v1's probe ids rather than v2's step keys, and that is deliberate: the
/// files on disk are v1's files, so an upgrading user opens the History page and sees the weeks of
/// latency they already had instead of an empty chart. The measurement behind each of those four
/// is the same quantity measured the same way, so the continuity is real and not just a filename
/// trick.
///
/// Steps 1 and 2 are the exception. v1 recorded one combined "network" series for adapter, link
/// and address together; v2 splits that into two separate questions, so only the adapter inherits
/// the old series and the link starts fresh. Pretending v1's combined 0/1 was a link measurement
/// would put a line on the chart that means something different before and after the upgrade,
/// which is worse than a short history.
/// </summary>
/// <summary>
/// One entry in the History page's series picker: a series key (as stored in
/// <see cref="MetricStore"/> and used by <see cref="SeriesNames.For"/>), the localization key for
/// its chip label, its unit, and whether it's a state series (see <see cref="SeriesNames.IsStateSeries"/>).
/// Exists because <see cref="SeriesNames.Chartable"/> covers more than the seven pipeline steps -
/// the Speed Test's download/upload results are recorded the same way but have no <see cref="StepId"/>
/// of their own, since they don't run as part of the step pipeline's cycle.
/// </summary>
/// <param name="MetadataChangeKey">
/// When set, the chart marks every point whose key (derived from its <see cref="MetricPoint.Metadata"/>)
/// differs from the one before - for the Wi-Fi signal series the access point, so a roam shows.
/// </param>
/// <param name="LatencyBands">
/// Whether the chart shades the good/warning/bad latency bands under it. Only for round trips: a
/// jitter of 150 ms is awful, while the same number as a latency is merely "warning".
/// </param>
public sealed record ChartableSeries(string Id, string NameKey, string Unit, bool IsState, Func<string?, string?>? MetadataChangeKey = null, bool LatencyBands = false);

public static class SeriesNames
{
    public static string? For(StepId step) => step switch
    {
        StepId.Adapter => "network",
        StepId.Link => "link",
        StepId.Gateway => "gateway",
        StepId.PublicIp => "internet",
        StepId.Dns => "dns",
        StepId.Target => "https",

        // An address is not a number worth charting. v1 reached the same conclusion and showed a
        // change log instead, which is the right answer and is what the History page does too.
        StepId.IpAddress => null,
        _ => null,
    };

    /// <summary>The unit a series is measured in, for axis labels and the hover card.</summary>
    public static string UnitFor(StepId step) => step switch
    {
        StepId.Adapter or StepId.Link => string.Empty,
        _ => "ms",
    };

    /// <summary>
    /// True for series whose value is a state rather than a magnitude. They are drawn as a
    /// connected/disconnected timeline rather than a line with a y-axis, because the midpoint
    /// between 0 and 1 means nothing.
    /// </summary>
    public static bool IsStateSeries(StepId step) => step is StepId.Adapter or StepId.Link;

    /// <summary>
    /// Every series the History page can show, in display order. The six pipeline steps are built
    /// from the step-keyed members above (so they stay in sync automatically); the two Speed Test
    /// series are listed directly, since they have no <see cref="StepId"/> to derive from.
    /// </summary>
    /// <summary>Wi-Fi signal quality in percent, with the access point's BSSID as each point's metadata.</summary>
    public const string WifiSignal = "wifi-signal";

    /// <summary>This computer's own traffic per cycle, in Mbit/s - see <see cref="ThroughputMeter"/>.</summary>
    public const string TrafficDown = "traffic-down";

    public const string TrafficUp = "traffic-up";

    /// <summary>Share of a ten-echo burst lost, in percent - see <see cref="Monitors.LineQualitySampler"/>.</summary>
    public const string PacketLoss = "packet-loss";

    /// <summary>How much the round trip varied within the same burst, in ms.</summary>
    public const string Jitter = "jitter";

    /// <summary>1 while Windows treated the connection as metered (mobile data, a hotspot), else 0. Not charted.</summary>
    public const string Metered = "metered";

    public const string SpeedTestDownload = "speedtest-download";

    public const string SpeedTestUpload = "speedtest-upload";

    /// <summary>The speed test's idle round trip, as shown on its Ping tile.</summary>
    public const string SpeedTestPing = "speedtest-ping";

    /// <summary>How many milliseconds the round trip grew while the speed test filled the line.</summary>
    public const string SpeedTestBufferbloat = "speedtest-bufferbloat";

    public static IReadOnlyList<ChartableSeries> Chartable { get; } =
    [
        new(For(StepId.Adapter)!, "step.adapter.name", UnitFor(StepId.Adapter), IsStateSeries(StepId.Adapter)),
        new(For(StepId.Link)!, "step.link.name", UnitFor(StepId.Link), IsStateSeries(StepId.Link)),
        new(WifiSignal, "series.wifiSignal", "%", false, WifiHistory.AccessPointOf),
        new(For(StepId.Gateway)!, "step.gateway.name", UnitFor(StepId.Gateway), IsStateSeries(StepId.Gateway), LatencyBands: true),
        new(For(StepId.PublicIp)!, "step.publicIp.name", UnitFor(StepId.PublicIp), IsStateSeries(StepId.PublicIp), LatencyBands: true),
        new(PacketLoss, "series.packetLoss", "%", false),
        new(Jitter, "series.jitter", "ms", false),
        new(For(StepId.Dns)!, "step.dns.name", UnitFor(StepId.Dns), IsStateSeries(StepId.Dns), LatencyBands: true),
        new(For(StepId.Target)!, "step.target.name", UnitFor(StepId.Target), IsStateSeries(StepId.Target), LatencyBands: true),
        new(TrafficDown, "series.trafficDown", "Mbps", false),
        new(TrafficUp, "series.trafficUp", "Mbps", false),
        new(SpeedTestDownload, "speedtest.download.name", "Mbps", false),
        new(SpeedTestUpload, "speedtest.upload.name", "Mbps", false),
        new(SpeedTestPing, "series.speedTestPing", "ms", false, LatencyBands: true),
        new(SpeedTestBufferbloat, "series.speedTestBufferbloat", "ms", false),
    ];
}
