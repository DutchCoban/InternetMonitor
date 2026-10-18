using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Steps;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

/// <summary>A snapshot becomes one reading: the primary adapter's facts, every adapter's situation, and the gateway's hardware address only when step 4 reached it.</summary>
public sealed class NetworkReadingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress Gateway = IPAddress.Parse("192.168.1.1");

    private static StepSnapshot Snapshot(StepState gateway, bool withAdapterId = true)
    {
        StepResult R(StepId id, StepState state, StepFacts? facts = null) =>
            new(id, state, id.ToString(), null, null, TimeSpan.Zero, At, [], facts);

        var adapter = new AdapterFacts(true, true, "Ethernet", "Intel", NetworkInterfaceType.Ethernet, "AA:BB", 1000, false, false, 2, withAdapterId ? "{eth}" : null);
        var ip = new IpAddressFacts(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"), false, true, [Gateway, IPAddress.Parse("9.9.9.9")], Gateway, new VpnInfo("Work", true), IPAddress.Parse("2a02:a46b:1234:5678:1:2:3:4"));
        var link = new LinkFacts(PhysicalLinkState.Connected, OperationalStatus.Up, true, "Ethernet", new WifiInfo("Home", "00:11", 80, -50, 36, 5180, "Wi-Fi 6", 600, 600, false, 0, 0));

        return new StepSnapshot(
        [
            R(StepId.Adapter, StepState.Ok, adapter),
            R(StepId.Link, StepState.Ok, link),
            R(StepId.IpAddress, StepState.Ok, ip),
            R(StepId.Gateway, gateway),
        ], At)
        {
            Adapters = [new AdapterReading("{eth}", "Ethernet", false, false, true, IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"), false, true, Gateway, Gateway)],
        };
    }

    [Fact]
    public void The_reading_carries_the_primary_adapters_facts_and_the_adapter_list()
    {
        NetworkReading reading = NetworkReading.From(Snapshot(StepState.Ok), ip => ip.Equals(Gateway) ? "AA:BB:CC:DD:EE:FF" : null);

        Assert.Equal(At, reading.AtUtc);
        Assert.Equal("{eth}", reading.PrimaryId);
        Assert.Equal("Ethernet", reading.PrimaryName);
        Assert.Equal(Gateway, reading.Gateway);
        Assert.Equal("AA:BB:CC:DD:EE:FF", reading.GatewayMac);
        Assert.Equal([Gateway, IPAddress.Parse("9.9.9.9")], reading.DnsServers);
        Assert.Equal("2a02:a46b:1234:5678::/64", reading.Ipv6Prefix);
        Assert.Equal("Home", reading.Ssid);
        Assert.True(reading.Vpn);
        Assert.Equal("192.168.1.0/24", Assert.Single(reading.Adapters).Network);
        Assert.Equal("{eth}", reading.Primary!.Id);
    }

    [Fact]
    public void The_hardware_address_is_only_looked_up_when_the_gateway_answered_this_cycle()
    {
        int lookups = 0;
        NetworkReading reading = NetworkReading.From(Snapshot(StepState.Failed), _ => { lookups++; return "AA"; });

        Assert.Null(reading.GatewayMac);
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void Without_an_adapter_id_there_is_no_primary_and_nothing_can_be_compared()
    {
        NetworkReading reading = NetworkReading.From(Snapshot(StepState.Ok, withAdapterId: false), _ => null);

        Assert.Null(reading.PrimaryId);
        Assert.Null(reading.Primary);
    }
}
