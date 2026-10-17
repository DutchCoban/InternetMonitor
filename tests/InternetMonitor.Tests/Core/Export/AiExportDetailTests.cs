using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using Xunit;

namespace InternetMonitor.Tests.Core.Export;

/// <summary>
/// The AI export must fit an AI's window: the detail level caps incidents, timelines and speed
/// tests, says what it left out, and the document still names every section it has no data for.
/// </summary>
[Collection("Localization")]
public sealed class AiExportDetailTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 20, 0, 0, TimeSpan.Zero);

    private static List<Incident> Incidents(int count, int timelineLength) =>
    [
        .. Enumerable.Range(0, count).Select(i =>
        {
            var incident = new Incident
            {
                Id = $"INC-{i:000}",
                Cause = RootCause.Internet,
                StartUtc = Now.AddHours(-count + i),
                EndUtc = Now.AddHours(-count + i).AddMinutes(5),
                Status = IncidentStatus.Resolved,
                Headline = $"Outage number {i}",
            };
            incident.Timeline.AddRange(Enumerable.Range(0, timelineLength).Select(t => new TimelineEntry(incident.StartUtc.AddSeconds(t), $"timeline entry {t}")));
            return incident;
        }),
    ];

    private static Dictionary<string, IReadOnlyList<MetricPoint>> SpeedTests(int count) => new()
    {
        [SeriesNames.SpeedTestDownload] = [.. Enumerable.Range(0, count).Select(i => new MetricPoint(Now.AddHours(-6 * i), 100 + i, null))],
        [SeriesNames.SpeedTestUpload] = [.. Enumerable.Range(0, count).Select(i => new MetricPoint(Now.AddHours(-6 * i), 20, null))],
    };

    private static string Export(IReadOnlyList<Incident> incidents, Dictionary<string, IReadOnlyList<MetricPoint>> history, AiExportDetail detail, bool forAi = true) =>
        forAi
            ? AiExportBuilder.Build(incidents, history, new AppSettings(), "2.3.0", TimeZoneInfo.Utc, Now, detail)
            : AiExportBuilder.BuildPlain(incidents, history, new AppSettings(), "2.3.0", TimeZoneInfo.Utc, Now, detail);

    [Theory]
    [InlineData(AiExportDetail.Compact, 10)]
    [InlineData(AiExportDetail.Standard, 20)]
    [InlineData(AiExportDetail.Full, 30)]
    public void The_newest_incidents_are_full_and_the_rest_one_line_each(AiExportDetail detail, int fullExpected)
    {
        string md = Export(Incidents(30, 50), [], detail);

        Assert.Equal(fullExpected, md.Split("\n### INC-").Length - 1);
        if (fullExpected < 30)
        {
            Assert.Contains("### Older incidents (one line each)", md);
            Assert.Equal(30 - fullExpected, md.Split("\n| INC-").Length - 1);
            Assert.Contains("entries left out", md);
            Assert.Contains($"{30 - fullExpected} older incidents are summarised", md);
        }
        else
        {
            Assert.DoesNotContain("Older incidents", md);
            Assert.DoesNotContain("entries left out", md);
            Assert.Contains("nothing was left out", md);
        }

        // The newest incident is always among the full ones, the oldest among the summarised.
        Assert.Contains("### INC-029", md);
    }

    [Fact]
    public void Speed_tests_above_the_limit_keep_the_latest_rows_and_a_weekly_table()
    {
        string standard = Export([], SpeedTests(100), AiExportDetail.Standard);
        string full = Export([], SpeedTests(100), AiExportDetail.Full);

        Assert.Equal(60, TestRows(standard));
        Assert.Contains("### Per week", standard);
        Assert.Contains("40 speed-test rows are left out", standard);
        Assert.Equal(100, TestRows(full));
        Assert.DoesNotContain("### Per week", full);

        // Rows of the speed-test table only: "| 2026-10-17 20:00:00 |", not the per-week "| 2026-W42 |" rows.
        static int TestRows(string md) => md.Split('\n').Count(line => line.StartsWith("| 2026-", StringComparison.Ordinal) && !line.StartsWith("| 2026-W", StringComparison.Ordinal));
    }

    [Fact]
    public void Sections_without_data_say_so_instead_of_disappearing()
    {
        string md = Export([], [], AiExportDetail.Standard);

        foreach (string heading in new[] { "## Latency per day", "## Packet loss and jitter", "## Wi-Fi", "## This computer's own traffic", "## Network-health warnings" })
        {
            Assert.Contains(heading, md);
        }

        Assert.Contains("Not measured in this period.", md);
        Assert.Contains("Not monitored", md);
    }

    [Fact]
    public void The_ai_version_ends_with_a_reminder_and_explains_the_stand_ins_and_the_plain_version_does_not_instruct()
    {
        string ai = Export([], [], AiExportDetail.Compact);
        string plain = Export([], [], AiExportDetail.Compact, forAi: false);

        Assert.Contains("## Reminder for the AI", ai);
        Assert.True(ai.IndexOf("## Reminder for the AI", StringComparison.Ordinal) > ai.IndexOf("## Network-health warnings", StringComparison.Ordinal));
        Assert.Contains("Names in this document", ai);
        Assert.Contains("Detail level: compact", ai);
        Assert.DoesNotContain("## Instructions for the AI", plain);
        Assert.DoesNotContain("Reminder for the AI", plain);
        Assert.Contains("Names in this document", plain);
    }
}
