using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Diagnosis;

[Collection("Localization")]
public sealed class Ipv6DiagnosisTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_a_usable_global_address_counts_and_the_stable_one_is_preferred()
    {
        IPAddress? picked = IpAddressCheck.PickGlobalIpv6(
        [
            (IPAddress.Parse("fe80::1"), SuffixOrigin.LinkLayerAddress, DuplicateAddressDetectionState.Preferred),
            (IPAddress.Parse("fd00::5"), SuffixOrigin.Random, DuplicateAddressDetectionState.Preferred),
            (IPAddress.Parse("2001:0:53aa:64c::1"), SuffixOrigin.Other, DuplicateAddressDetectionState.Preferred),
            (IPAddress.Parse("2a02:a46b:1:2:aaaa::1"), SuffixOrigin.Random, DuplicateAddressDetectionState.Preferred),
            (IPAddress.Parse("2a02:a46b:1:2:bbbb::1"), SuffixOrigin.LinkLayerAddress, DuplicateAddressDetectionState.Preferred),
            (IPAddress.Parse("2a02:a46b:1:2:cccc::1"), SuffixOrigin.LinkLayerAddress, DuplicateAddressDetectionState.Tentative),
        ]);

        Assert.Equal(IPAddress.Parse("2a02:a46b:1:2:bbbb::1"), picked);
    }

    [Fact]
    public void A_link_local_address_alone_means_no_ipv6()
    {
        Assert.Null(IpAddressCheck.PickGlobalIpv6([(IPAddress.Parse("fe80::b07b:d56d:fb9a:c4e5"), SuffixOrigin.LinkLayerAddress, DuplicateAddressDetectionState.Preferred)]));
    }

    [Fact]
    public void Only_the_prefix_of_an_address_is_shown()
    {
        Assert.Equal("2a02:a46b:1:2::/64", IpAddressCheck.Prefix64(IPAddress.Parse("2a02:a46b:1:2:bbbb:cccc:dddd:eeee")));
    }

    [Fact]
    public void Ipv6_failing_while_ipv4_works_is_its_own_warning()
    {
        RootCauseResult result = RootCauseAnalyzer.Analyze(Snapshot(ipv6Reachable: false));

        Assert.Equal(RootCause.Ipv6Broken, result.Cause);
        Assert.Equal(Severity.Degraded, result.Severity);
        Assert.False(result.IsProblem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Working_or_untested_ipv6_is_healthy(bool? ipv6Reachable)
    {
        Assert.Equal(RootCause.Healthy, RootCauseAnalyzer.Analyze(Snapshot(ipv6Reachable)).Cause);
    }

    [Fact]
    public void The_explanation_points_at_the_router_or_provider()
    {
        Explanation explanation = PlainLanguage.DescribeEnglish(RootCause.Ipv6Broken, Snapshot(ipv6Reachable: false));

        Assert.Contains("IPv6", explanation.What);
        Assert.Contains("provider", explanation.WhatToDo);
    }

    private static StepSnapshot Snapshot(bool? ipv6Reachable)
    {
        StepResult R(StepId id, StepState state, StepFacts? facts = null) =>
            new(id, state, id.ToString(), null, null, TimeSpan.Zero, At, [], facts);

        var internet = new TargetEndpointResult("Internet", TargetKind.Url, "https://example.test/generate_204", true, 204, 204, HttpStage.None, null, null, null, null, IsReachabilityCheck: true);
        var target = new TargetFacts([internet], Ipv6Reachable: ipv6Reachable);

        return new StepSnapshot(
        [
            R(StepId.Adapter, StepState.Ok),
            R(StepId.Link, StepState.Ok, new LinkFacts(PhysicalLinkState.Connected, OperationalStatus.Up, false, "Ethernet")),
            R(StepId.IpAddress, StepState.Ok),
            R(StepId.Gateway, StepState.Ok),
            R(StepId.PublicIp, StepState.Ok),
            R(StepId.Dns, StepState.Ok),
            R(StepId.Target, ipv6Reachable == false ? StepState.Warning : StepState.Ok, target),
        ], At);
    }
}
