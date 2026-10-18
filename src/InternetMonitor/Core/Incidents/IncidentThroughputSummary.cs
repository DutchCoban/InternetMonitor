using InternetMonitor.Core.History;

namespace InternetMonitor.Core.Incidents;

/// <summary>
/// Builds <see cref="IncidentThroughput"/> from the traffic history: pure over a series accessor,
/// so the host captures it when an incident opens and closes, and every reader can fill in what
/// a stored record lacks (an incident from before this existed, or one whose after-window the app
/// did not live to see) as long as the history still reaches back that far.
/// </summary>
internal static class IncidentThroughputSummary
{
    public static readonly TimeSpan BeforeWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AfterWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BaselineWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The traffic between <paramref name="fromUtc"/> (inclusive) and <paramref name="toUtc"/>
    /// (exclusive), or null when the down series has no point in that range. The up series is read
    /// on its own, so a missing point in one never misaligns the other.
    /// </summary>
    public static ThroughputWindow? Window(Func<string, IReadOnlyList<MetricPoint>> history, DateTimeOffset fromUtc, DateTimeOffset toUtc, double? capacityDown, double? capacityUp)
    {
        if (toUtc <= fromUtc)
        {
            return null;
        }

        (double avgDown, double peakDown, DateTimeOffset peakAt, int samples) = Summarise(history(SeriesNames.TrafficDown), fromUtc, toUtc);
        if (samples == 0)
        {
            return null;
        }

        (double avgUp, double peakUp, _, _) = Summarise(history(SeriesNames.TrafficUp), fromUtc, toUtc);

        return new ThroughputWindow(
            fromUtc,
            toUtc,
            samples,
            avgDown,
            avgUp,
            peakDown,
            peakUp,
            peakAt,
            Utilization(avgDown, avgUp, capacityDown, capacityUp),
            Utilization(peakDown, peakUp, capacityDown, capacityUp));
    }

    /// <summary>The last speed test at or before <paramref name="at"/>, per direction; a result under 1 Mbit/s is a failed test and counts for nothing.</summary>
    public static (double? Down, double? Up) SpeedTestCapacityBefore(Func<string, IReadOnlyList<MetricPoint>> history, DateTimeOffset at) =>
        (LastAtOrBefore(history(SeriesNames.SpeedTestDownload), at), LastAtOrBefore(history(SeriesNames.SpeedTestUpload), at));

    /// <summary>
    /// The four windows as far as time allows: the baseline and the before-window always, the
    /// incident itself up to its end or to now, and the after-window only once it has fully
    /// elapsed - plus one recording interval, so its last point has been stored.
    /// </summary>
    public static IncidentThroughput Compute(
        Incident incident,
        Func<string, IReadOnlyList<MetricPoint>> history,
        double? capacityDown,
        double? capacityUp,
        ThroughputCapacitySource source,
        DateTimeOffset nowUtc,
        TimeSpan sampleInterval)
    {
        DateTimeOffset start = incident.StartUtc;
        bool afterDue = AfterWindowDue(incident, nowUtc, sampleInterval);

        return new IncidentThroughput(
            Window(history, start - BeforeWindow - BaselineWindow, start - BeforeWindow, capacityDown, capacityUp),
            Window(history, start - BeforeWindow, start, capacityDown, capacityUp),
            Window(history, start, incident.EndUtc ?? nowUtc, capacityDown, capacityUp),
            afterDue ? Window(history, incident.EndUtc!.Value, incident.EndUtc.Value + AfterWindow, capacityDown, capacityUp) : null,
            capacityDown,
            capacityUp,
            source)
        {
            AfterPending = !afterDue,
        };
    }

    /// <summary>
    /// What a reader shows: the stored record with any missing window filled from history while
    /// it still reaches back that far. Null when nothing at all is known, so the section is left out
    /// rather than listing four times "no measurements".
    /// </summary>
    public static IncidentThroughput? Resolve(Incident incident, Func<string, IReadOnlyList<MetricPoint>> history, DateTimeOffset nowUtc, TimeSpan sampleInterval)
    {
        IncidentThroughput? stored = incident.Throughput;
        if (stored is { AfterPending: false } complete && complete.Baseline is not null && complete.Before is not null && complete.During is not null && complete.After is not null)
        {
            return stored;
        }

        (double? capacityDown, double? capacityUp, ThroughputCapacitySource source) = stored is { CapacitySource: not ThroughputCapacitySource.None }
            ? (stored.CapacityDownMbps, stored.CapacityUpMbps, stored.CapacitySource)
            : CapacityFromHistory(history, incident.StartUtc);

        IncidentThroughput computed = Compute(incident, history, capacityDown, capacityUp, source, nowUtc, sampleInterval);
        IncidentThroughput merged = stored is null
            ? computed
            : computed with
            {
                Baseline = stored.Baseline ?? computed.Baseline,
                Before = stored.Before ?? computed.Before,
                During = stored.During ?? computed.During,
                After = stored.After ?? computed.After,
            };

        return merged.HasAnyWindow ? merged : null;
    }

    /// <summary>True once the five minutes after the incident closed, plus one recording interval, have passed.</summary>
    public static bool AfterWindowDue(Incident incident, DateTimeOffset nowUtc, TimeSpan sampleInterval) =>
        incident.EndUtc is { } end && nowUtc >= end + AfterWindow + sampleInterval;

    /// <summary>A stored record is complete once its after-window has been captured; until then the host owes it another pass.</summary>
    public static bool NeedsCompletion(Incident incident, DateTimeOffset nowUtc, TimeSpan sampleInterval) =>
        incident.Status == IncidentStatus.Resolved
        && incident.Throughput is { After: null }
        && AfterWindowDue(incident, nowUtc, sampleInterval);

    private static (double? Down, double? Up, ThroughputCapacitySource Source) CapacityFromHistory(Func<string, IReadOnlyList<MetricPoint>> history, DateTimeOffset at)
    {
        (double? down, double? up) = SpeedTestCapacityBefore(history, at);
        return down is null && up is null ? (null, null, ThroughputCapacitySource.None) : (down, up, ThroughputCapacitySource.SpeedTest);
    }

    private static (double Average, double Peak, DateTimeOffset PeakAt, int Samples) Summarise(IReadOnlyList<MetricPoint> points, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        int first = MetricStore.LowerBound(points, fromUtc);
        int end = MetricStore.LowerBound(points, toUtc);
        if (end <= first)
        {
            return (0, 0, default, 0);
        }

        double sum = 0;
        double peak = double.NegativeInfinity;
        DateTimeOffset peakAt = points[first].Timestamp;
        for (int i = first; i < end; i++)
        {
            MetricPoint point = points[i];
            sum += point.Value;
            double pointPeak = TrafficHistory.PeakOf(point);
            if (pointPeak > peak)
            {
                peak = pointPeak;
                peakAt = point.Timestamp;
            }
        }

        return (sum / (end - first), peak, peakAt, end - first);
    }

    /// <summary>The busier direction as a share of capacity, the convention <see cref="Steps.ThroughputSample"/> uses; null without a capacity.</summary>
    private static double? Utilization(double down, double up, double? capacityDown, double? capacityUp) =>
        capacityDown is > 0 || capacityUp is > 0
            ? Math.Max(capacityDown is > 0 ? down / capacityDown.Value : 0, capacityUp is > 0 ? up / capacityUp.Value : 0)
            : null;

    private static double? LastAtOrBefore(IReadOnlyList<MetricPoint> points, DateTimeOffset at)
    {
        int index = MetricStore.UpperBound(points, at) - 1;
        return index >= 0 && points[index].Value >= 1 ? points[index].Value : null;
    }
}
