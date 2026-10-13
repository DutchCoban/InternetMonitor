using System.Globalization;
using System.Text;
using InternetMonitor.Configuration;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;

namespace InternetMonitor.Core.Export;

/// <summary>
/// Builds the "export for AI analysis" markdown document - the same incident and history data as
/// <see cref="DataExportBuilder"/>, reshaped as prose an AI can read directly rather than as
/// machine-format files it would have to parse first.
///
/// Entirely in English, unlike <see cref="Diagnosis.DiagnosticReportBuilder"/>, which is written
/// in the user's own language because the user is who reads it - this document's reader is
/// whichever AI the user pastes it into, and the opening instructions are written to put that
/// reader into a consistent, useful frame regardless of which one it turns out to be.
///
/// A pure function over data the caller already has, same as <see cref="DataExportBuilder"/>.
/// </summary>
public static class AiExportBuilder
{
    private const string Preamble =
        "You are a network-diagnostics specialist reviewing data exported from Internet Monitor, " +
        "a Windows connectivity-monitoring tool. The sections below summarize recent connectivity " +
        "incidents and historical measurements collected automatically on the user's machine. " +
        "Analyze this data for root causes, recurring patterns over time, and anything that " +
        "correlates across incidents or series. Where the evidence supports it, give specific, " +
        "actionable recommendations the user could act on; where it does not, say so rather than " +
        "speculating. Do not assume network expertise on the reader's part when phrasing conclusions.";

    public static string Build(
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> historySeries,
        AppSettings settings)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Internet Monitor - Data Export for AI Analysis");
        sb.AppendLine();
        sb.AppendLine(Preamble);
        sb.AppendLine();

        AppendSections(sb, incidents, historySeries, settings);

        return sb.ToString();
    }

    /// <summary>
    /// Same sections as <see cref="Build"/>, without the specialist preamble - for a user who
    /// wants the data itself, not an AI prompt built around it. Still English, like the rest of
    /// this export set: it is the same content as the AI export, just missing the framing around
    /// it, not a separately-localized document.
    /// </summary>
    public static string BuildPlain(
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> historySeries,
        AppSettings settings)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Internet Monitor - Data Export");
        sb.AppendLine();

        AppendSections(sb, incidents, historySeries, settings);

        return sb.ToString();
    }

    private static void AppendSections(
        StringBuilder sb,
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> historySeries,
        AppSettings settings)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTimeOffset.UtcNow:O}");
        sb.AppendLine();

        AppendIncidents(sb, incidents);
        AppendHistory(sb, historySeries);
        AppendNetworkHealth(sb, incidents, settings);
    }

    private static void AppendIncidents(StringBuilder sb, IReadOnlyList<Incident> incidents)
    {
        sb.AppendLine("## Incidents");
        sb.AppendLine();

        if (incidents.Count == 0)
        {
            sb.AppendLine("No incidents recorded.");
            sb.AppendLine();
            return;
        }

        foreach (Incident incident in incidents.OrderByDescending(i => i.StartUtc))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {incident.Headline} ({incident.Cause})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Start: {incident.StartUtc:O}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- End: {(incident.EndUtc is { } end ? end.ToString("O", CultureInfo.InvariantCulture) : "still active")}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Duration: {(incident.Duration is { } d ? FormatDuration(d) : "n/a")}");

            if (incident.Explanation is { } explanation)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- What happened: {explanation.What}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"- Likely cause: {explanation.LikelyCause}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"- What to do: {explanation.WhatToDo}");
            }

            sb.AppendLine();
        }
    }

    private static void AppendHistory(StringBuilder sb, IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> historySeries)
    {
        sb.AppendLine("## Historical measurements");
        sb.AppendLine();

        if (historySeries.Count == 0 || historySeries.All(s => s.Value.Count == 0))
        {
            sb.AppendLine("No history recorded.");
            sb.AppendLine();
            return;
        }

        foreach ((string seriesId, IReadOnlyList<MetricPoint> points) in historySeries.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"### {FriendlyName(seriesId)}");

            if (points.Count == 0)
            {
                sb.AppendLine("No data points.");
                sb.AppendLine();
                continue;
            }

            double[] values = [.. points.Select(p => p.Value).OrderBy(v => v)];
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Points: {values.Length}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Min: {values[0]:F1}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Avg: {values.Average():F1}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Max: {values[^1]:F1}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- P95: {Percentile(values, 0.95):F1}");
            sb.AppendLine();
        }
    }

    private static void AppendNetworkHealth(StringBuilder sb, IReadOnlyList<Incident> incidents, AppSettings settings)
    {
        if (!settings.NetworkHealthMonitoringEnabled)
        {
            return;
        }

        sb.AppendLine("## Network health conditions");
        sb.AppendLine();

        var grouped = incidents
            .Where(i => i.ConditionKey is { Length: > 0 })
            .GroupBy(i => i.ConditionKey!)
            .OrderByDescending(g => g.Count());

        bool any = false;
        foreach (IGrouping<string, Incident> group in grouped)
        {
            any = true;
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {group.Key}: {group.Count()} incident(s)");
        }

        if (!any)
        {
            sb.AppendLine("No network-health incidents recorded.");
        }

        sb.AppendLine();
    }

    /// <summary>Nearest-rank percentile over an already-ascending array - fine for the dataset sizes a chart series ever reaches.</summary>
    private static double Percentile(double[] sortedAscending, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * sortedAscending.Length) - 1;
        return sortedAscending[Math.Clamp(rank, 0, sortedAscending.Length - 1)];
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{duration.TotalHours.ToString("F1", CultureInfo.InvariantCulture)} h"
        : duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)} min"
            : $"{duration.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} s";

    /// <summary>English labels for the internal series ids - see <see cref="SeriesNames"/> for why those ids themselves are not self-explanatory (several are v1's probe ids, kept for on-disk continuity).</summary>
    private static string FriendlyName(string seriesId) => seriesId switch
    {
        "network" => "Network adapter",
        "link" => "Link (cable/Wi-Fi)",
        "gateway" => "Gateway",
        "internet" => "Internet (public resolvers)",
        "dns" => "DNS",
        "https" => "Target endpoints",
        _ => seriesId,
    };
}
