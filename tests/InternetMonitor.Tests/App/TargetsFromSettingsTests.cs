using InternetMonitor.Configuration;
using Xunit;

namespace InternetMonitor.Tests.App;

/// <summary>The check targets a cycle gets from the settings: thresholds, the adapter choice, and every enabled endpoint mapped to its kind.</summary>
public sealed class TargetsFromSettingsTests
{
    [Fact]
    public void Enabled_endpoints_are_added_after_the_built_in_targets_with_their_ids()
    {
        var defaults = new CheckTargets();
        var settings = new AppSettings
        {
            ApplicationEndpoints =
            [
                new EndpointConfig("u1", "API", EndpointKind.Url, Url: "https://api.example/health", ExpectedStatusCode: 204, Enabled: true, Headers: [new EndpointHeader("X-Key", "k")]),
                new EndpointConfig("t1", "Mail", EndpointKind.TcpPort, Host: "mail.example", Port: 993, Enabled: true),
                new EndpointConfig("p1", "Printer", EndpointKind.Ping, Host: "192.168.1.9", Enabled: true),
                new EndpointConfig("off", "Old", EndpointKind.Url, Url: "https://old.example/", Enabled: false),
            ],
        };

        CheckTargets targets = TargetsFromSettings.Build(settings, defaults);

        Assert.Equal(defaults.Targets.Count + 3, targets.Targets.Count);
        Assert.Equal(defaults.Targets, targets.Targets.Take(defaults.Targets.Count));

        TargetEndpoint api = targets.Targets.Single(t => t.Id == "u1");
        Assert.Equal(TargetKind.Url, api.Kind);
        Assert.Equal("https://api.example/health", api.Url);
        Assert.Equal(204, api.ExpectedStatusCode);
        Assert.Equal("X-Key", Assert.Single(api.Headers).Name);

        TargetEndpoint mail = targets.Targets.Single(t => t.Id == "t1");
        Assert.Equal(TargetKind.TcpPort, mail.Kind);
        Assert.Equal(("mail.example", 993), (mail.Host, mail.Port));

        TargetEndpoint printer = targets.Targets.Single(t => t.Id == "p1");
        Assert.Equal(TargetKind.Ping, printer.Kind);
        Assert.Equal("192.168.1.9", printer.Host);

        Assert.DoesNotContain(targets.Targets, t => t.Id == "off");
    }

    [Fact]
    public void Thresholds_and_the_adapter_choice_come_from_the_settings_and_empty_names_become_null()
    {
        var settings = new AppSettings
        {
            LatencyErrorMs = 2000,
            LatencyWarningMs = 400,
            WeakWifiSignalPercent = 35,
            AdapterMode = AdapterMode.Fixed,
            AdapterId = "{adapter-id}",
            AdapterName = string.Empty,
        };

        CheckTargets targets = TargetsFromSettings.Build(settings, new CheckTargets());

        Assert.Equal((400, 2000), (targets.LatencyWarningMs, targets.LatencyErrorMs));
        Assert.Equal(35, targets.WeakWifiSignalPercent);
        Assert.Equal(AdapterMode.Fixed, targets.AdapterMode);
        Assert.Equal("{adapter-id}", targets.AdapterId);
        Assert.Null(targets.AdapterName);
        Assert.Equal(settings.ParsedPublicIpTargets(), targets.PublicIpTargets);
        Assert.Equal(settings.ParsedCustomDnsServers(), targets.CustomDnsServers);

        CheckTargets automatic = TargetsFromSettings.Build(new AppSettings(), new CheckTargets());
        Assert.Null(automatic.AdapterId);
        Assert.Equal(AdapterMode.Automatic, automatic.AdapterMode);
    }
}
