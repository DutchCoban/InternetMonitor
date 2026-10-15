using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Reports;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Incidents;

/// <summary>Where an incident started, for filtering - the provider report's sides plus "everything else".</summary>
public enum IncidentSideFilter
{
    All,
    Provider,
    HomeNetwork,
    ThisComputer,

    /// <summary>A login page, an unknown cause, or a packet-level network-health warning.</summary>
    Other,
}

/// <summary>The Incidents page's time filter.</summary>
public enum IncidentPeriodFilter
{
    All,

    /// <summary>Only incidents that are still open.</summary>
    Active,
    Day,
    Week,
    Month,
}

/// <summary>Everything the Incidents page filters on; the defaults filter nothing.</summary>
public sealed record IncidentFilterCriteria(
    string Search = "",
    IncidentSideFilter Side = IncidentSideFilter.All,
    IncidentPeriodFilter Period = IncidentPeriodFilter.All)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Search) && Side == IncidentSideFilter.All && Period == IncidentPeriodFilter.All;
}

/// <summary>
/// Narrows the incident list on the Incidents page. With months of history the list runs into the
/// hundreds, and the questions people bring to it are specific: "what happened last night", "how
/// often was it the provider", "that DNS thing from last week".
///
/// The search matches every word typed, anywhere in an incident's id, headline, cause (in the
/// display language and in English, since incidents are recorded in English), explanation and
/// timeline - so "dns router" finds the DNS incident whose timeline mentions the router.
/// </summary>
public static class IncidentFilter
{
    public static IReadOnlyList<Incident> Apply(IReadOnlyList<Incident> incidents, IncidentFilterCriteria criteria, DateTimeOffset nowUtc)
    {
        if (criteria.IsEmpty)
        {
            return incidents;
        }

        string[] words = criteria.Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. incidents.Where(i => MatchesSide(i, criteria.Side) && MatchesPeriod(i, criteria.Period, nowUtc) && MatchesWords(i, words))];
    }

    public static IncidentSideFilter SideOf(Incident incident) =>
        incident.ConditionKey is not null
            ? IncidentSideFilter.Other
            : ProviderReportData.SideOf(incident.Cause) switch
            {
                OutageSide.Provider => IncidentSideFilter.Provider,
                OutageSide.HomeNetwork => IncidentSideFilter.HomeNetwork,
                OutageSide.ThisComputer => IncidentSideFilter.ThisComputer,
                _ => IncidentSideFilter.Other,
            };

    private static bool MatchesSide(Incident incident, IncidentSideFilter side) =>
        side == IncidentSideFilter.All || SideOf(incident) == side;

    /// <summary>An incident belongs to a period when any part of it falls inside - one that started before and is still going counts.</summary>
    private static bool MatchesPeriod(Incident incident, IncidentPeriodFilter period, DateTimeOffset now)
    {
        TimeSpan? window = period switch
        {
            IncidentPeriodFilter.Day => TimeSpan.FromDays(1),
            IncidentPeriodFilter.Week => TimeSpan.FromDays(7),
            IncidentPeriodFilter.Month => TimeSpan.FromDays(30),
            _ => null,
        };

        return period switch
        {
            IncidentPeriodFilter.All => true,
            IncidentPeriodFilter.Active => incident.Status == IncidentStatus.Active,
            _ => (incident.EndUtc ?? now) >= now - window!.Value,
        };
    }

    private static bool MatchesWords(Incident incident, string[] words)
    {
        if (words.Length == 0)
        {
            return true;
        }

        string haystack = SearchText(incident);
        return words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private static string SearchText(Incident incident)
    {
        string causeKey = $"cause.{incident.Cause.Key()}";
        IEnumerable<string?> parts =
        [
            incident.Id,
            incident.Headline,
            LocalizationManager.Instance.Get(causeKey),
            LocalizationManager.GetEnglish(causeKey),
            incident.ConditionKey,
            incident.Explanation?.What,
            incident.Explanation?.LikelyCause,
            incident.Explanation?.WhatToDo,
            .. incident.Timeline.Select(t => t.Text),
        ];

        return string.Join('\n', parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}
