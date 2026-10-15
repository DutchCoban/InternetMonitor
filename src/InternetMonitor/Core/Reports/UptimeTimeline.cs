using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.Reports;

public enum UptimeState
{
    /// <summary>The app was not running for most of the block - no verdict either way.</summary>
    NotMeasured,
    Ok,

    /// <summary>Working, but slow, behind a login page, or with a packet-level warning.</summary>
    Warning,

    /// <summary>An outage was recorded in the block.</summary>
    Outage,
}

/// <param name="IncidentId">The incident that coloured the block, longest first, for clicking through to it.</param>
public sealed record UptimeBlock(DateTimeOffset StartUtc, DateTimeOffset EndUtc, UptimeState State, string? IncidentId, RootCause? Cause);

/// <param name="Availability">The provider report's figure for that day; null when nothing was measured.</param>
public sealed record UptimeDay(DateOnly Day, UptimeState State, double? Availability, double Coverage, int Outages);

public sealed record UptimeOverview(IReadOnlyList<UptimeBlock> Blocks, IReadOnlyList<UptimeDay> Days);

/// <summary>
/// The Status page's glance at the recent past: the last 24 hours in quarter-hour blocks, and the
/// last seven days with their availability.
///
/// The numbers come from <see cref="ProviderReportData"/> rather than a second set of rules, so a
/// day shown as 99.2% here is 99.2% in the report too. A block counts as measured when the app ran
/// for at least half of it; a gap is grey, never green - a computer that was off saw nothing.
/// </summary>
public static class UptimeTimeline
{
    public static readonly TimeSpan BlockLength = TimeSpan.FromMinutes(15);
    public const int BlockCount = 96;
    public const int DayCount = 7;

    /// <summary>Below this availability a day counts as having had a real outage on the provider's side.</summary>
    private const double GoodDay = 0.999;

    public static UptimeOverview Compute(
        IReadOnlyList<Incident> incidents,
        Func<string, IReadOnlyList<MetricPoint>> history,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        double latencyWarningMs)
    {
        return new UptimeOverview(
            ComputeBlocks(incidents, history, nowUtc, latencyWarningMs),
            ComputeDays(incidents, history, nowUtc, timeZone));
    }

    private static List<UptimeBlock> ComputeBlocks(
        IReadOnlyList<Incident> incidents,
        Func<string, IReadOnlyList<MetricPoint>> history,
        DateTimeOffset nowUtc,
        double latencyWarningMs)
    {
        // Blocks end on a quarter-hour boundary, the last one being the quarter that is under way.
        long blockTicks = BlockLength.Ticks;
        var end = new DateTimeOffset((nowUtc.UtcTicks / blockTicks + 1) * blockTicks, TimeSpan.Zero);
        DateTimeOffset start = end - (BlockLength * BlockCount);

        IReadOnlyList<MetricPoint> adapter = history(SeriesNames.For(StepId.Adapter)!);
        IReadOnlyList<MetricPoint> latency = history(SeriesNames.For(StepId.PublicIp)!);
        List<Incident> relevant = [.. incidents.Where(i => i.StartUtc < end && (i.EndUtc ?? nowUtc) > start)];

        var blocks = new List<UptimeBlock>(BlockCount);
        for (int b = 0; b < BlockCount; b++)
        {
            DateTimeOffset from = start + (BlockLength * b);
            DateTimeOffset to = from + BlockLength;
            DateTimeOffset until = to < nowUtc ? to : nowUtc;

            if (from >= nowUtc)
            {
                blocks.Add(new UptimeBlock(from, to, UptimeState.NotMeasured, null, null));
                continue;
            }

            (Incident Incident, TimeSpan Overlap)? longest = relevant
                .Select(i => (Incident: i, Overlap: Overlap(i, from, until, nowUtc)))
                .Where(x => x.Overlap > TimeSpan.Zero)
                .OrderByDescending(x => StateOf(x.Incident) == UptimeState.Outage)
                .ThenByDescending(x => x.Overlap)
                .Select(x => ((Incident, TimeSpan)?)x)
                .FirstOrDefault();

            if (longest is { } hit)
            {
                blocks.Add(new UptimeBlock(from, to, StateOf(hit.Incident), hit.Incident.Id, hit.Incident.Cause));
                continue;
            }

            List<MetricPoint> samples = [.. adapter.Where(p => p.Timestamp >= from && p.Timestamp < until)];
            TimeSpan measured = ProviderReportData.MonitoredTime(samples);
            if (measured < (until - from) / 2)
            {
                blocks.Add(new UptimeBlock(from, to, UptimeState.NotMeasured, null, null));
                continue;
            }

            double[] round = [.. latency.Where(p => p.Timestamp >= from && p.Timestamp < until).Select(p => p.Value).Order()];
            bool slow = round.Length > 0 && round[Math.Clamp((int)Math.Ceiling(round.Length * 0.95) - 1, 0, round.Length - 1)] >= latencyWarningMs;
            blocks.Add(new UptimeBlock(from, to, slow ? UptimeState.Warning : UptimeState.Ok, null, null));
        }

        return blocks;
    }

    private static List<UptimeDay> ComputeDays(
        IReadOnlyList<Incident> incidents,
        Func<string, IReadOnlyList<MetricPoint>> history,
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone)
    {
        DateOnly today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);
        var days = new List<UptimeDay>(DayCount);
        for (int d = DayCount - 1; d >= 0; d--)
        {
            DateOnly day = today.AddDays(-d);
            DateTimeOffset from = LocalMidnight(day, timeZone);
            DateTimeOffset to = LocalMidnight(day.AddDays(1), timeZone);
            if (to > nowUtc)
            {
                to = nowUtc;
            }

            ProviderReportData data = ProviderReportData.Compute(incidents, history, from, to, timeZone);
            UptimeState state = data.Availability is not { } availability || data.Coverage < 0.05
                ? UptimeState.NotMeasured
                : availability < GoodDay
                    ? UptimeState.Outage
                    : data.Outages.Count > 0 ? UptimeState.Warning : UptimeState.Ok;

            days.Add(new UptimeDay(day, state, data.Availability, data.Coverage, data.Outages.Count));
        }

        return days;
    }

    /// <summary>
    /// An outage colours a block red; a login page, a packet-level warning or a degraded cause
    /// colours it orange, the same split the status hero makes.
    /// </summary>
    private static UptimeState StateOf(Incident incident) =>
        incident.ConditionKey is null && ProviderReportData.IsOutage(incident.Cause) && incident.Cause != RootCause.CaptivePortal
            ? UptimeState.Outage
            : UptimeState.Warning;

    private static TimeSpan Overlap(Incident incident, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        DateTimeOffset start = incident.StartUtc > from ? incident.StartUtc : from;
        DateTimeOffset end = (incident.EndUtc ?? now) < to ? incident.EndUtc ?? now : to;
        return end - start;
    }

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo timeZone)
    {
        DateTime local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }
}
