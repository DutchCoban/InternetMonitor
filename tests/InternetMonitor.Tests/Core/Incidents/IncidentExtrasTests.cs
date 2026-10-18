using System.Text.Json;
using System.Text.Json.Serialization;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

/// <summary>A note and a traceroute survive the trip through incidents.json, and an endpoint series name round-trips.</summary>
public sealed class IncidentExtrasTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void Note_and_route_round_trip_through_json()
    {
        var incident = new Incident
        {
            Id = "INC-1",
            Cause = RootCause.Internet,
            StartUtc = DateTimeOffset.UtcNow,
            Headline = "x",
            Note = "Called the provider, ticket 12345",
            Traceroute = new IncidentTraceroute("203.0.113.1", DateTimeOffset.UtcNow, false,
            [
                new IncidentHop(1, "192.168.1.1", "router.lan", null, 1.2),
                new IncidentHop(2, null, null, null, null),
                new IncidentHop(3, "203.0.113.9", null, "AS64500  EXAMPLE-NET  NL", 12.5),
            ]),
        };

        Incident? back = JsonSerializer.Deserialize<Incident>(JsonSerializer.Serialize(incident, Json), Json);

        Assert.NotNull(back);
        Assert.Equal("Called the provider, ticket 12345", back.Note);
        Assert.NotNull(back.Traceroute);
        Assert.Equal(3, back.Traceroute.Hops.Count);
        Assert.Null(back.Traceroute.Hops[1].Address);
        Assert.Equal("AS64500  EXAMPLE-NET  NL", back.Traceroute.Hops[2].Owner);
    }

    [Fact]
    public void A_route_describes_each_hop_on_one_line()
    {
        var route = new IncidentTraceroute("203.0.113.1", DateTimeOffset.UtcNow, true,
        [
            new IncidentHop(1, "192.168.1.1", "router.lan", null, 1.2),
            new IncidentHop(2, null, null, null, null),
        ]);

        IReadOnlyList<string> lines = route.Describe("no answer");

        Assert.Equal(" 1. 192.168.1.1  router.lan  1 ms", lines[0]);
        Assert.Equal(" 2. no answer", lines[1]);
    }

    [Fact]
    public void An_incident_without_extras_serialises_as_before()
    {
        var incident = new Incident { Id = "INC-1", Cause = RootCause.Dns, StartUtc = DateTimeOffset.UtcNow, Headline = "x" };

        string json = JsonSerializer.Serialize(incident, Json);

        Assert.DoesNotContain("\"Note\"", json);
        Assert.DoesNotContain("\"Traceroute\"", json);
        Assert.DoesNotContain("\"Throughput\"", json);
    }

    [Fact]
    public void The_text_export_lists_the_traffic_around_the_incident_when_given_it()
    {
        var incident = new Incident { Id = "INC-1", Cause = RootCause.Dns, StartUtc = DateTimeOffset.UtcNow, Headline = "x" };
        var traffic = new IncidentThroughput(null, new ThroughputWindow(incident.StartUtc.AddMinutes(-5), incident.StartUtc, 20, 41, 4.1, 140, 14, incident.StartUtc.AddSeconds(-45), 0.205, 0.7), null, null, 200, 20, ThroughputCapacitySource.SpeedTest);

        string without = IncidentReportBuilder.Build(incident);
        string with = IncidentReportBuilder.Build(incident, traffic);

        Assert.DoesNotContain("TRAFFIC", without);
        Assert.Contains("THIS COMPUTER'S TRAFFIC", with);
        Assert.Contains("- 5 min before: average 41.0 / 4.1 Mbit/s (21% of capacity), peak 140.0 / 14.0 Mbit/s (70%) at ", with);
        Assert.Contains("- Capacity: 200.0 / 20.0 Mbit/s, from the last speed test", with);
        Assert.Contains("- Only this computer's traffic is visible", with);
    }

    [Fact]
    public void Endpoint_series_names_round_trip()
    {
        string id = SeriesNames.ForEndpoint("abc123");

        Assert.Equal("endpoint-abc123", id);
        Assert.Equal("abc123", SeriesNames.EndpointOf(id));
        Assert.Null(SeriesNames.EndpointOf("gateway"));
        Assert.Null(SeriesNames.EndpointOf("endpoint-"));
    }
}
