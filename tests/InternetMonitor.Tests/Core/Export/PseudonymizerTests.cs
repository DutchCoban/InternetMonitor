using System.Net;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Export;

/// <summary>
/// What the AI export may and may not say about the household: a public address of this computer
/// becomes a stand-in, everything that identifies nobody stays, and a network name is only ever
/// replaced as a whole word.
/// </summary>
[Collection("Localization")]
public sealed class PseudonymizerTests
{
    private static Incident IncidentWith(Dictionary<string, string> measurements) => new()
    {
        Id = "INC-20261017-0001",
        Cause = RootCause.Internet,
        StartUtc = DateTimeOffset.UtcNow,
        Status = IncidentStatus.Active,
        Headline = "No public address reachable",
        StepsAtStart = [new StepSnapshotEntry(StepId.IpAddress, StepState.Ok, "203.0.113.5", null, measurements)],
    };

    [Fact]
    public void A_public_address_of_this_computer_becomes_a_stable_stand_in()
    {
        var names = new Pseudonymizer();

        Assert.Equal("IP-1", names.Measurement("IP address", "203.0.113.5"));
        Assert.Equal("IP-1", names.Measurement("IP-adres", "203.0.113.5"));
        Assert.Equal("IP-2", names.Measurement("Gateway", "203.0.113.1"));
        Assert.Equal("Address IP-1 via IP-2, later IP-3", names.Text("Address 203.0.113.5 via 203.0.113.1, later 198.51.100.7"));
    }

    [Fact]
    public void Addresses_that_identify_nobody_stay_as_they_are()
    {
        var names = new Pseudonymizer();

        Assert.Equal("192.168.1.23", names.Measurement("IP address", "192.168.1.23"));
        Assert.Equal("10.0.0.1", names.Measurement("Gateway", "10.0.0.1"));
        Assert.Equal("100.64.3.9", names.Measurement("IP address", "100.64.3.9"));
        Assert.Equal("169.254.7.7", names.Measurement("IP address", "169.254.7.7"));
        Assert.Equal("2001:db8::1", names.Measurement("IP address", "2001:db8::1"));
        Assert.Equal("gateway 192.168.1.1 answered in 2 ms", names.Text("gateway 192.168.1.1 answered in 2 ms"));
        Assert.Equal("not an address: 1.2.3.4.5 or 300.1.1.1", names.Text("not an address: 1.2.3.4.5 or 300.1.1.1"));
    }

    [Fact]
    public void The_apps_own_targets_and_the_adapters_dns_servers_stay_readable()
    {
        var names = new Pseudonymizer([IPAddress.Parse("9.9.9.9"), IPAddress.Parse("1.1.1.1")]);
        names.Learn([IncidentWith(new Dictionary<string, string> { ["DNS servers"] = "195.121.1.34, 195.121.1.66" })], []);

        Assert.Equal("9.9.9.9 unreachable, 1.1.1.1 answered", names.Text("9.9.9.9 unreachable, 1.1.1.1 answered"));
        Assert.Equal("DNS servers changed from 195.121.1.34 to IP-1", names.Text("DNS servers changed from 195.121.1.34 to 203.0.113.53"));
        Assert.Equal("195.121.1.34, 195.121.1.66", names.Measurement("DNS servers", "195.121.1.34, 195.121.1.66"));
    }

    [Fact]
    public void A_learned_public_address_gets_the_same_stand_in_in_measurements_and_text()
    {
        var names = new Pseudonymizer();
        names.Learn([IncidentWith(new Dictionary<string, string> { ["IP address"] = "203.0.113.5", ["Gateway"] = "203.0.113.1" })], []);

        Assert.Equal("IP address changed from IP-3 to IP-1 (gateway IP-2)", names.Text("IP address changed from 203.0.113.4 to 203.0.113.5 (gateway 203.0.113.1)"));
        Assert.Equal("IP-1", names.Measurement("IP address", "203.0.113.5"));
    }

    [Fact]
    public void A_network_name_is_replaced_only_as_a_whole_word()
    {
        var names = new Pseudonymizer();
        names.NetworkName("home");

        Assert.Equal("Wi-Fi network A", names.Text("home"));
        Assert.Equal("Switched to Wi-Fi network A; homework stays", names.Text("Switched to home; homework stays"));
        Assert.Equal("my homework", names.Text("my homework"));
        Assert.Equal("(Wi-Fi network A)", names.Text("(home)"));
    }

    [Fact]
    public void A_network_name_with_regex_characters_or_spaces_is_matched_literally()
    {
        var names = new Pseudonymizer();
        names.NetworkName("Cafe (5G) *fast*");

        Assert.Equal("now on Wi-Fi network A!", names.Text("now on Cafe (5G) *fast*!"));
        Assert.Equal("Cafe 5G fast", names.Text("Cafe 5G fast"));
    }

    [Fact]
    public void A_blank_network_name_is_ignored_rather_than_breaking_the_export()
    {
        var names = new Pseudonymizer();
        names.Learn([IncidentWith(new Dictionary<string, string> { ["Network (SSID)"] = string.Empty })], []);

        Assert.Equal(string.Empty, names.NetworkName(string.Empty));
        Assert.Equal("   ", names.NetworkName("   "));
        Assert.Equal("nothing changes here", names.Text("nothing changes here"));
    }
}
