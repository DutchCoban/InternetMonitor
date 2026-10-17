using System.Net;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>
/// The gateway and public-internet steps, the ping helper and the traceroute, run for real against
/// 127.0.0.1 - the one address that always answers and never leaves the machine.
/// </summary>
[Collection("Localization")]
public sealed class LoopbackCheckTests
{
    [Fact]
    public async Task Without_a_gateway_the_gateway_step_fails_and_says_so()
    {
        var context = new StepContext(new CheckTargets()) { Gateway = null };

        StepResult result = await new GatewayCheck().RunAsync(context, CancellationToken.None);

        Assert.Equal(StepState.Failed, result.State);
        Assert.Equal("step.gateway.none", result.HeadlineKey);
        Assert.False(((GatewayFacts)result.Facts!).Reachable);
    }

    [Fact]
    public async Task The_loopback_gateway_is_reachable_with_a_latency()
    {
        var context = new StepContext(new CheckTargets()) { Gateway = IPAddress.Loopback };

        StepResult result = await new GatewayCheck().RunAsync(context, CancellationToken.None);

        Assert.NotEqual(StepState.Failed, result.State);
        Assert.Equal("step.gateway.ok", result.HeadlineKey);
        var facts = (GatewayFacts)result.Facts!;
        Assert.True(facts.Reachable);
        Assert.NotNull(facts.LatencyMs);
        Assert.Contains(result.Measurements, m => m.Value == "ICMP" || m.Value.StartsWith("TCP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_public_internet_step_reports_the_first_address_that_answers()
    {
        var targets = new CheckTargets { PublicIpTargets = [IPAddress.Loopback], PublicIpv6Targets = [] };
        var context = new StepContext(targets);

        StepResult result = await new PublicIpCheck().RunAsync(context, CancellationToken.None);

        Assert.Equal("step.publicIp.ok", result.HeadlineKey);
        var facts = (PublicIpFacts)result.Facts!;
        Assert.False(facts.AllUnreachable);
        Assert.Contains(result.Measurements, m => m.Label == "127.0.0.1" && m.Value.EndsWith(" ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ping_to_loopback_succeeds_and_one_to_a_black_hole_does_not()
    {
        PingOutcome home = await IcmpPing.SendAsync(IPAddress.Loopback, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(home.Success);
        Assert.NotNull(home.RoundtripMs);

        // 192.0.2.0/24 is reserved for documentation and routes nowhere.
        PingOutcome nowhere = await IcmpPing.SendAsync(IPAddress.Parse("192.0.2.1"), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.False(nowhere.Success);
    }

    [Fact]
    public async Task A_traceroute_to_loopback_arrives_at_the_first_hop()
    {
        var hops = new List<TracerouteHop>();
        var progress = new Progress<TracerouteHop>(hops.Add);

        TracerouteResult result = await new TracerouteRunner().RunAsync("127.0.0.1", progress, null, CancellationToken.None);

        Assert.True(result.ReachedDestination);
        Assert.Equal(IPAddress.Loopback, result.TargetAddress);
        Assert.Single(result.Hops);
        Assert.Contains(IPAddress.Loopback, result.Hops[0].Responders);
        Assert.Null(result.TcpReachability);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_gives_an_empty_trace()
    {
        TracerouteResult result = await new TracerouteRunner().RunAsync("no-such-host.invalid", null, null, CancellationToken.None);

        Assert.Null(result.TargetAddress);
        Assert.Empty(result.Hops);
        Assert.False(result.ReachedDestination);
    }
}
