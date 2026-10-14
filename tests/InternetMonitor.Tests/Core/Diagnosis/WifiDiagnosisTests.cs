using System.Net.NetworkInformation;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Diagnosis;

[Collection("Localization")]
public sealed class WifiDiagnosisTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_roam_on_the_same_network_is_reported_once_as_another_access_point()
    {
        var detector = new ChangeDetector();
        detector.Observe(Snapshot(Start, Wifi("HeBo", "aa:00:00:00:00:01", 80)));

        IReadOnlyList<EnvironmentChange> found = detector.Observe(Snapshot(Start.AddSeconds(15), Wifi("HeBo", "aa:00:00:00:00:02", 78)));

        EnvironmentChange change = Assert.Single(found);
        Assert.Equal(ChangeKind.WifiAccessPoint, change.Kind);
        Assert.Equal("Switched to another Wi-Fi access point", change.DescribeEnglish());
    }

    [Fact]
    public void A_different_network_is_reported_as_such_and_not_also_as_a_roam()
    {
        var detector = new ChangeDetector();
        detector.Observe(Snapshot(Start, Wifi("HeBo", "aa:00:00:00:00:01", 80)));

        EnvironmentChange change = Assert.Single(detector.Observe(Snapshot(Start.AddSeconds(15), Wifi("Guest", "bb:00:00:00:00:01", 80))));

        Assert.Equal(ChangeKind.WifiNetwork, change.Kind);
        Assert.Equal("Wi-Fi network changed from HeBo to Guest", change.DescribeEnglish());
    }

    [Fact]
    public void A_sharp_signal_drop_is_reported_once_but_ordinary_wobble_is_not()
    {
        var detector = new ChangeDetector();
        detector.Observe(Snapshot(Start, Wifi("HeBo", "aa:00:00:00:00:01", 82)));
        Assert.Empty(detector.Observe(Snapshot(Start.AddSeconds(15), Wifi("HeBo", "aa:00:00:00:00:01", 70))));

        EnvironmentChange drop = Assert.Single(detector.Observe(Snapshot(Start.AddSeconds(30), Wifi("HeBo", "aa:00:00:00:00:01", 31))));
        Assert.Equal("Wi-Fi signal dropped from 82% to 31%", drop.DescribeEnglish());

        Assert.Empty(detector.Observe(Snapshot(Start.AddSeconds(45), Wifi("HeBo", "aa:00:00:00:00:01", 25))));
    }

    [Fact]
    public void Withheld_wifi_details_are_not_a_change()
    {
        var detector = new ChangeDetector();
        detector.Observe(Snapshot(Start, Wifi("HeBo", "aa:00:00:00:00:01", 80)));

        Assert.Empty(detector.Observe(Snapshot(Start.AddSeconds(15), WifiInfo.Denied)));
        Assert.Empty(detector.Observe(Snapshot(Start.AddSeconds(30), Wifi("HeBo", "aa:00:00:00:00:01", 80))));
    }

    [Fact]
    public void A_gateway_failure_over_a_weak_signal_blames_the_signal()
    {
        StepSnapshot snapshot = Snapshot(Start, Wifi("HeBo", "aa:00:00:00:00:01", 22), gateway: StepState.Failed);

        Explanation explanation = PlainLanguage.DescribeEnglish(RootCause.Gateway, snapshot);

        Assert.Contains("only 22%", explanation.LikelyCause);
    }

    [Fact]
    public void A_gateway_failure_over_a_good_signal_keeps_the_general_explanation()
    {
        StepSnapshot snapshot = Snapshot(Start, Wifi("HeBo", "aa:00:00:00:00:01", 85), gateway: StepState.Failed);

        Explanation explanation = PlainLanguage.DescribeEnglish(RootCause.Gateway, snapshot);

        Assert.DoesNotContain("Wi-Fi signal", explanation.LikelyCause);
    }

    private static WifiInfo Wifi(string ssid, string bssid, int signal) =>
        new(ssid, bssid, signal, -60, 48, 5240, "Wi-Fi 6", 287, 287, AccessDenied: false);

    private static StepSnapshot Snapshot(DateTimeOffset at, WifiInfo wifi, StepState gateway = StepState.Ok)
    {
        StepResult R(StepId id, StepState state, StepFacts? facts = null) =>
            new(id, state, id.ToString(), null, null, TimeSpan.Zero, at, [], facts);

        return new StepSnapshot(
        [
            R(StepId.Adapter, StepState.Ok),
            R(StepId.Link, StepState.Ok, new LinkFacts(PhysicalLinkState.Connected, OperationalStatus.Up, true, "WiFi", wifi)),
            R(StepId.IpAddress, StepState.Ok),
            R(StepId.Gateway, gateway),
            R(StepId.PublicIp, StepState.Ok),
            R(StepId.Dns, StepState.Ok),
            R(StepId.Target, StepState.Ok),
        ], at);
    }
}
