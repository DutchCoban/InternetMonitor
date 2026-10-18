using System.Net;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Reports;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

/// <summary>
/// The network-change monitor over made-up readings: the case it was written for (cable adapter
/// loses DHCP, Windows moves to Wi-Fi, the cable adapter sits on 169.254) and the rules that keep
/// it quiet - a baseline first, no "none to value", a VPN re-baselines, a disabled kind closes.
/// </summary>
[Collection("Localization")]
public sealed class NetworkChangeMonitorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 10, 0, 0, TimeSpan.Zero);
    private const string Eth = "{eth}";
    private const string Wifi = "{wifi}";
    private const string Usb = "{usb}";

    private static readonly IPAddress EthAddress = IPAddress.Parse("192.168.1.23");
    private static readonly IPAddress EthGateway = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress WifiAddress = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress WifiGateway = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress Apipa = IPAddress.Parse("169.254.17.4");
    private static readonly IPAddress Mask24 = IPAddress.Parse("255.255.255.0");

    private static AdapterReading Ethernet(IPAddress? address = null, bool up = true, bool dhcp = true, bool noAddress = false)
    {
        IPAddress? held = noAddress ? null : address ?? EthAddress;
        bool apipa = held is not null && InterfaceSelector.IsApipa(held);
        IPAddress? gateway = held is null || apipa ? null : EthGateway;
        return new AdapterReading(Eth, "Ethernet", false, false, up, held, held is null ? null : Mask24, apipa, dhcp, gateway, gateway);
    }

    private static readonly NetworkChangeOptions ApipaOnly = new(false, true, false, false);

    private static AdapterReading WiFi(bool up = true) =>
        new(Wifi, "Wi-Fi", true, false, up, up ? WifiAddress : null, Mask24, false, true, up ? WifiGateway : null, up ? WifiGateway : null);

    /// <summary>A reading with the primary adapter's network facts taken from that adapter.</summary>
    private static NetworkReading Reading(DateTimeOffset at, string? primary, string? gatewayMac = null, IReadOnlyList<IPAddress>? dns = null, string? ipv6 = null, string? ssid = null, bool vpn = false, params AdapterReading[] adapters)
    {
        AdapterReading? primaryAdapter = adapters.FirstOrDefault(a => a.Id == primary);
        return new NetworkReading(at, primary, primaryAdapter?.Name, adapters, primaryAdapter?.Gateway, gatewayMac, dns ?? (primaryAdapter?.Gateway is { } g ? [g] : []), ipv6, ssid, vpn);
    }

    private static (IncidentStore Store, NetworkChangeMonitor Monitor, List<Incident> Opened, List<Incident> Closed) Fresh()
    {
        var store = new IncidentStore();
        store.Clear();
        var monitor = new NetworkChangeMonitor(store, new ChangeDetector());
        var opened = new List<Incident>();
        var closed = new List<Incident>();
        monitor.IncidentOpened += opened.Add;
        monitor.IncidentClosed += closed.Add;
        return (store, monitor, opened, closed);
    }

    // ---- The case --------------------------------------------------------------------------------

    [Fact]
    public void The_cable_adapter_losing_dhcp_and_windows_moving_to_wifi_is_two_incidents_that_close_when_it_is_over()
    {
        (IncidentStore store, NetworkChangeMonitor monitor, List<Incident> opened, List<Incident> closed) = Fresh();

        // Normal: both adapters up, traffic over Ethernet.
        monitor.Observe(Reading(T0, Eth, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        Assert.Empty(opened);

        // The DHCP server stops answering; Windows moves to Wi-Fi; Ethernet gets 169.254.
        monitor.Observe(Reading(T0.AddSeconds(15), Wifi, adapters: [Ethernet(Apipa), WiFi()]), NetworkChangeOptions.All);
        Incident path = Assert.Single(opened);
        Assert.Equal(RootCause.NetworkChange, path.Cause);
        Assert.Equal(NetworkChangeMonitor.PathChanged, path.ConditionKey);
        Assert.Equal(Eth, path.Subject);
        Assert.Equal("Connection moved from Ethernet (192.168.1.23 via 192.168.1.1) to Wi-Fi (10.0.0.5 via 10.0.0.1)", path.Headline);
        Assert.Equal(IncidentStatus.Active, path.Status);
        Assert.Contains("Windows now sends this computer's traffic over Wi-Fi instead of Ethernet.", path.Explanation!.What);

        // A second reading on 169.254 opens the APIPA incident, with the address it had before.
        monitor.Observe(Reading(T0.AddSeconds(30), Wifi, adapters: [Ethernet(Apipa), WiFi()]), NetworkChangeOptions.All);
        Assert.Equal(2, opened.Count);
        Incident apipa = opened[1];
        Assert.Equal(NetworkChangeMonitor.Apipa, apipa.ConditionKey);
        Assert.Equal(Eth, apipa.Subject);
        Assert.Equal("Ethernet got an automatic private address 169.254.17.4: no DHCP server answered", apipa.Headline);
        Assert.Contains($"Until {T0.ToLocalTime():HH:mm:ss} this adapter had 192.168.1.23 from DHCP server 192.168.1.1.", apipa.Explanation!.LikelyCause);
        Assert.Equal(OutageSide.HomeNetwork, NetworkChangeMonitor.SideOf(apipa.ConditionKey));
        Assert.Equal(OutageSide.ThisComputer, NetworkChangeMonitor.SideOf(path.ConditionKey));

        // Ethernet gets its lease back, but Windows keeps routing over Wi-Fi: said once.
        monitor.Observe(Reading(T0.AddMinutes(20), Wifi, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddMinutes(21), Wifi, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        Assert.Equal(apipa, Assert.Single(closed));
        Assert.Equal("Ethernet has an address again: 192.168.1.23", apipa.Timeline[^1].Text);
        Assert.Equal(T0.AddMinutes(20), apipa.EndUtc);
        Assert.Equal("Ethernet has an address again (192.168.1.23) but traffic still goes over Wi-Fi (10.0.0.5 via 10.0.0.1)", Assert.Single(path.Timeline, t => t.Text.Contains("still goes over", StringComparison.Ordinal)).Text);

        // Back on Ethernet: the detour is over, and its duration is on record.
        monitor.Observe(Reading(T0.AddMinutes(33), Eth, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        Assert.Equal(2, closed.Count);
        Assert.Equal(IncidentStatus.Resolved, path.Status);
        Assert.Equal(T0.AddMinutes(33), path.EndUtc);
        Assert.Equal("Back on Ethernet after 32m 45s", path.Timeline[^1].Text);
        Assert.Equal(2, store.All.Count);
    }

    // ---- Path changes ----------------------------------------------------------------------------

    [Fact]
    public void The_first_reading_is_only_a_baseline_and_nothing_opens_without_a_known_previous_adapter()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, _) = Fresh();

        monitor.Observe(Reading(T0, Wifi, adapters: [WiFi()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddSeconds(15), null, adapters: [WiFi(up: false)]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddSeconds(30), Eth, adapters: [Ethernet(), WiFi(up: false)]), NetworkChangeOptions.All);

        Assert.Empty(opened);
    }

    [Fact]
    public void A_move_whose_original_adapter_goes_away_closes_as_standing()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, List<Incident> closed) = Fresh();
        monitor.Observe(Reading(T0, Eth, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddSeconds(15), Wifi, adapters: [Ethernet(up: false, noAddress: true), WiFi()]), NetworkChangeOptions.All);
        Assert.Single(opened);

        // Still down a cycle later: the cable was pulled on purpose, say.
        monitor.Observe(Reading(T0.AddMinutes(1), Wifi, adapters: [WiFi()]), NetworkChangeOptions.All);

        Incident incident = Assert.Single(closed);
        Assert.Equal("Ethernet is no longer connected; the move stands", incident.Timeline[^1].Text);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
    }

    [Fact]
    public void A_third_adapter_is_a_timeline_entry_on_the_open_move_not_a_new_incident()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, List<Incident> closed) = Fresh();
        var usb = new AdapterReading(Usb, "USB Ethernet", false, false, true, IPAddress.Parse("172.16.0.9"), Mask24, false, true, IPAddress.Parse("172.16.0.1"), IPAddress.Parse("172.16.0.1"));
        monitor.Observe(Reading(T0, Eth, adapters: [Ethernet(), WiFi(), usb]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddSeconds(15), Wifi, adapters: [Ethernet(Apipa), WiFi(), usb]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddSeconds(30), Usb, adapters: [Ethernet(Apipa), WiFi(), usb]), NetworkChangeOptions.All);

        Incident incident = Assert.Single(opened, i => i.ConditionKey == NetworkChangeMonitor.PathChanged);
        Assert.Equal("Moved on to USB Ethernet (172.16.0.9 via 172.16.0.1)", incident.Timeline[^1].Text);
        Assert.DoesNotContain(closed, i => i.ConditionKey == NetworkChangeMonitor.PathChanged);

        monitor.Observe(Reading(T0.AddMinutes(5), Eth, adapters: [Ethernet(), WiFi(), usb]), NetworkChangeOptions.All);
        Assert.Contains(closed, i => i.ConditionKey == NetworkChangeMonitor.PathChanged);
    }

    [Fact]
    public void Switching_a_kind_off_closes_its_open_incident_and_opens_no_new_one()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, List<Incident> closed) = Fresh();
        monitor.Observe(Reading(T0, Eth, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddSeconds(15), Wifi, adapters: [Ethernet(Apipa), WiFi()]), NetworkChangeOptions.All);
        Assert.Single(opened);

        NetworkChangeOptions off = NetworkChangeOptions.All with { PathChanged = false, Apipa = false };
        monitor.Observe(Reading(T0.AddSeconds(30), Wifi, adapters: [Ethernet(Apipa), WiFi()]), off);
        monitor.Observe(Reading(T0.AddSeconds(45), Wifi, adapters: [Ethernet(Apipa), WiFi()]), off);

        Incident incident = Assert.Single(closed);
        Assert.Equal("Closed: this kind of change is no longer recorded (Settings)", incident.Timeline[^1].Text);
        Assert.Single(opened);
    }

    [Fact]
    public void A_restart_resumes_an_open_move_and_an_open_apipa_incident_and_closes_them_by_what_they_are_about()
    {
        (IncidentStore store, NetworkChangeMonitor first, _, _) = Fresh();
        first.Observe(Reading(T0, Eth, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        first.Observe(Reading(T0.AddSeconds(15), Wifi, adapters: [Ethernet(Apipa), WiFi()]), NetworkChangeOptions.All);
        first.Observe(Reading(T0.AddSeconds(30), Wifi, adapters: [Ethernet(Apipa), WiFi()]), NetworkChangeOptions.All);
        Assert.Equal(2, store.All.Count(i => i.Status == IncidentStatus.Active));

        var resumed = new NetworkChangeMonitor(new IncidentStore(), new ChangeDetector());
        var closed = new List<Incident>();
        resumed.IncidentClosed += closed.Add;

        // The very first reading after the restart already judges them: Ethernet is back in every sense.
        resumed.Observe(Reading(T0.AddMinutes(10), Eth, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);

        Assert.Equal(2, closed.Count);
        Assert.Contains(closed, i => i.ConditionKey == NetworkChangeMonitor.PathChanged && i.Timeline[^1].Text.StartsWith("Back on Ethernet", StringComparison.Ordinal));
        Assert.Contains(closed, i => i.ConditionKey == NetworkChangeMonitor.Apipa && i.Timeline[^1].Text == "Ethernet has an address again: 192.168.1.23");
    }

    // ---- APIPA -----------------------------------------------------------------------------------

    [Fact]
    public void Apipa_needs_two_readings_and_ignores_the_primary_adapter_virtual_adapters_and_static_addressing()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, _) = Fresh();
        var virtualSwitch = new AdapterReading("{vm}", "vEthernet (Default Switch)", false, true, true, IPAddress.Parse("169.254.80.1"), Mask24, true, true, null, null);
        monitor.Observe(Reading(T0, Wifi, adapters: [Ethernet(), WiFi(), virtualSwitch]), ApipaOnly);

        // One reading: not yet. The virtual switch is on 169.254 the whole time and never counts.
        monitor.Observe(Reading(T0.AddSeconds(15), Wifi, adapters: [Ethernet(Apipa), WiFi(), virtualSwitch]), ApipaOnly);
        Assert.Empty(opened);

        // A good reading in between resets the count.
        monitor.Observe(Reading(T0.AddSeconds(30), Wifi, adapters: [Ethernet(), WiFi(), virtualSwitch]), ApipaOnly);
        monitor.Observe(Reading(T0.AddSeconds(45), Wifi, adapters: [Ethernet(Apipa), WiFi(), virtualSwitch]), ApipaOnly);
        Assert.Empty(opened);

        // Static addressing (DHCP off) with a 169.254 address is a choice, not a fault.
        monitor.Observe(Reading(T0.AddMinutes(1), Wifi, adapters: [Ethernet(Apipa, dhcp: false), WiFi(), virtualSwitch]), ApipaOnly);
        monitor.Observe(Reading(T0.AddMinutes(1).AddSeconds(15), Wifi, adapters: [Ethernet(Apipa, dhcp: false), WiFi(), virtualSwitch]), ApipaOnly);
        Assert.Empty(opened);

        // The primary adapter on 169.254 is step 3's incident, not this one's.
        monitor.Observe(Reading(T0.AddMinutes(2), Eth, adapters: [Ethernet(Apipa), WiFi(up: false)]), ApipaOnly);
        monitor.Observe(Reading(T0.AddMinutes(2).AddSeconds(15), Eth, adapters: [Ethernet(Apipa), WiFi(up: false)]), ApipaOnly);
        Assert.Empty(opened);

        monitor.Observe(Reading(T0.AddMinutes(3), Wifi, adapters: [Ethernet(Apipa), WiFi()]), ApipaOnly);
        monitor.Observe(Reading(T0.AddMinutes(3).AddSeconds(15), Wifi, adapters: [Ethernet(Apipa), WiFi()]), ApipaOnly);
        Assert.Single(opened);
    }

    [Fact]
    public void An_apipa_incident_closes_when_the_adapter_is_disconnected()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, List<Incident> closed) = Fresh();
        monitor.Observe(Reading(T0, Wifi, adapters: [Ethernet(), WiFi()]), ApipaOnly);
        monitor.Observe(Reading(T0.AddSeconds(15), Wifi, adapters: [Ethernet(Apipa), WiFi()]), ApipaOnly);
        monitor.Observe(Reading(T0.AddSeconds(30), Wifi, adapters: [Ethernet(Apipa), WiFi()]), ApipaOnly);
        Assert.Single(opened);

        monitor.Observe(Reading(T0.AddMinutes(1), Wifi, adapters: [Ethernet(up: false, noAddress: true), WiFi()]), ApipaOnly);

        Incident incident = Assert.Single(closed);
        Assert.Equal("Ethernet is no longer connected", incident.Timeline[^1].Text);
        Assert.Equal(T0.AddMinutes(1), incident.EndUtc);
    }

    // ---- The network's settings and the gateway's hardware ----------------------------------------

    [Fact]
    public void A_changed_gateway_dns_or_dhcp_server_on_the_same_adapter_is_a_moment_with_old_and_new()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, List<Incident> closed) = Fresh();
        IPAddress[] oldDns = [IPAddress.Parse("192.168.1.1")];
        IPAddress[] newDns = [IPAddress.Parse("192.168.2.1"), IPAddress.Parse("9.9.9.9")];
        monitor.Observe(Reading(T0, Eth, dns: oldDns, adapters: [Ethernet()]), NetworkChangeOptions.All);

        var moved = new AdapterReading(Eth, "Ethernet", false, false, true, IPAddress.Parse("192.168.2.23"), Mask24, false, true, IPAddress.Parse("192.168.2.1"), IPAddress.Parse("192.168.2.1"));
        monitor.Observe(Reading(T0.AddMinutes(1), Eth, dns: newDns, adapters: [moved]), NetworkChangeOptions.All);

        Incident incident = Assert.Single(opened);
        Assert.Equal(NetworkChangeMonitor.NetworkChanged, incident.ConditionKey);
        Assert.Equal("Network changed on Ethernet: gateway 192.168.1.1 → 192.168.2.1; network 192.168.1.0/24 → 192.168.2.0/24; DNS servers 192.168.1.1 → 192.168.2.1, 9.9.9.9; DHCP server 192.168.1.1 → 192.168.2.1", incident.Headline);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal(incident.StartUtc, incident.EndUtc);
        Assert.Equal(T0.AddMinutes(1), incident.StartUtc);
        Assert.Empty(closed);

        // Steady afterwards: no repeat.
        monitor.Observe(Reading(T0.AddMinutes(2), Eth, dns: newDns, adapters: [moved]), NetworkChangeOptions.All);
        Assert.Single(opened);
    }

    [Fact]
    public void A_value_appearing_or_disappearing_a_vpn_flip_or_an_adapter_switch_is_not_a_network_change()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, _) = Fresh();
        monitor.Observe(Reading(T0, Eth, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);

        // No IPv6 prefix, then one: connecting, not changing. Then a different one with a VPN flip: re-baseline.
        monitor.Observe(Reading(T0.AddMinutes(1), Eth, ipv6: "2a02:1::/64", adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddMinutes(2), Eth, ipv6: "2a02:2::/64", vpn: true, dns: [IPAddress.Parse("10.8.0.1")], adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        Assert.Empty(opened);

        // The adapter switch changes everything, and is the path incident's story alone.
        monitor.Observe(Reading(T0.AddMinutes(3), Wifi, adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        Assert.Equal(NetworkChangeMonitor.PathChanged, Assert.Single(opened).ConditionKey);

        // And a real prefix change on a steady adapter is one.
        monitor.Observe(Reading(T0.AddMinutes(4), Wifi, ipv6: "2a02:3::/64", adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddMinutes(5), Wifi, ipv6: "2a02:4::/64", adapters: [Ethernet(), WiFi()]), NetworkChangeOptions.All);
        Assert.Equal("Network changed on Wi-Fi: IPv6 prefix 2a02:3::/64 → 2a02:4::/64", opened[^1].Headline);
    }

    [Fact]
    public void The_gateway_answering_from_another_hardware_address_is_a_moment_and_an_unknown_address_is_not()
    {
        (_, NetworkChangeMonitor monitor, List<Incident> opened, _) = Fresh();
        monitor.Observe(Reading(T0, Eth, gatewayMac: "AA:BB:CC:00:00:01", adapters: [Ethernet()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddMinutes(1), Eth, gatewayMac: null, adapters: [Ethernet()]), NetworkChangeOptions.All);
        monitor.Observe(Reading(T0.AddMinutes(2), Eth, gatewayMac: "AA:BB:CC:00:00:01", adapters: [Ethernet()]), NetworkChangeOptions.All);
        Assert.Empty(opened);

        monitor.Observe(Reading(T0.AddMinutes(3), Eth, gatewayMac: "AA:BB:CC:00:00:02", adapters: [Ethernet()]), NetworkChangeOptions.All);

        Incident incident = Assert.Single(opened);
        Assert.Equal(NetworkChangeMonitor.GatewayHardwareChanged, incident.ConditionKey);
        Assert.Equal("The router at 192.168.1.1 now answers from hardware address AA:BB:CC:00:00:02 (was AA:BB:CC:00:00:01)", incident.Headline);
        Assert.Equal(incident.StartUtc, incident.EndUtc);
        Assert.Contains("AA:BB:CC:00:00:02", incident.Explanation!.WhatToDo);

        // Off: a further change is not recorded.
        monitor.Observe(Reading(T0.AddMinutes(4), Eth, gatewayMac: "AA:BB:CC:00:00:03", adapters: [Ethernet()]), NetworkChangeOptions.All with { GatewayHardware = false });
        Assert.Single(opened);
    }
}
