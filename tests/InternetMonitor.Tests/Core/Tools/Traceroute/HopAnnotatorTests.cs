using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools.Traceroute;

/// <summary>
/// Regression coverage for a real bug found by inspection: a traceroute hop at 10.227.137.150 was
/// labelled "still within your own network", even though the user's actual LAN was 10.25.10.0/24.
/// RFC 1918 reserves the whole of 10.0.0.0/8 - sixteen million addresses - and the code was
/// conflating "private address space" with "the user's own network", which are not the same
/// thing: several ISPs number their own internal equipment out of 10.x, the same way others use
/// real carrier-grade NAT (100.64.0.0/10).
///
/// The fix itself introduced a second, narrower version of the same mistake: tracing immediately
/// at startup, before the pipeline had measured the user's subnet, meant HopAnnotator had no local
/// network to compare against and asserted "not yours" for every private hop - including the
/// gateway. <see cref="Annotate_does_not_guess_ownership_when_the_local_subnet_is_unknown"/> pins
/// down the fix for that: no local subnet means "unknown", not a guess in either direction.
/// </summary>
[Collection("Localization")]
public sealed class HopAnnotatorTests
{
    [Theory]
    [InlineData("10.0.0.0", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("9.255.255.255", false)]
    [InlineData("11.0.0.0", false)]
    [InlineData("172.16.0.0", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.0", false)]
    [InlineData("192.168.0.0", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("192.167.255.255", false)]
    [InlineData("192.169.0.0", false)]
    [InlineData("169.254.1.1", true)] // link-local / APIPA
    [InlineData("127.0.0.1", true)] // loopback
    [InlineData("100.64.0.1", false)] // CGNAT is its own range, not RFC 1918 - see IsCarrierGradeNat
    [InlineData("8.8.8.8", false)] // public
    [InlineData("1.1.1.1", false)] // public
    public void IsPrivate_covers_every_rfc1918_boundary(string address, bool expected) =>
        Assert.Equal(expected, HopAnnotator.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.0", false)]
    [InlineData("10.0.0.1", false)] // private, but not CGNAT - the two ranges are disjoint
    public void IsCarrierGradeNat_covers_the_100_64_slash_10_boundary(string address, bool expected) =>
        Assert.Equal(expected, HopAnnotator.IsCarrierGradeNat(IPAddress.Parse(address)));

    [Fact]
    public void Annotate_recognizes_a_hop_inside_the_measured_local_subnet_as_the_users_own_network()
    {
        TracerouteHop[] hops = [AnsweredHop(ttl: 1, "10.25.10.1")];

        HopAnnotator.Annotate(hops, reachedDestination: false, (IPAddress.Parse("10.25.10.214"), Mask24));

        Assert.Equal(Text("hop.privateNetwork"), hops[0].Annotation);
    }

    [Fact]
    public void Annotate_recognizes_a_private_hop_outside_the_measured_subnet_as_the_isps_network()
    {
        // The exact regression: a private address nowhere near the user's own /24.
        TracerouteHop[] hops = [AnsweredHop(ttl: 3, "10.227.137.150")];

        HopAnnotator.Annotate(hops, reachedDestination: false, (IPAddress.Parse("10.25.10.214"), Mask24));

        Assert.Equal(Text("hop.ispPrivateNetwork"), hops[0].Annotation);
    }

    [Fact]
    public void Annotate_does_not_guess_ownership_when_the_local_subnet_is_unknown()
    {
        TracerouteHop[] hops = [AnsweredHop(ttl: 1, "10.25.10.1")];

        HopAnnotator.Annotate(hops, reachedDestination: false, localNetwork: null);

        Assert.Equal(Text("hop.privateNetworkUnknown"), hops[0].Annotation);
    }

    [Fact]
    public void Annotate_falls_back_to_a_slash_24_when_no_mask_was_measured()
    {
        TracerouteHop[] hops = [AnsweredHop(ttl: 1, "10.25.10.1")];

        HopAnnotator.Annotate(hops, reachedDestination: false, (IPAddress.Parse("10.25.10.214"), null));

        Assert.Equal(Text("hop.privateNetwork"), hops[0].Annotation);
    }

    [Theory]
    // Same /12 as the user, different /24: not local - this is what 10.0.0.0/8 being treated as
    // one network got wrong, reproduced for the other two RFC 1918 ranges.
    [InlineData("172.16.40.1", "172.16.5.1", false)]
    [InlineData("172.16.5.1", "172.16.5.1", true)]
    [InlineData("192.168.99.1", "192.168.1.1", false)]
    [InlineData("192.168.1.1", "192.168.1.1", true)]
    public void Annotate_subnet_containment_is_consistent_across_every_rfc1918_range(
        string hopAddress, string localAddress, bool expectedOwnNetwork)
    {
        TracerouteHop[] hops = [AnsweredHop(ttl: 2, hopAddress)];

        HopAnnotator.Annotate(hops, reachedDestination: false, (IPAddress.Parse(localAddress), Mask24));

        string expectedKey = expectedOwnNetwork ? "hop.privateNetwork" : "hop.ispPrivateNetwork";
        Assert.Equal(Text(expectedKey), hops[0].Annotation);
    }

    private static readonly IPAddress Mask24 = IPAddress.Parse("255.255.255.0");

    private static string Text(string key) => LocalizationManager.Instance.Get(key);

    /// <summary>
    /// A hop with three clean TtlExpired replies from the same address - a fully healthy,
    /// non-silent, non-lossy hop, so Describe() falls through to the private-address branch under
    /// test rather than being short-circuited by the loss or silence checks that run first.
    /// </summary>
    private static TracerouteHop AnsweredHop(int ttl, string address)
    {
        IPAddress parsed = IPAddress.Parse(address);
        HopProbe[] probes =
        [
            new HopProbe(IPStatus.TtlExpired, 5.0, parsed),
            new HopProbe(IPStatus.TtlExpired, 5.0, parsed),
            new HopProbe(IPStatus.TtlExpired, 5.0, parsed),
        ];
        return new TracerouteHop(ttl, probes);
    }
}
