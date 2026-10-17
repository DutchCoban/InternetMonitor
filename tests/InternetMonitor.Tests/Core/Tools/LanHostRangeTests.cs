using System.Net;
using InternetMonitor.Core.Tools.LanScan;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools;

/// <summary>Which addresses a LAN scan visits: the subnet's hosts between network and broadcast, capped at 254.</summary>
public sealed class LanHostRangeTests
{
    [Fact]
    public void A_slash_24_gives_every_host_from_1_to_254()
    {
        (List<IPAddress> hosts, bool truncated) = LanScanner.EnumerateHosts(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"));

        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.254"), hosts[^1]);
        Assert.False(truncated);
    }

    [Fact]
    public void A_small_subnet_gives_only_its_two_hosts()
    {
        (List<IPAddress> hosts, bool truncated) = LanScanner.EnumerateHosts(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.255.252"));

        Assert.Equal([IPAddress.Parse("10.0.0.5"), IPAddress.Parse("10.0.0.6")], hosts);
        Assert.False(truncated);
    }

    [Fact]
    public void A_large_subnet_is_capped_and_says_so()
    {
        (List<IPAddress> hosts, bool truncated) = LanScanner.EnumerateHosts(IPAddress.Parse("10.1.2.3"), IPAddress.Parse("255.255.0.0"));

        Assert.Equal(254, hosts.Count);
        Assert.Equal(IPAddress.Parse("10.1.0.1"), hosts[0]);
        Assert.Equal(IPAddress.Parse("10.1.0.254"), hosts[^1]);
        Assert.True(truncated);
    }

    [Fact]
    public void A_host_route_and_an_ipv6_address_give_nothing_to_scan()
    {
        Assert.Empty(LanScanner.EnumerateHosts(IPAddress.Parse("10.0.0.5"), IPAddress.Parse("255.255.255.255")).Addresses);
        Assert.Empty(LanScanner.EnumerateHosts(IPAddress.Parse("2001:db8::1"), IPAddress.Parse("255.255.255.0")).Addresses);
    }
}
