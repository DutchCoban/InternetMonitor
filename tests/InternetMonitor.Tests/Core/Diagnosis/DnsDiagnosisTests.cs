using System.Net;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Diagnosis;

/// <summary>Which explanation a DNS fault gets, by what step 6 found out about the adapter's own server.</summary>
[Collection("Localization")]
public sealed class DnsDiagnosisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 18, 3, 19, TimeSpan.Zero);

    private static StepSnapshot Snapshot(bool? ownReachable)
    {
        var facts = new DnsFacts("www.ripe.net", [], null, [IPAddress.Parse("1.1.1.1")], true, OwnServersReachable: ownReachable);
        return new StepSnapshot([new StepResult(StepId.Dns, StepState.Failed, "x", null, null, TimeSpan.Zero, T0, [], facts)], T0);
    }

    [Fact]
    public void An_own_server_that_is_silent_but_reachable_on_443_is_explained_as_dropped_dns_traffic()
    {
        Explanation explanation = PlainLanguage.DescribeEnglish(RootCause.Dns, Snapshot(ownReachable: true));

        Assert.Equal("The DNS server this computer uses is reachable but gives no DNS answer, while the rest of the connection is fine.", explanation.What);
        Assert.Contains("(1.1.1.1), which did accept a connection on port 443", explanation.LikelyCause);
        Assert.Contains("drops DNS traffic (port 53)", explanation.LikelyCause);
        Assert.Contains("A second DNS server", explanation.WhatToDo);
    }

    [Fact]
    public void An_own_server_that_is_not_reachable_keeps_the_plain_own_resolver_explanation()
    {
        Assert.Equal(
            "The DNS server this computer uses is not answering, while the rest of the connection is fine.",
            PlainLanguage.DescribeEnglish(RootCause.Dns, Snapshot(ownReachable: false)).What);
        Assert.Equal(
            "The DNS server this computer uses is not answering, while the rest of the connection is fine.",
            PlainLanguage.DescribeEnglish(RootCause.Dns, Snapshot(ownReachable: null)).What);
    }
}
