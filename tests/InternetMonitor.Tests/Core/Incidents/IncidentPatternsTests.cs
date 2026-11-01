using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

/// <summary>
/// The patterns an incident shares with earlier ones, over made-up incidents: the same cause around
/// the same hour on several days, a start right after a network change like earlier ones, and a
/// route that stops at the same hop. Nothing is stored; everything is counted at read time.
/// </summary>
[Collection("Localization")]
public sealed class IncidentPatternsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static Incident At(int daysAgo, int hour, int minute, RootCause cause = RootCause.Dns, string? conditionKey = null, IReadOnlyList<string>? changes = null, IncidentTraceroute? route = null)
    {
        var start = new DateTimeOffset(Now.AddDays(-daysAgo).Date.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);
        return new Incident
        {
            Id = $"INC-{start:yyyyMMdd-HHmmss}",
            Cause = cause,
            StartUtc = start,
            EndUtc = start.AddMinutes(3),
            Status = IncidentStatus.Resolved,
            ConditionKey = conditionKey,
            RecentChanges = changes ?? [],
            Traceroute = route,
        };
    }

    [Fact]
    public void The_same_cause_around_the_same_hour_on_three_days_is_a_pattern_and_on_two_days_is_not()
    {
        Incident[] three = [At(1, 2, 30), At(3, 3, 0), At(6, 3, 40)];
        IncidentPattern pattern = Assert.Single(IncidentPatterns.For(three[1], three, Utc, Now));
        Assert.Equal(IncidentPatterns.SameHour, pattern.Key);
        Assert.Equal([3, 3, 3, 30], pattern.Args);

        // The first two incidents are part of it as well, now that the third has happened.
        Assert.Single(IncidentPatterns.For(three[0], three, Utc, Now));

        Incident[] twoDays = [At(1, 2, 30), At(1, 3, 0), At(3, 3, 40)];
        Assert.Empty(IncidentPatterns.For(twoDays[1], twoDays, Utc, Now));

        Incident[] otherCauses = [At(1, 3, 0, RootCause.Gateway), At(3, 3, 0, RootCause.Dns), At(6, 3, 0, RootCause.Internet)];
        Assert.Empty(IncidentPatterns.For(otherCauses[1], otherCauses, Utc, Now));

        Incident[] old = [At(1, 3, 0), At(31, 3, 0), At(40, 3, 0)];
        Assert.Empty(IncidentPatterns.For(old[0], old, Utc, Now));
    }

    [Fact]
    public void Around_the_same_hour_wraps_round_midnight()
    {
        Incident[] incidents = [At(1, 23, 40), At(2, 0, 10), At(4, 23, 55)];
        Assert.Single(IncidentPatterns.For(incidents[1], incidents, Utc, Now));
    }

    [Fact]
    public void Since_the_last_change_reads_the_clock_time_the_tracker_wrote_and_tolerates_midnight()
    {
        Assert.Equal(TimeSpan.FromSeconds(100), IncidentPatterns.SinceLastChange(At(1, 3, 0, changes: ["02:58:20  Adapter changed from A to B"]), Utc));
        Assert.Equal(TimeSpan.FromMinutes(2), IncidentPatterns.SinceLastChange(At(1, 0, 1, changes: ["23:59:00  Gateway changed"]), Utc));
        Assert.Null(IncidentPatterns.SinceLastChange(At(1, 3, 0), Utc));
        Assert.Null(IncidentPatterns.SinceLastChange(At(1, 3, 0, changes: ["Gateway changed from 10.0.0.1 to 10.0.0.2"]), Utc));
    }

    [Fact]
    public void Three_starts_right_after_a_network_change_are_a_pattern_and_two_are_not()
    {
        static Incident After(int daysAgo, int hour) => At(daysAgo, hour, 4, RootCause.Gateway, changes: [$"{hour:00}:00:30  Active adapter changed from Ethernet to Wi-Fi"]);

        Incident[] three = [After(1, 9), After(2, 14), After(5, 20)];
        IncidentPattern pattern = Assert.Single(IncidentPatterns.For(three[0], three, Utc, Now));
        Assert.Equal(IncidentPatterns.AfterChange, pattern.Key);
        Assert.Equal([5, 2, 30], pattern.Args);

        Incident[] two = [After(1, 9), After(2, 14), At(5, 20, 4, RootCause.Gateway)];
        Assert.Empty(IncidentPatterns.For(two[0], two, Utc, Now));

        // A change long before the start does not count, however many of them there are.
        Incident[] late = [At(1, 9, 30, RootCause.Gateway, changes: ["09:00:30  Active adapter changed"]), After(2, 14), After(5, 20)];
        Assert.Empty(IncidentPatterns.For(late[0], late, Utc, Now));
    }

    [Fact]
    public void The_route_stopping_at_the_same_hop_three_times_is_a_pattern()
    {
        static IncidentTraceroute Route(bool reached, params string?[] hops) =>
            new("9.9.9.9", Now, reached, [.. hops.Select((h, i) => new IncidentHop(i + 1, h, null, null, h is null ? null : 5.0))]);

        Assert.Equal("10.20.0.1", IncidentPatterns.StoppedAtHop(Route(false, "192.168.1.1", "10.20.0.1", null, null)));
        Assert.Null(IncidentPatterns.StoppedAtHop(Route(true, "192.168.1.1", "10.20.0.1", "9.9.9.9")));
        Assert.Null(IncidentPatterns.StoppedAtHop(Route(false, null, null)));
        Assert.Null(IncidentPatterns.StoppedAtHop(null));

        Incident[] three =
        [
            At(1, 9, 0, RootCause.Internet, route: Route(false, "192.168.1.1", "10.20.0.1", null)),
            At(2, 15, 0, RootCause.Internet, route: Route(false, "192.168.1.1", "10.20.0.1", null, null)),
            At(9, 21, 0, RootCause.Internet, route: Route(false, "192.168.1.1", "10.20.0.1", null)),
        ];
        IncidentPattern pattern = Assert.Single(IncidentPatterns.For(three[2], three, Utc, Now));
        Assert.Equal(IncidentPatterns.SameHop, pattern.Key);
        Assert.Equal(["10.20.0.1", 3, 30], pattern.Args);

        Incident[] differentHops = [three[0], three[1], At(9, 21, 0, RootCause.Internet, route: Route(false, "192.168.1.1", "10.20.0.2", null))];
        Assert.Empty(IncidentPatterns.For(differentHops[2], differentHops, Utc, Now));
    }

    [Fact]
    public void Across_a_set_lists_each_pattern_once_and_describe_makes_sentences()
    {
        Incident[] incidents = [At(1, 2, 30), At(3, 3, 0), At(6, 3, 40), At(2, 15, 0, RootCause.Gateway)];
        IReadOnlyList<IncidentPattern> across = IncidentPatterns.Across(incidents, Utc, Now);
        IncidentPattern pattern = Assert.Single(across);

        string sentence = Assert.Single(IncidentPatterns.Describe([pattern], LocalizationManager.FormatEnglish));
        Assert.Equal("Recurs around 3:00: 3 times on 3 different days in the last 30 days, same cause.", sentence);
    }

    [Fact]
    public void Network_health_incidents_only_match_their_own_condition()
    {
        Incident[] mixed = [At(1, 3, 0, RootCause.NetworkHealth, "packetLoss"), At(3, 3, 0, RootCause.NetworkHealth, "tcpRetransmissions"), At(6, 3, 0, RootCause.NetworkHealth, "packetLoss")];
        Assert.Empty(IncidentPatterns.For(mixed[0], mixed, Utc, Now));
    }
}
