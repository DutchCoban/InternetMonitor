using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>The shallow per-adapter reading: address, network, APIPA, DHCP flag and server, gateway, up/down - from made-up adapters only.</summary>
public sealed class AdapterInventoryTests
{
    [Fact]
    public void Every_candidate_gets_a_reading_with_what_the_monitor_compares()
    {
        var ethernet = new FakeAdapter("eth", "Ethernet", "Intel I219", ip: new FakeIpProperties(
            unicast: [(IPAddress.Parse("192.168.1.23"), IPAddress.Parse("255.255.255.0"))],
            gateways: [IPAddress.Parse("192.168.1.1")],
            dns: [IPAddress.Parse("192.168.1.1")],
            dhcpServer: IPAddress.Parse("192.168.1.1")));
        var apipa = new FakeAdapter("usb", "USB Ethernet", "Realtek USB", ip: new FakeIpProperties(
            unicast: [(IPAddress.Parse("169.254.17.4"), IPAddress.Parse("255.255.0.0"))]));
        var disabled = new FakeAdapter("wifi", "Wi-Fi", "Intel AX", NetworkInterfaceType.Wireless80211, OperationalStatus.Down, ip: new FakeIpProperties(noIpv4: true));
        var virtualSwitch = new FakeAdapter("vm", "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", ip: new FakeIpProperties(
            unicast: [(IPAddress.Parse("172.20.0.1"), IPAddress.Parse("255.255.240.0"))], dhcp: false));

        IReadOnlyList<AdapterReading> readings = AdapterInventory.Read([ethernet, apipa, disabled, virtualSwitch]);

        Assert.Equal(4, readings.Count);

        AdapterReading eth = readings[0];
        Assert.Equal(("eth", "Ethernet", true, false, false), (eth.Id, eth.Name, eth.Up, eth.IsWireless, eth.IsLikelyVirtual));
        Assert.Equal("192.168.1.0/24", eth.Network);
        Assert.True(eth.HasRealAddress);
        Assert.True(eth.DhcpEnabled);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), eth.DhcpServer);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), eth.Gateway);

        AdapterReading usb = readings[1];
        Assert.True(usb.IsApipa);
        Assert.False(usb.HasRealAddress);
        Assert.Null(usb.Gateway);
        Assert.Null(usb.DhcpServer);
        Assert.Equal("169.254.0.0/16", usb.Network);

        AdapterReading wifi = readings[2];
        Assert.False(wifi.Up);
        Assert.True(wifi.IsWireless);
        Assert.Null(wifi.Address);
        Assert.False(wifi.DhcpEnabled);
        Assert.Null(wifi.Network);

        AdapterReading vm = readings[3];
        Assert.True(vm.IsLikelyVirtual);
        Assert.False(vm.DhcpEnabled);
        Assert.Equal("172.20.0.0/20", vm.Network);
    }

    [Fact]
    public void Apipa_is_exactly_the_169_254_range()
    {
        Assert.True(InterfaceSelector.IsApipa(IPAddress.Parse("169.254.0.1")));
        Assert.True(InterfaceSelector.IsApipa(IPAddress.Parse("169.254.255.254")));
        Assert.False(InterfaceSelector.IsApipa(IPAddress.Parse("169.253.1.1")));
        Assert.False(InterfaceSelector.IsApipa(IPAddress.Parse("192.168.1.1")));
        Assert.False(InterfaceSelector.IsApipa(IPAddress.Parse("fe80::1")));
    }
}
