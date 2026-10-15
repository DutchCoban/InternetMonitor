using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Reports;

[Collection("Localization")]
public sealed class OutageSeriesTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(10);

    [Fact]
    public void Clearing_the_incident_list_does_not_change_availability()
    {
        List<MetricPoint> outage = Series(From, To, at => at >= From.AddHours(2) && at < From.AddHours(3) ? "internet|INC-A" : null);

        ProviderReportData data = Compute([], outage);

        OutageEntry entry = Assert.Single(data.Outages);
        Assert.Equal(RootCause.Internet, entry.Cause);
        Assert.Equal(1, entry.Duration.TotalHours, 2);
        Assert.Equal(1 - (1 / 10.0), data.Availability!.Value, 2);
    }

    [Fact]
    public void A_change_of_cause_starts_a_new_span_and_a_gap_ends_one()
    {
        List<MetricPoint> outage =
        [
            new(From, 1, "dns|INC-A"),
            new(From.AddSeconds(15), 1, "dns|INC-A"),
            new(From.AddSeconds(30), 1, "gateway|INC-B"),
            new(From.AddSeconds(45), 1, "gateway|INC-B"),

            // The app was off for an hour; the outage is only known up to the last sample.
            new(From.AddHours(1), 0, null),
        ];

        List<OutageSpan> spans = ProviderReportData.Spans([], id => id == SeriesNames.Outage ? outage : [], To);

        Assert.Equal(2, spans.Count);
        Assert.Equal((RootCause.Dns, "INC-A", From, From.AddSeconds(30)), (spans[0].Cause, spans[0].IncidentId, spans[0].StartUtc, spans[0].EndUtc));
        Assert.Equal((RootCause.Gateway, From.AddSeconds(45)), (spans[1].Cause, spans[1].EndUtc));
    }

    [Fact]
    public void An_incident_still_in_the_list_supplies_its_earlier_start()
    {
        List<MetricPoint> outage = [new(From.AddSeconds(6), 1, "dns|INC-A"), new(From.AddSeconds(30), 0, null)];
        Incident incident = new() { Id = "INC-A", Cause = RootCause.Dns, StartUtc = From, EndUtc = From.AddSeconds(30), Status = IncidentStatus.Resolved };

        OutageSpan span = Assert.Single(ProviderReportData.Spans([incident], id => id == SeriesNames.Outage ? outage : [], To));

        Assert.Equal(From, span.StartUtc);
    }

    [Fact]
    public void Before_the_series_began_the_incident_list_is_used()
    {
        List<MetricPoint> outage = [new(From.AddHours(5), 0, null)];
        Incident older = new() { Id = "INC-OLD", Cause = RootCause.Internet, StartUtc = From.AddHours(1), EndUtc = From.AddHours(1.5), Status = IncidentStatus.Resolved };
        Incident newer = new() { Id = "INC-NEW", Cause = RootCause.Internet, StartUtc = From.AddHours(6), EndUtc = From.AddHours(6.5), Status = IncidentStatus.Resolved };

        List<OutageSpan> spans = ProviderReportData.Spans([older, newer], id => id == SeriesNames.Outage ? outage : [], To);

        // The newer incident falls inside the series, which says nothing was down - so it is not double-counted.
        Assert.Equal("INC-OLD", Assert.Single(spans).IncidentId);
    }

    [Fact]
    public void An_outage_still_open_runs_up_to_now()
    {
        List<MetricPoint> outage = [new(To.AddMinutes(-3), 1, "internet|INC-A"), new(To.AddMinutes(-1), 1, "internet|INC-A")];

        OutageSpan span = Assert.Single(ProviderReportData.Spans([], id => id == SeriesNames.Outage ? outage : [], To));

        Assert.True(span.Ongoing);
        Assert.Equal(To, span.EndUtc);
    }

    private static ProviderReportData Compute(IReadOnlyList<Incident> incidents, IReadOnlyList<MetricPoint> outage)
    {
        List<MetricPoint> adapter = Series(From, To, _ => null, value: 1);
        return ProviderReportData.Compute(
            incidents,
            id => id == SeriesNames.Outage ? outage : id == SeriesNames.For(StepId.Adapter) ? adapter : [],
            From,
            To,
            TimeZoneInfo.Utc,
            To.AddDays(1));
    }

    /// <summary>One point every 15 s; down (with this state as metadata) wherever <paramref name="state"/> returns one.</summary>
    private static List<MetricPoint> Series(DateTimeOffset from, DateTimeOffset to, Func<DateTimeOffset, string?> state, double? value = null)
    {
        var points = new List<MetricPoint>();
        for (DateTimeOffset t = from; t <= to; t += TimeSpan.FromSeconds(15))
        {
            string? s = state(t);
            points.Add(new MetricPoint(t, value ?? (s is null ? 0 : 1), s));
        }

        return points;
    }
}
