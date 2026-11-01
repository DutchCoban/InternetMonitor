using System.Globalization;

namespace InternetMonitor.Core.Incidents;

/// <summary>One recurring thing about an incident: a localisation key and its arguments, resolved per reader (the panel, the text export, the English AI export).</summary>
public sealed record IncidentPattern(string Key, object[] Args);

/// <summary>
/// Finds what an incident has in common with earlier ones: the same cause around the same hour on
/// several days, a start right after a network change like earlier ones, or a route that stops at
/// the same hop again. Nothing here is stored or changes what the app does - it is an annotation,
/// computed when an incident is read, so that a third occurrence makes the first two part of the
/// pattern too. The rules are deliberately plain counts with the numbers printed in the texts.
/// </summary>
public static class IncidentPatterns
{
    public const string SameHour = "pattern.sameHour";
    public const string AfterChange = "pattern.afterChange";
    public const string SameHop = "pattern.sameHop";

    public static readonly TimeSpan LookBack = TimeSpan.FromDays(30);

    /// <summary>The same cause around the same hour on at least this many different local days.</summary>
    public const int MinDays = 3;

    /// <summary>"Around the same hour": this many hours either side, round the clock.</summary>
    public const int HourTolerance = 1;

    /// <summary>A start this soon after a network change counts as "after" it.</summary>
    public static readonly TimeSpan AfterChangeWindow = TimeSpan.FromMinutes(5);

    /// <summary>How many incidents (this one included) make the after-change and same-hop rules fire.</summary>
    public const int MinRepeats = 3;

    /// <summary>The patterns this incident is part of, judged against every incident of the last 30 days.</summary>
    public static IReadOnlyList<IncidentPattern> For(Incident incident, IReadOnlyList<Incident> all, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        List<Incident> kin = [.. all.Where(i => i.StartUtc >= nowUtc - LookBack && SameKind(i, incident))];
        if (!kin.Any(i => i.Id == incident.Id))
        {
            kin.Add(incident);
        }

        var patterns = new List<IncidentPattern>(3);

        int hour = LocalHour(incident, zone);
        List<Incident> sameHour = [.. kin.Where(i => HoursApart(LocalHour(i, zone), hour) <= HourTolerance)];
        int days = sameHour.Select(i => LocalDate(i, zone)).Distinct().Count();
        if (days >= MinDays)
        {
            int modalHour = sameHour.GroupBy(i => LocalHour(i, zone)).OrderByDescending(g => g.Count()).ThenBy(g => HoursApart(g.Key, hour)).First().Key;
            patterns.Add(new IncidentPattern(SameHour, [modalHour, sameHour.Count, days, LookBack.Days]));
        }

        if (SinceLastChange(incident, zone) <= AfterChangeWindow)
        {
            int others = kin.Count(i => i.Id != incident.Id && SinceLastChange(i, zone) <= AfterChangeWindow);
            if (others + 1 >= MinRepeats)
            {
                patterns.Add(new IncidentPattern(AfterChange, [(int)AfterChangeWindow.TotalMinutes, others, LookBack.Days]));
            }
        }

        if (StoppedAtHop(incident.Traceroute) is { } hop)
        {
            int count = kin.Count(i => StoppedAtHop(i.Traceroute) == hop);
            if (count >= MinRepeats)
            {
                patterns.Add(new IncidentPattern(SameHop, [hop, count, LookBack.Days]));
            }
        }

        return patterns;
    }

    /// <summary>The distinct patterns across a set of incidents - the report's "recurring patterns" list.</summary>
    public static IReadOnlyList<IncidentPattern> Across(IReadOnlyList<Incident> incidents, TimeZoneInfo zone, DateTimeOffset nowUtc) =>
    [
        .. incidents
            .SelectMany(i => For(i, incidents, zone, nowUtc).Select(p => (Kind: (i.Cause, i.ConditionKey), Pattern: p)))
            .DistinctBy(x => (x.Kind, x.Pattern.Key, string.Join("\u001f", x.Pattern.Args)))
            .Select(x => x.Pattern),
    ];

    public static IReadOnlyList<string> Describe(IReadOnlyList<IncidentPattern> patterns, Func<string, object[], string> format) =>
        [.. patterns.Select(p => format(p.Key, p.Args))];

    /// <summary>The last hop that answered when the route did not reach its target; null when it did, or when nothing answered.</summary>
    internal static string? StoppedAtHop(IncidentTraceroute? route) =>
        route is { Reached: false } ? route.Hops.LastOrDefault(h => h.Address is not null)?.Address : null;

    /// <summary>
    /// How long after the newest recorded network change the incident started, or null without one.
    /// The changes are the "HH:mm:ss  text" lines the tracker writes (local time, newest first);
    /// the clock time alone is compared with the incident's local start, so a change just before
    /// midnight still sits a minute before an incident just after it.
    /// </summary>
    // ponytail: parses the HH:mm:ss prefix the tracker writes; a typed change list only if this ever misfires.
    internal static TimeSpan? SinceLastChange(Incident incident, TimeZoneInfo zone)
    {
        string? line = incident.RecentChanges.FirstOrDefault();
        if (line is null || line.Length < 8 || !TimeOnly.TryParseExact(line.AsSpan(0, 8), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly changedAt))
        {
            return null;
        }

        TimeOnly startedAt = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(incident.StartUtc, zone).DateTime);
        TimeSpan since = startedAt - changedAt;
        return since < TimeSpan.Zero ? since + TimeSpan.FromDays(1) : since;
    }

    private static bool SameKind(Incident a, Incident b) => a.Cause == b.Cause && a.ConditionKey == b.ConditionKey;

    private static int LocalHour(Incident i, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(i.StartUtc, zone).Hour;

    private static DateOnly LocalDate(Incident i, TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(i.StartUtc, zone).DateTime);

    private static int HoursApart(int a, int b)
    {
        int d = Math.Abs(a - b);
        return Math.Min(d, 24 - d);
    }
}
