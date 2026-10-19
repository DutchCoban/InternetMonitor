using System.Net;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools.Traceroute;

/// <summary>Where an opening incident's traceroute goes: the DNS server for a DNS incident, the reachability host otherwise.</summary>
public sealed class IncidentTracerouteTargetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 18, 3, 19, TimeSpan.Zero);
    private static readonly IPAddress Cloudflare = IPAddress.Parse("1.1.1.1");
    private static readonly IPAddress Quad9 = IPAddress.Parse("9.9.9.9");
    private static readonly IPAddress ReachabilityHost = IPAddress.Parse("167.235.114.38");

    private static StepResult Result(StepId id, StepState state, StepFacts facts) =>
        new(id, state, id.ToString(), null, null, TimeSpan.Zero, T0, [], facts);

    private static StepResult Dns(params IPAddress[] servers) =>
        Result(StepId.Dns, StepState.Failed, new DnsFacts("www.ripe.net", [], null, servers, true));

    private static StepResult Target() =>
        Result(StepId.Target, StepState.Ok, new TargetFacts([new TargetEndpointResult("Internet", TargetKind.Url, "https://example.test/generate_204", true, 204, 204, HttpStage.None, null, null, ReachabilityHost, null, IsReachabilityCheck: true)]));

    [Fact]
    public void A_dns_incident_traces_to_the_adapters_own_dns_server()
    {
        var snapshot = new StepSnapshot([Dns(Cloudflare), Target()], T0);

        Assert.Equal("1.1.1.1", IncidentTracerouteTarget.For(RootCause.Dns, snapshot, [Quad9]));
    }

    [Fact]
    public void A_dns_incident_skips_an_ipv6_server_in_favour_of_the_first_ipv4_one()
    {
        var snapshot = new StepSnapshot([Dns(IPAddress.Parse("2606:4700:4700::1111"), Cloudflare)], T0);

        Assert.Equal("1.1.1.1", IncidentTracerouteTarget.For(RootCause.Dns, snapshot, [Quad9]));
    }

    [Fact]
    public void A_dns_incident_without_servers_falls_back_to_the_reachability_host()
    {
        var snapshot = new StepSnapshot([Dns(), Target()], T0);

        Assert.Equal("167.235.114.38", IncidentTracerouteTarget.For(RootCause.Dns, snapshot, [Quad9]));
    }

    [Fact]
    public void Other_incidents_trace_to_the_reachability_host_then_the_first_public_address_then_nothing()
    {
        var snapshot = new StepSnapshot([Dns(Cloudflare), Target()], T0);

        Assert.Equal("167.235.114.38", IncidentTracerouteTarget.For(RootCause.Internet, snapshot, [Quad9]));
        Assert.Equal("9.9.9.9", IncidentTracerouteTarget.For(RootCause.Target, null, [Quad9]));
        Assert.Null(IncidentTracerouteTarget.For(RootCause.Internet, null, []));
    }
}
