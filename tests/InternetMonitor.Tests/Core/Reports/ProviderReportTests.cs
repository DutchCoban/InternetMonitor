using System.Globalization;
using System.Text.RegularExpressions;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.Reports;

[Collection("Localization")]
public sealed class ProviderReportTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(2);

    [Fact]
    public void Monitored_time_leaves_out_stretches_the_app_was_not_running()
    {
        // One hour of samples every 15 s, a ten-hour gap, then another hour.
        List<MetricPoint> points = [.. Samples(From, TimeSpan.FromHours(1)), .. Samples(From.AddHours(11), TimeSpan.FromHours(1))];

        TimeSpan monitored = ProviderReportData.MonitoredTime(points);

        Assert.InRange(monitored.TotalMinutes, 119, 120);
    }

    [Fact]
    public void Availability_counts_only_outages_on_the_providers_side()
    {
        ProviderReportData data = Compute(
            Samples(From, TimeSpan.FromHours(10)),
            Incident(RootCause.Internet, From.AddHours(1), TimeSpan.FromMinutes(30)),
            Incident(RootCause.Link, From.AddHours(2), TimeSpan.FromHours(1)));

        Assert.Equal(2, data.Outages.Count);
        Assert.Equal(1 - (0.5 / 10), data.Availability!.Value, 3);
        Assert.Equal(OutageSide.ThisComputer, Assert.Single(data.OutagesByCause, g => g.Cause == RootCause.Link).Side);
    }

    [Fact]
    public void Outages_are_clipped_to_the_period_and_warnings_are_not_outages()
    {
        ProviderReportData data = Compute(
            Samples(From, TimeSpan.FromHours(1)),
            Incident(RootCause.Dns, From.AddMinutes(-20), TimeSpan.FromMinutes(30)),
            Incident(RootCause.FirewallSuspected, From.AddHours(1), TimeSpan.FromMinutes(5)),
            Incident(RootCause.NetworkHealth, From.AddHours(1), TimeSpan.FromMinutes(5), conditionKey: "packetLoss"));

        OutageEntry outage = Assert.Single(data.Outages);
        Assert.Equal(TimeSpan.FromMinutes(10), outage.Duration);
    }

    [Fact]
    public void Nothing_measured_means_no_availability_figure_rather_than_100_percent()
    {
        Assert.Null(Compute([]).Availability);
    }

    [Fact]
    public void Chart_numbers_use_a_dot_whatever_the_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            ProviderReportData data = Compute(Samples(From, TimeSpan.FromHours(30), value: 12.5));

            string html = ProviderReportHtml.Render(data, new ProviderReportOptions(100, 20, "2.1.0", To, TimeZoneInfo.Utc), LocalizationManager.Instance);

            Assert.Contains("<polyline", html);
            Assert.DoesNotMatch(new Regex(@"points=""[^""]*\d,\d+,"), html);
            Assert.Matches(new Regex(@"points=""\d+\.\d,\d+\.\d"), html);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ProviderReportData Compute(IReadOnlyList<MetricPoint> adapterAndLatency, params Incident[] incidents)
    {
        IReadOnlyList<MetricPoint> History(string series) =>
            series == SeriesNames.For(InternetMonitor.Core.Steps.StepId.Adapter) || series == SeriesNames.For(InternetMonitor.Core.Steps.StepId.PublicIp)
                ? adapterAndLatency
                : [];

        return ProviderReportData.Compute(incidents, History, From, To, TimeZoneInfo.Utc);
    }

    private static List<MetricPoint> Samples(DateTimeOffset start, TimeSpan length, double value = 1)
    {
        var points = new List<MetricPoint>();
        for (DateTimeOffset t = start; t <= start + length; t += TimeSpan.FromSeconds(15))
        {
            points.Add(new MetricPoint(t, value, null));
        }

        return points;
    }

    private static Incident Incident(RootCause cause, DateTimeOffset start, TimeSpan duration, string? conditionKey = null) => new()
    {
        Id = $"INC-{start:HHmmss}",
        Cause = cause,
        StartUtc = start,
        EndUtc = start + duration,
        Status = IncidentStatus.Resolved,
        ConditionKey = conditionKey,
    };
}
