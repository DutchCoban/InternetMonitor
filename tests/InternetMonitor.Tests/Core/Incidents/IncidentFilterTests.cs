using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

[Collection("Localization")]
public sealed class IncidentFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    private static readonly Incident DnsYesterday = Make("INC-1", RootCause.Dns, Now.AddHours(-20), TimeSpan.FromMinutes(3), "DNS lookups fail", timeline: "Router restarted");
    private static readonly Incident LinkLastWeek = Make("INC-2", RootCause.Link, Now.AddDays(-5), TimeSpan.FromMinutes(10), "Wi-Fi disconnected");
    private static readonly Incident InternetOld = Make("INC-3", RootCause.Internet, Now.AddDays(-20), TimeSpan.FromHours(1), "No internet");
    private static readonly Incident GatewayNow = Make("INC-4", RootCause.Gateway, Now.AddMinutes(-4), null, "Router not answering");
    private static readonly Incident LossWarning = Make("INC-5", RootCause.NetworkHealth, Now.AddHours(-2), TimeSpan.FromMinutes(5), "Packet loss above threshold", conditionKey: "packetLoss");

    private static readonly IReadOnlyList<Incident> All = [GatewayNow, LossWarning, DnsYesterday, LinkLastWeek, InternetOld];

    [Fact]
    public void No_criteria_returns_everything()
    {
        Assert.Same(All, IncidentFilter.Apply(All, new IncidentFilterCriteria(), Now));
    }

    [Fact]
    public void Every_word_must_match_somewhere_in_the_incident()
    {
        Assert.Equal([DnsYesterday], Filter(new(Search: "dns router")));
        Assert.Equal([DnsYesterday], Filter(new(Search: "inc-1")));
        Assert.Empty(Filter(new(Search: "dns wifi")));
    }

    [Fact]
    public void The_cause_name_is_searchable()
    {
        Assert.Equal([InternetOld], Filter(new(Search: "internet")));
    }

    [Fact]
    public void Side_follows_the_provider_report_and_warnings_are_other()
    {
        Assert.Equal([DnsYesterday, InternetOld], Filter(new(Side: IncidentSideFilter.Provider)));
        Assert.Equal([GatewayNow], Filter(new(Side: IncidentSideFilter.HomeNetwork)));
        Assert.Equal([LinkLastWeek], Filter(new(Side: IncidentSideFilter.ThisComputer)));
        Assert.Equal([LossWarning], Filter(new(Side: IncidentSideFilter.Other)));
    }

    [Fact]
    public void A_period_includes_incidents_that_overlap_it_and_active_means_still_open()
    {
        Assert.Equal([GatewayNow, LossWarning, DnsYesterday], Filter(new(Period: IncidentPeriodFilter.Day)));
        Assert.Equal([GatewayNow, LossWarning, DnsYesterday, LinkLastWeek], Filter(new(Period: IncidentPeriodFilter.Week)));
        Assert.Equal([GatewayNow], Filter(new(Period: IncidentPeriodFilter.Active)));
    }

    [Fact]
    public void Criteria_combine()
    {
        Assert.Equal([DnsYesterday], Filter(new(Side: IncidentSideFilter.Provider, Period: IncidentPeriodFilter.Week)));
    }

    private static IReadOnlyList<Incident> Filter(IncidentFilterCriteria criteria) => IncidentFilter.Apply(All, criteria, Now);

    private static Incident Make(string id, RootCause cause, DateTimeOffset start, TimeSpan? duration, string headline, string? timeline = null, string? conditionKey = null) => new()
    {
        Id = id,
        Cause = cause,
        StartUtc = start,
        EndUtc = duration is { } d ? start + d : null,
        Status = duration is null ? IncidentStatus.Active : IncidentStatus.Resolved,
        Headline = headline,
        ConditionKey = conditionKey,
        Timeline = timeline is null ? [] : [new TimelineEntry(start, timeline)],
    };
}
