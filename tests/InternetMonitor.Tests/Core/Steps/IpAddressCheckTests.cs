using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Net;
using InternetMonitor.Tests.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>
/// Step 3 over made-up adapter properties: no address, a self-assigned one, a normal one with
/// what it publishes for the later steps, IPv6 on offer, and a VPN beside it.
/// </summary>
[Collection("Localization")]
public sealed class IpAddressCheckTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.168.1.20");
    private static readonly IPAddress Mask = IPAddress.Parse("255.255.255.0");
    private static readonly IPAddress Gateway = IPAddress.Parse("192.168.1.1");

    private static StepContext Context(FakeIpProperties? ip, IReadOnlyList<NetworkInterface>? adapters = null) =>
        new(new CheckTargets()) { IpProperties = ip, AllAdapters = adapters ?? [] };

    [Fact]
    public async Task Without_interface_properties_the_step_fails_as_no_address()
    {
        StepResult result = await new IpAddressCheck().RunAsync(Context(null), CancellationToken.None);

        Assert.Equal(StepState.Failed, result.State);
        Assert.Equal("step.ip.none", result.HeadlineKey);
        Assert.Equal("step.ip.none.detail", result.DetailKey);
        Assert.Null(((IpAddressFacts)result.Facts!).Address);
    }

    [Fact]
    public async Task An_adapter_without_an_ipv4_address_fails_as_no_address_too()
    {
        var ip = new FakeIpProperties(unicast: [(IPAddress.Parse("fe80::1"), null)]);

        StepResult result = await new IpAddressCheck().RunAsync(Context(ip), CancellationToken.None);

        Assert.Equal("step.ip.none", result.HeadlineKey);
        Assert.Empty(result.HeadlineArgs!);
    }

    [Fact]
    public async Task A_self_assigned_address_is_reported_as_apipa_not_as_working()
    {
        var ip = new FakeIpProperties(unicast: [(IPAddress.Parse("169.254.10.5"), IPAddress.Parse("255.255.0.0"))], dhcp: true);

        StepResult result = await new IpAddressCheck().RunAsync(Context(ip), CancellationToken.None);

        Assert.Equal(StepState.Failed, result.State);
        Assert.Equal("step.ip.apipa", result.HeadlineKey);
        Assert.Equal("step.ip.apipa.detail", result.DetailKey);
        var facts = (IpAddressFacts)result.Facts!;
        Assert.True(facts.IsApipa);
        Assert.True(facts.DhcpEnabled);
    }

    [Fact]
    public async Task A_normal_address_is_ok_and_publishes_gateway_and_dns_for_the_later_steps()
    {
        IPAddress dnsV6 = IPAddress.Parse("2001:db8::53");
        var ip = new FakeIpProperties(
            unicast: [(Address, Mask)],
            gateways: [IPAddress.Any, Gateway],
            dns: [Gateway, dnsV6],
            dhcp: false);
        StepContext context = Context(ip);

        StepResult result = await new IpAddressCheck().RunAsync(context, CancellationToken.None);

        Assert.Equal(StepState.Ok, result.State);
        Assert.Equal("step.ip.ok", result.HeadlineKey);
        Assert.Equal(Address, result.HeadlineArgs![0]);
        Assert.Null(result.DetailKey);

        Assert.Equal(Address, context.LocalAddress);
        Assert.Equal(Gateway, context.Gateway);
        Assert.Equal([Gateway], context.DnsServers);
        Assert.Null(context.GlobalIpv6);
        Assert.Null(context.Vpn);

        var facts = (IpAddressFacts)result.Facts!;
        Assert.Equal(Mask, facts.SubnetMask);
        Assert.False(facts.DhcpEnabled);
        Assert.False(facts.IsApipa);

        Assert.Contains(result.Measurements, m => m.Value == Address.ToString());
        Assert.Contains(result.Measurements, m => m.Value == Mask.ToString());
        Assert.Contains(result.Measurements, m => m.Value == Gateway.ToString());
        Assert.Contains(result.Measurements, m => m.Label == "DHCP");
        Assert.Contains(result.Measurements, m => m.Label == "IPv6");
        Assert.DoesNotContain(result.Measurements, m => m.Label == "VPN");
    }

    [Fact]
    public async Task A_global_ipv6_address_is_published_as_its_prefix()
    {
        IPAddress v6 = IPAddress.Parse("2a02:a46b:1:2:bbbb:cccc:dddd:eeee");
        var ip = new FakeIpProperties(unicast: [(Address, Mask), (IPAddress.Parse("fe80::1"), null), (v6, null)]);
        StepContext context = Context(ip);

        StepResult result = await new IpAddressCheck().RunAsync(context, CancellationToken.None);

        Assert.Equal(v6, context.GlobalIpv6);
        Assert.Equal(v6, ((IpAddressFacts)result.Facts!).GlobalIpv6);
        Assert.Contains(result.Measurements, m => m.Value == "2a02:a46b:1:2::/64");
    }

    [Fact]
    public async Task An_adapter_that_cannot_tell_its_dhcp_state_counts_as_static()
    {
        var ip = new FakeIpProperties(unicast: [(Address, Mask)], noIpv4: true);

        StepResult result = await new IpAddressCheck().RunAsync(Context(ip), CancellationToken.None);

        Assert.Equal(StepState.Ok, result.State);
        Assert.False(((IpAddressFacts)result.Facts!).DhcpEnabled);
    }

    [Fact]
    public async Task A_vpn_adapter_that_is_up_is_reported_beside_the_address()
    {
        var vpn = new FakeAdapter("vpn", "WireGuard Tunnel", "WireGuard Tunnel", NetworkInterfaceType.Ppp,
            ip: new FakeIpProperties(unicast: [(IPAddress.Parse("10.8.0.2"), null)], index: 4_000_001));
        var ip = new FakeIpProperties(unicast: [(Address, Mask)], gateways: [Gateway]);
        StepContext context = Context(ip, [vpn]);

        StepResult result = await new IpAddressCheck().RunAsync(context, CancellationToken.None);

        VpnInfo? found = ((IpAddressFacts)result.Facts!).Vpn;
        Assert.NotNull(found);
        Assert.Equal("WireGuard Tunnel", found.Name);
        Assert.Same(found, context.Vpn);
        Assert.Contains(result.Measurements, m => m.Label == "VPN" && m.Value.Contains("WireGuard Tunnel", StringComparison.Ordinal));
    }
}
