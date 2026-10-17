using InternetMonitor.Core.Incidents;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

/// <summary>
/// The readable incident id is the local date and time it opened, to the millisecond - not a
/// counter, which restarts when the list is cleared and then names two different outages alike.
/// </summary>
public sealed class IncidentIdTests
{
    [Fact]
    public void The_id_is_the_local_date_and_time_to_the_millisecond_without_colons()
    {
        var at = new DateTimeOffset(2026, 10, 17, 19, 38, 16, 729, TimeSpan.Zero);
        DateTimeOffset local = at.ToLocalTime();

        string id = IncidentStore.IdFor(at);

        Assert.Equal($"INC-{local:yyyyMMdd}-{local:HHmmss}.729", id);
        Assert.DoesNotContain(':', id);
        Assert.Equal(IncidentStore.IdFor(at), IncidentStore.IdFor(at.ToOffset(TimeSpan.FromHours(5))));
    }
}
