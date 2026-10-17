using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Tests.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>
/// Steps 1 and 2 over made-up adapters: the one Settings fixed on, one that is gone, a wireless
/// one, and the link state read from the managed status when the interface table has nothing.
/// </summary>
[Collection("Localization")]
public sealed class AdapterCheckTests
{
    private static FakeIpProperties Ip() =>
        new(unicast: [(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("255.255.255.0"))], gateways: [IPAddress.Parse("192.168.1.1")]);

    private static StepContext Fixed(string id, string name = "") =>
        new(new CheckTargets { AdapterMode = AdapterMode.Fixed, AdapterId = id, AdapterName = name.Length > 0 ? name : null });

    [Fact]
    public async Task The_fixed_adapter_is_described_and_handed_to_the_later_steps()
    {
        FakeIpProperties ip = Ip();
        var ethernet = new FakeAdapter("eth-id", "Ethernet", "Intel(R) Ethernet Connection", speed: 2_500_000_000, ip: ip);
        var other = new FakeAdapter("wifi-id", "Wi-Fi", type: NetworkInterfaceType.Wireless80211, status: OperationalStatus.Down);
        StepContext context = Fixed("eth-id");

        StepResult result = await new AdapterCheck(() => [ethernet, other]).RunAsync(context, CancellationToken.None);

        Assert.Equal(StepState.Ok, result.State);
        Assert.Equal("step.adapter.ok", result.HeadlineKey);
        Assert.Equal(["Ethernet"], result.HeadlineArgs!);
        Assert.Equal(1, result.ChartValue);

        var facts = (AdapterFacts)result.Facts!;
        Assert.True(facts.AnyAdapterPresent);
        Assert.True(facts.Enabled);
        Assert.Equal(("Ethernet", "Intel(R) Ethernet Connection"), (facts.Name, facts.Description));
        Assert.Equal(NetworkInterfaceType.Ethernet, facts.Type);
        Assert.Equal(2500, facts.SpeedMbps);
        Assert.False(facts.IsWireless);
        Assert.False(facts.IsLikelyVirtual);
        Assert.Equal(2, facts.CandidateCount);

        Assert.Same(ethernet, context.Adapter);
        Assert.Same(ip, context.IpProperties);
        Assert.Equal(2, context.AllAdapters!.Count);
        Assert.Contains(result.Measurements, m => m.Value == "2500 Mbit/s");
        Assert.Contains(result.Measurements, m => m.Value == "Intel(R) Ethernet Connection");
    }

    [Fact]
    public async Task A_fixed_adapter_windows_no_longer_lists_is_reported_by_its_remembered_name()
    {
        var wifi = new FakeAdapter("wifi-id", "Wi-Fi", type: NetworkInterfaceType.Wireless80211, ip: Ip());

        StepResult byName = await new AdapterCheck(() => [wifi]).RunAsync(Fixed("usb-id", "USB Ethernet"), CancellationToken.None);
        Assert.Equal(StepState.Failed, byName.State);
        Assert.Equal("step.adapter.chosenMissing", byName.HeadlineKey);
        Assert.Equal(["USB Ethernet"], byName.HeadlineArgs!);
        Assert.Equal("step.adapter.chosenMissing.detail", byName.DetailKey);
        Assert.Equal(0, byName.ChartValue);
        var facts = (AdapterFacts)byName.Facts!;
        Assert.True(facts.AnyAdapterPresent);
        Assert.False(facts.Enabled);
        Assert.Equal(1, facts.CandidateCount);

        StepResult byId = await new AdapterCheck(() => []).RunAsync(Fixed("usb-id"), CancellationToken.None);
        Assert.Equal(["usb-id"], byId.HeadlineArgs!);
        Assert.False(((AdapterFacts)byId.Facts!).AnyAdapterPresent);
    }

    [Fact]
    public async Task A_wireless_adapter_is_called_wifi_and_a_virtual_one_is_flagged()
    {
        var wifi = new FakeAdapter("wifi-id", "Wi-Fi", "Intel(R) Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, ip: Ip());
        StepResult wireless = await new AdapterCheck(() => [wifi]).RunAsync(Fixed("wifi-id"), CancellationToken.None);
        Assert.True(((AdapterFacts)wireless.Facts!).IsWireless);
        Assert.Contains(wireless.Measurements, m => m.Value == "Wi-Fi");

        var hyperv = new FakeAdapter("v-id", "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", speed: 0, ip: Ip());
        StepResult virtualOne = await new AdapterCheck(() => [hyperv]).RunAsync(Fixed("v-id"), CancellationToken.None);
        Assert.True(((AdapterFacts)virtualOne.Facts!).IsLikelyVirtual);
        Assert.Null(((AdapterFacts)virtualOne.Facts!).SpeedMbps);
    }

    [Fact]
    public async Task The_link_step_fails_without_an_adapter_and_follows_the_managed_status_when_the_table_has_nothing()
    {
        StepResult none = await new LinkCheck().RunAsync(new StepContext(new CheckTargets()) { Adapter = null }, CancellationToken.None);
        Assert.Equal(StepState.Failed, none.State);
        Assert.Equal("step.link.noAdapter", none.HeadlineKey);

        // An interface index Windows does not have, so the interface table has nothing to say
        // and the step falls back to the managed status - the case under test.
        var nowhere = new FakeIpProperties(unicast: [(IPAddress.Parse("192.168.1.20"), null)], index: 4_000_010);
        var up = new FakeAdapter("eth-id", "Ethernet", ip: nowhere);
        StepContext upContext = new(new CheckTargets()) { Adapter = up };
        StepResult connected = await new LinkCheck().RunAsync(upContext, CancellationToken.None);
        Assert.Equal(StepState.Ok, connected.State);
        var facts = (LinkFacts)connected.Facts!;
        Assert.Equal(OperationalStatus.Up, facts.ManagedStatus);
        Assert.Equal("Ethernet", facts.AdapterName);
        Assert.False(facts.IsWireless);
        Assert.Null(facts.Wifi);

        var down = new FakeAdapter("eth-id", "Ethernet", status: OperationalStatus.Down, ip: nowhere);
        StepResult unplugged = await new LinkCheck().RunAsync(new StepContext(new CheckTargets()) { Adapter = down }, CancellationToken.None);
        Assert.Equal(StepState.Failed, unplugged.State);
        Assert.Equal(0, unplugged.ChartValue);
    }
}
