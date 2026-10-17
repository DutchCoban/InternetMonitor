using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>
/// Which adapter the pipeline talks about when Windows has no route to ask about, which one a
/// fixed or preferred setting picks, and which adapters the VPN detector counts - all over
/// made-up adapters, never the machine's own.
/// </summary>
public sealed class AdapterRankingTests
{
    private static FakeIpProperties WithGateway() =>
        new(unicast: [(IPAddress.Parse("192.168.1.20"), null)], gateways: [IPAddress.Parse("192.168.1.1")]);

    [Fact]
    public void Candidates_leave_out_loopback_and_tunnels_but_keep_everything_else()
    {
        NetworkInterface[] all =
        [
            new FakeAdapter("lo", "Loopback", type: NetworkInterfaceType.Loopback),
            new FakeAdapter("teredo", "Teredo Tunneling", type: NetworkInterfaceType.Tunnel),
            new FakeAdapter("eth", "Ethernet"),
            new FakeAdapter("wifi", "Wi-Fi", type: NetworkInterfaceType.Wireless80211, status: OperationalStatus.Down),
        ];

        Assert.Equal(["eth", "wifi"], InterfaceSelector.Candidates(all).Select(n => n.Id));
    }

    [Fact]
    public void The_fallback_prefers_up_then_a_gateway_then_physical_then_fast()
    {
        var down = new FakeAdapter("down", "Ethernet 2", status: OperationalStatus.Down, speed: 10_000_000_000);
        var virtualUp = new FakeAdapter("hyperv", "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", ip: WithGateway());
        var slow = new FakeAdapter("slow", "Ethernet", "Intel(R) Ethernet Connection", speed: 100_000_000, ip: WithGateway());
        var fast = new FakeAdapter("fast", "Ethernet 3", "Realtek PCIe GbE", speed: 2_500_000_000, ip: WithGateway());
        var noGateway = new FakeAdapter("nogw", "Ethernet 4", "Realtek PCIe GbE", speed: 10_000_000_000);

        Assert.Equal("fast", InterfaceSelector.GuessPhysical([down, virtualUp, slow, fast, noGateway])!.Id);
        Assert.Equal("slow", InterfaceSelector.GuessPhysical([down, virtualUp, slow, noGateway])!.Id);
        Assert.Equal("hyperv", InterfaceSelector.GuessPhysical([down, virtualUp, noGateway])!.Id);
        Assert.Equal("nogw", InterfaceSelector.GuessPhysical([down, noGateway])!.Id);
        Assert.Equal("down", InterfaceSelector.GuessPhysical([down])!.Id);
        Assert.Null(InterfaceSelector.GuessPhysical([]));
    }

    [Fact]
    public void A_fixed_adapter_is_used_even_when_down_and_null_when_gone()
    {
        var down = new FakeAdapter("usb", "USB Ethernet", status: OperationalStatus.Down);
        var up = new FakeAdapter("wifi", "Wi-Fi", type: NetworkInterfaceType.Wireless80211, ip: WithGateway());

        Assert.Same(down, InterfaceSelector.GetPrimary([down, up], AdapterMode.Fixed, "usb"));
        Assert.Null(InterfaceSelector.GetPrimary([down, up], AdapterMode.Fixed, "gone"));
        Assert.Same(up, InterfaceSelector.GetPrimary([down, up], AdapterMode.Preferred, "wifi"));
    }

    [Theory]
    [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V", false)]
    [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("Ethernet 5", "VMware Virtual Ethernet Adapter for VMnet8", true)]
    [InlineData("wg0", "WireGuard Tunnel", true)]
    public void Virtual_adapters_are_recognised_by_name_or_description(string name, string description, bool expected)
    {
        Assert.Equal(expected, InterfaceSelector.IsLikelyVirtual(new FakeAdapter("x", name, description)));
    }

    [Fact]
    public void The_vpn_detector_counts_only_vpn_adapters_that_are_up_with_an_ipv4_address()
    {
        var ethernet = new FakeAdapter("eth", "Ethernet", "Intel(R) Ethernet", ip: WithGateway());
        var disconnected = new FakeAdapter("vpn-off", "OpenVPN TAP", "TAP-Windows Adapter V9", status: OperationalStatus.Down,
            ip: new FakeIpProperties(unicast: [(IPAddress.Parse("10.8.0.2"), null)], index: 4_000_002));
        var noAddress = new FakeAdapter("vpn-v6", "Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ppp,
            ip: new FakeIpProperties(unicast: [(IPAddress.Parse("fd7a::1"), null)], index: 4_000_003));
        var teredo = new FakeAdapter("teredo", "Teredo Tunneling Pseudo-Interface", type: NetworkInterfaceType.Tunnel,
            ip: new FakeIpProperties(unicast: [(IPAddress.Parse("10.9.0.2"), null)], index: 4_000_004));

        Assert.Null(VpnDetector.Detect([ethernet, disconnected, noAddress, teredo]));

        var connected = new FakeAdapter("vpn-on", "NordLynx", "NordLynx Tunnel", NetworkInterfaceType.Ppp,
            ip: new FakeIpProperties(unicast: [(IPAddress.Parse("10.5.0.2"), null)], index: 4_000_005));
        VpnInfo? found = VpnDetector.Detect([ethernet, disconnected, connected]);
        Assert.NotNull(found);
        Assert.Equal("NordLynx", found.Name);
        Assert.False(found.FullTunnel);
    }

    [Fact]
    public void A_vpn_adapter_with_the_routed_index_is_a_full_tunnel()
    {
        if (InterfaceSelector.GetRoutedIndex() is not { } routed)
        {
            // No route on this machine right now: there is nothing to compare the index against.
            return;
        }

        var split = new FakeAdapter("split", "Corporate VPN", "Cisco AnyConnect", NetworkInterfaceType.Ppp,
            ip: new FakeIpProperties(unicast: [(IPAddress.Parse("10.5.0.2"), null)], index: 4_000_006));
        var full = new FakeAdapter("full", "Proton VPN", "ProtonVPN TUN", NetworkInterfaceType.Ppp,
            ip: new FakeIpProperties(unicast: [(IPAddress.Parse("10.6.0.2"), null)], index: (int)routed));

        VpnInfo? found = VpnDetector.Detect([split, full]);
        Assert.NotNull(found);
        Assert.Equal("Proton VPN", found.Name);
        Assert.True(found.FullTunnel);
    }
}
