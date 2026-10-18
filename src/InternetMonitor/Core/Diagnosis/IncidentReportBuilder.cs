using System.Text;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Diagnosis;

/// <summary>
/// Builds the plain-text report behind a single incident's export action - the per-incident
/// sibling of <see cref="DiagnosticReportBuilder"/>, same purpose (paste into a support ticket or
/// chat) but for one recorded incident rather than the current live snapshot. Mirrors
/// <see cref="Ui.Controls.IncidentDetail"/>'s own section order and localization keys exactly, so
/// the exported text reads the same as the screen it was exported from.
///
/// In the user's own language, unlike <see cref="Export.AiExportBuilder"/> - the reader here is
/// the user themselves, same reasoning as <see cref="DiagnosticReportBuilder"/>. Historical step
/// text inside an older incident may still be in whatever language was active when it happened;
/// this export shows whatever the incident actually contains, the same way the detail panel
/// already does, rather than trying to re-translate it after the fact.
/// </summary>
public static class IncidentReportBuilder
{
    /// <param name="throughput">This computer's traffic around the incident, resolved by the caller (which has the history); null leaves the section out.</param>
    public static string Build(Incident incident, IncidentThroughput? throughput = null)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var sb = new StringBuilder();

        sb.AppendLine(incident.Headline);
        sb.AppendLine(string.Join(
            "   ·   ",
            incident.Id,
            incident.StartUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
            incident.Status == IncidentStatus.Active ? loc.Get("incidents.active") : IncidentTracker.FormatDuration(incident.Duration),
            loc.Get($"cause.{incident.Cause.Key()}")));
        sb.AppendLine();

        if (incident.Explanation is { } explanation)
        {
            AppendParagraph(sb, loc.Get("incidents.what"), explanation.What);
            AppendParagraph(sb, loc.Get("incidents.likelyCause"), explanation.LikelyCause);
            AppendParagraph(sb, loc.Get("incidents.whatToDo"), explanation.WhatToDo);
        }

        if (incident.Note is { Length: > 0 } note)
        {
            AppendParagraph(sb, loc.Get("incidents.note"), note);
        }

        if (incident.RecentChanges.Count > 0)
        {
            AppendList(sb, loc.Get("incidents.recentChanges"), incident.RecentChanges);
        }

        if (throughput is { } traffic)
        {
            AppendList(sb, loc.Get("incidents.traffic"), traffic.Describe(loc.Get, loc.Format));
        }

        if (incident.Traceroute is { } route)
        {
            AppendList(sb, loc.Format("incidents.route", route.Target), route.Describe(loc.Get("incidents.route.noAnswer")));
        }

        if (incident.Timeline.Count > 0)
        {
            AppendList(
                sb,
                loc.Get("incidents.timeline"),
                [.. incident.Timeline.Select(t => $"{t.AtUtc.ToLocalTime():HH:mm:ss}  {t.Text}")]);
        }

        if (incident.StepsAtStart.Count > 0)
        {
            AppendList(
                sb,
                loc.Get("incidents.stepsAtStart"),
                [.. incident.StepsAtStart.Select(s => $"{loc.Get($"step.{s.Step.Key()}.name")}: {s.Headline}")]);
        }

        return sb.ToString();
    }

    private static void AppendParagraph(StringBuilder sb, string label, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        sb.AppendLine(label.ToUpperInvariant());
        sb.AppendLine(body);
        sb.AppendLine();
    }

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> lines)
    {
        sb.AppendLine(label.ToUpperInvariant());
        foreach (string line in lines)
        {
            sb.AppendLine($"- {line}");
        }

        sb.AppendLine();
    }
}
