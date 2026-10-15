using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.Steps;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Diagnosis;

[Collection("Localization")]
public sealed class VpnDiagnosisTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NetworkInterfaceType.Ppp, "Work", "WAN Miniport (IKEv2)", true)]
    [InlineData(NetworkInterfaceType.Unknown, "wg0", "WireGuard Tunnel", true)]
    [InlineData(NetworkInterfaceType.Ethernet, "Ethernet 3", "TAP-Windows Adapter V9", true)]
    [InlineData(NetworkInterfaceType.Unknown, "Tailscale", "Tailscale Tunnel", true)]
    [InlineData(NetworkInterfaceType.Tunnel, "Teredo Tunneling Pseudo-Interface", "Microsoft Teredo Tunneling Adapter", false)]
    [InlineData(NetworkInterfaceType.Tunnel, "6to4 Adapter", "Microsoft 6to4 Adapter", false)]
    [InlineData(NetworkInterfaceType.Wireless80211, "WiFi", "Intel(R) Wi-Fi 6 AX101", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false)]
    public void Recognises_vpn_adapters_but_not_windows_transition_tunnels(NetworkInterfaceType type, string name, string description, bool expected)
    {
        Assert.Equal(expected, VpnDetector.LooksLikeVpn(type, name, description));
    }

    [Fact]
    public void A_vpn_connecting_and_disconnecting_are_changes_without_its_name()
    {
        var detector = new ChangeDetector();
        detector.Observe(Snapshot(Start, vpn: null));

        EnvironmentChange connected = Assert.Single(detector.Observe(Snapshot(Start.AddSeconds(15), new VpnInfo("Acme Corp VPN", true))));
        Assert.Equal(ChangeKind.VpnConnected, connected.Kind);
        Assert.DoesNotContain("Acme", connected.DescribeEnglish());

        Assert.Empty(detector.Observe(Snapshot(Start.AddSeconds(30), new VpnInfo("Acme Corp VPN", true))));
        Assert.Equal(ChangeKind.VpnDisconnected, Assert.Single(detector.Observe(Snapshot(Start.AddSeconds(45), vpn: null))).Kind);
    }

    [Fact]
    public void A_dns_failure_through_a_full_tunnel_points_at_the_vpn()
    {
        Explanation explanation = PlainLanguage.DescribeEnglish(RootCause.Dns, Snapshot(Start, new VpnInfo("wg0", true)));

        Assert.Contains("VPN", explanation.WhatToDo);
    }

    [Fact]
    public void A_split_tunnel_keeps_the_ordinary_explanation()
    {
        Explanation explanation = PlainLanguage.DescribeEnglish(RootCause.Dns, Snapshot(Start, new VpnInfo("wg0", false)));

        Assert.DoesNotContain("VPN", explanation.WhatToDo);
    }

    [Fact]
    public void The_ai_export_keeps_only_whether_the_vpn_carried_all_traffic()
    {
        var names = new Pseudonymizer();

        Assert.Equal("VPN (all traffic)", names.Measurement("VPN", "Acme Corp VPN (al het verkeer)"));
        Assert.Equal("VPN (some traffic only)", names.Measurement("VPN", "Acme Corp VPN (only some traffic)"));
    }

    private static StepSnapshot Snapshot(DateTimeOffset at, VpnInfo? vpn)
    {
        StepResult R(StepId id, StepState state, StepFacts? facts = null) =>
            new(id, state, id.ToString(), null, null, TimeSpan.Zero, at, [], facts);

        var ip = new IpAddressFacts(IPAddress.Parse("192.168.1.20"), null, false, true, [IPAddress.Parse("192.168.1.1")], IPAddress.Parse("192.168.1.1"), vpn);

        return new StepSnapshot(
        [
            R(StepId.Adapter, StepState.Ok),
            R(StepId.Link, StepState.Ok, new LinkFacts(PhysicalLinkState.Connected, OperationalStatus.Up, false, "Ethernet")),
            R(StepId.IpAddress, StepState.Ok, ip),
            R(StepId.Gateway, StepState.Ok),
            R(StepId.PublicIp, StepState.Ok),
            R(StepId.Dns, StepState.Failed),
            R(StepId.Target, StepState.Skipped),
        ], at);
    }
}
