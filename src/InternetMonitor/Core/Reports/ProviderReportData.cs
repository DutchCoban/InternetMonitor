using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;

namespace InternetMonitor.Core.Reports;

/// <summary>Where an outage most likely originated - the question a provider will ask first.</summary>
public enum OutageSide
{
    /// <summary>The adapter, the cable or Wi-Fi link, or this computer's own address.</summary>
    ThisComputer,

    /// <summary>The router: the home network, which may or may not be the provider's equipment.</summary>
    HomeNetwork,

    /// <summary>Beyond the router: the internet connection itself, the provider's DNS, the wider internet.</summary>
    Provider,

    /// <summary>
    /// Started while the computer was on a metered connection - mobile data or a phone's hotspot -
    /// so whatever went wrong, it was not on the line this report is about.
    /// </summary>
    MeteredConnection,

    /// <summary>A login page, or a cause that could not be determined.</summary>
    Other,
}

public sealed record OutageEntry(DateTimeOffset StartUtc, TimeSpan Duration, RootCause Cause, OutageSide Side, bool Ongoing);

public sealed record OutageGroup(RootCause Cause, OutageSide Side, int Count, TimeSpan Total, TimeSpan Longest);

public sealed record DayLatency(DateOnly Day, double AverageMs, double P95Ms, int Samples);

/// <summary>One day's packet loss and jitter, averaged over that day's one-minute bursts (see <see cref="Monitors.LineQualitySampler"/>).</summary>
/// <param name="BurstsWithLoss">How many bursts lost at least one echo - a steady 1% and a few bad minutes average the same.</param>
public sealed record DayQuality(DateOnly Day, double LossPercent, double? JitterMs, int Bursts, int BurstsWithLoss);

public sealed record SpeedTestEntry(DateTimeOffset AtUtc, double DownloadMbps, double UploadMbps, double? PingMs, double? BufferbloatMs);

/// <summary>Everything the provider report says, computed - see <see cref="Compute"/>.</summary>
/// <param name="Monitored">How much of the period the app was actually measuring.</param>
/// <param name="Availability">
/// Share of the monitored time without an outage on the provider's side; null when nothing was
/// measured. Outages in this computer or the home network are listed, but not held against the
/// connection - a report a provider can dismiss over one flat laptop battery helps nobody.
/// </param>
public sealed record ProviderReportData(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    TimeSpan Monitored,
    double? Availability,
    IReadOnlyList<OutageEntry> Outages,
    IReadOnlyList<OutageGroup> OutagesByCause,
    IReadOnlyList<DayLatency> LatencyByDay,
    IReadOnlyList<SpeedTestEntry> SpeedTests)
{
    /// <summary>
    /// Monitored time spent on a metered connection (mobile data, a phone's hotspot). It is left out
    /// of the availability figure, like outages that started in it: the report is about the line.
    /// </summary>
    public TimeSpan MeteredTime { get; init; }

    /// <summary>
    /// The share of IPv6 checks in the period that failed while IPv4 was working; null when IPv6
    /// was never on offer. Not an outage - IPv4 carried on - but a fault the provider should fix.
    /// </summary>
    public double? Ipv6FailureShare { get; init; }

    /// <summary>Packet loss and jitter per day; empty when the period predates the series.</summary>
    public IReadOnlyList<DayQuality> QualityByDay { get; init; } = [];

    /// <summary>Average packet loss over every burst in the period, or null without any.</summary>
    public double? AverageLossPercent
    {
        get
        {
            int bursts = QualityByDay.Sum(d => d.Bursts);
            return bursts > 0 ? QualityByDay.Sum(d => d.LossPercent * d.Bursts) / bursts : null;
        }
    }

    public TimeSpan Period => ToUtc - FromUtc;

    public double Coverage => Period > TimeSpan.Zero ? Math.Clamp(Monitored / Period, 0, 1) : 0;

    public TimeSpan TotalDowntime => Outages.Aggregate(TimeSpan.Zero, (sum, o) => sum + o.Duration);

    public IEnumerable<OutageEntry> ProviderOutages => Outages.Where(o => o.Side == OutageSide.Provider);

    /// <summary>
    /// Builds the report's numbers from the stores. Pure apart from reading <paramref name="history"/>,
    /// so it can be tested against synthetic data.
    ///
    /// Two choices worth knowing when reading a report:
    ///  - "Monitored" comes from the adapter series, which records every cycle the app runs. A gap
    ///    much longer than its usual spacing means the app was not running (computer off, app
    ///    closed); that time is left out rather than counted as either up or down. Availability is
    ///    therefore "of the time we were watching", and the report says how much that was.
    ///  - An outage is an incident with a connectivity cause. Packet-level warnings (network
    ///    health) and filtered pings are not outages: nothing stopped working.
    /// </summary>
    public static ProviderReportData Compute(
        IReadOnlyList<Incident> incidents,
        Func<string, IReadOnlyList<MetricPoint>> history,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        TimeZoneInfo timeZone)
    {
        TimeSpan monitored = MonitoredTime(InPeriod(history(SeriesNames.For(Steps.StepId.Adapter)!), fromUtc, toUtc));
        List<(DateTimeOffset From, DateTimeOffset To)> metered = MeteredStretches(InPeriod(history(SeriesNames.Metered), fromUtc, toUtc));
        TimeSpan meteredTime = metered.Aggregate(TimeSpan.Zero, (sum, s) => sum + (s.To - s.From));
        bool OnMetered(DateTimeOffset at) => metered.Any(s => at >= s.From && at <= s.To);

        List<OutageEntry> outages =
        [
            .. incidents
                .Where(i => i.ConditionKey is null && IsOutage(i.Cause))
                .Where(i => i.StartUtc < toUtc && (i.EndUtc ?? toUtc) > fromUtc)
                .Select(i =>
                {
                    DateTimeOffset start = i.StartUtc < fromUtc ? fromUtc : i.StartUtc;
                    DateTimeOffset end = i.EndUtc is { } e && e < toUtc ? e : toUtc;
                    OutageSide side = OnMetered(i.StartUtc) ? OutageSide.MeteredConnection : SideOf(i.Cause);
                    return new OutageEntry(i.StartUtc, end - start, i.Cause, side, i.EndUtc is null);
                })
                .OrderBy(o => o.StartUtc),
        ];

        List<OutageGroup> groups =
        [
            .. outages
                .GroupBy(o => (o.Cause, o.Side))
                .Select(g => new OutageGroup(
                    g.Key.Cause,
                    g.Key.Side,
                    g.Count(),
                    g.Aggregate(TimeSpan.Zero, (sum, o) => sum + o.Duration),
                    g.Max(o => o.Duration)))
                .OrderBy(g => g.Side)
                .ThenByDescending(g => g.Total),
        ];

        TimeSpan downtime = outages.Where(o => o.Side == OutageSide.Provider).Aggregate(TimeSpan.Zero, (sum, o) => sum + o.Duration);
        TimeSpan onLine = monitored - meteredTime;
        double? availability = onLine > TimeSpan.Zero
            ? Math.Clamp(1 - (downtime / onLine), 0, 1)
            : null;

        return new ProviderReportData(
            fromUtc,
            toUtc,
            monitored,
            availability,
            outages,
            groups,
            ComputeLatencyByDay(InPeriod(history(SeriesNames.For(Steps.StepId.PublicIp)!), fromUtc, toUtc), timeZone),
            CollectSpeedTests(history, fromUtc, toUtc))
        {
            MeteredTime = meteredTime,
            Ipv6FailureShare = InPeriod(history(SeriesNames.Ipv6), fromUtc, toUtc) is { Count: > 0 } ipv6
                ? ipv6.Count(p => p.Value < 0.5) / (double)ipv6.Count
                : null,
            QualityByDay = ComputeQualityByDay(
                InPeriod(history(SeriesNames.PacketLoss), fromUtc, toUtc),
                InPeriod(history(SeriesNames.Jitter), fromUtc, toUtc),
                timeZone),
        };
    }

    /// <summary>
    /// The stretches during which the metered series said 1. A stretch ends at the first 0 or at a
    /// gap the app was not running for, the same way <see cref="MonitoredTime"/> treats gaps.
    /// </summary>
    public static List<(DateTimeOffset From, DateTimeOffset To)> MeteredStretches(IReadOnlyList<MetricPoint> points)
    {
        var stretches = new List<(DateTimeOffset From, DateTimeOffset To)>();
        DateTimeOffset? start = null;
        DateTimeOffset last = default;
        foreach (MetricPoint point in points)
        {
            bool gap = start is not null && point.Timestamp - last > MaxSampleGap;
            if (start is { } open && (point.Value < 0.5 || gap))
            {
                stretches.Add((open, gap ? last : point.Timestamp));
                start = null;
            }

            if (point.Value >= 0.5 && start is null)
            {
                start = point.Timestamp;
            }

            last = point.Timestamp;
        }

        if (start is { } stillOpen && last > stillOpen)
        {
            stretches.Add((stillOpen, last));
        }

        return stretches;
    }

    /// <summary>Longer than this between two samples means the app was not running in between.</summary>
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromMinutes(5);

    private static List<DayQuality> ComputeQualityByDay(IReadOnlyList<MetricPoint> loss, IReadOnlyList<MetricPoint> jitter, TimeZoneInfo timeZone)
    {
        DateOnly DayOf(MetricPoint p) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.Timestamp, timeZone).DateTime);
        Dictionary<DateOnly, double> jitterByDay = jitter.GroupBy(DayOf).ToDictionary(g => g.Key, g => g.Average(p => p.Value));

        return
        [
            .. loss
                .GroupBy(DayOf)
                .Select(g => new DayQuality(
                    g.Key,
                    g.Average(p => p.Value),
                    jitterByDay.TryGetValue(g.Key, out double j) ? j : null,
                    g.Count(),
                    g.Count(p => p.Value > 0)))
                .OrderBy(d => d.Day),
        ];
    }

    public static OutageSide SideOf(RootCause cause) => cause switch
    {
        RootCause.Adapter or RootCause.Link or RootCause.IpConfiguration => OutageSide.ThisComputer,
        RootCause.Gateway => OutageSide.HomeNetwork,
        RootCause.Internet or RootCause.Dns or RootCause.Target or RootCause.Ipv6Broken => OutageSide.Provider,
        _ => OutageSide.Other,
    };

    private static bool IsOutage(RootCause cause) =>
        cause is not (RootCause.Healthy or RootCause.FirewallSuspected or RootCause.NetworkHealth or RootCause.Ipv6Broken);

    private static List<MetricPoint> InPeriod(IReadOnlyList<MetricPoint> points, DateTimeOffset from, DateTimeOffset to) =>
        [.. points.Where(p => p.Timestamp >= from && p.Timestamp <= to)];

    /// <summary>
    /// Sums the spacing between consecutive samples, leaving out any gap longer than a few times
    /// the usual spacing - those are stretches the app was not running.
    /// </summary>
    public static TimeSpan MonitoredTime(IReadOnlyList<MetricPoint> points)
    {
        if (points.Count < 2)
        {
            return TimeSpan.Zero;
        }

        var deltas = new List<TimeSpan>(points.Count - 1);
        for (int i = 1; i < points.Count; i++)
        {
            deltas.Add(points[i].Timestamp - points[i - 1].Timestamp);
        }

        List<TimeSpan> sorted = [.. deltas.OrderBy(d => d)];
        TimeSpan median = sorted[sorted.Count / 2];
        TimeSpan threshold = TimeSpan.FromTicks(Math.Max(median.Ticks * 3, TimeSpan.FromSeconds(90).Ticks));

        return deltas.Where(d => d <= threshold).Aggregate(TimeSpan.Zero, (sum, d) => sum + d);
    }

    private static List<DayLatency> ComputeLatencyByDay(IReadOnlyList<MetricPoint> points, TimeZoneInfo timeZone) =>
    [
        .. points
            .GroupBy(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.Timestamp, timeZone).DateTime))
            .Select(g =>
            {
                List<double> values = [.. g.Select(p => p.Value).OrderBy(v => v)];
                int p95Index = Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * 0.95) - 1);
                return new DayLatency(g.Key, values.Average(), values[Math.Max(0, p95Index)], values.Count);
            })
            .OrderBy(d => d.Day),
    ];

    /// <summary>
    /// One row per download sample, with the upload, ping and loaded-latency samples of the same
    /// run matched to it by time - a run records them all at the same instant.
    /// </summary>
    private static List<SpeedTestEntry> CollectSpeedTests(Func<string, IReadOnlyList<MetricPoint>> history, DateTimeOffset from, DateTimeOffset to)
    {
        double? Near(string series, DateTimeOffset at) =>
            history(series).Where(p => (p.Timestamp - at).Duration() <= TimeSpan.FromMinutes(1)).Select(p => (double?)p.Value).FirstOrDefault();

        return
        [
            .. InPeriod(history(SeriesNames.SpeedTestDownload), from, to)
                .Select(d => new SpeedTestEntry(
                    d.Timestamp,
                    d.Value,
                    Near(SeriesNames.SpeedTestUpload, d.Timestamp) ?? 0,
                    Near(SeriesNames.SpeedTestPing, d.Timestamp),
                    Near(SeriesNames.SpeedTestBufferbloat, d.Timestamp))),
        ];
    }
}
