using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.Export;

[Collection("Localization")]
public sealed class AiExportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Network_names_and_hardware_addresses_are_replaced_by_stable_stand_ins()
    {
        var names = new Pseudonymizer();
        names.Learn([IncidentOn("HomeNet", "60:22:32:ef:f0:a3")], []);

        Assert.Equal("Wi-Fi network A", names.NetworkName("HomeNet"));
        Assert.Equal("Wi-Fi network A", names.Text("HomeNet"));
        Assert.Equal("AP-1", names.AccessPoint("60-22-32-EF-F0-A3"));
        Assert.Equal("Wi-Fi network changed from Wi-Fi network A to Wi-Fi network B", names.Text("Wi-Fi network changed from HomeNet to " + "Guest".Replace("Guest", names.NetworkName("Guest"))));
        Assert.Equal("MAC address MAC-1, again MAC-1", names.Text("MAC address 08:B4:D2:D2:27:79, again 08-b4-d2-d2-27-79"));
    }

    [Fact]
    public void Export_contains_the_providers_figures_and_the_incident_detail_but_no_identifiers()
    {
        Incident incident = IncidentOn("HomeNet", "60:22:32:ef:f0:a3");
        var history = new Dictionary<string, IReadOnlyList<MetricPoint>>
        {
            ["network"] = [.. Enumerable.Range(0, 240).Select(i => new MetricPoint(Now.AddHours(-1).AddSeconds(i * 15), 1, null))],
            ["internet"] = [.. Enumerable.Range(0, 240).Select(i => new MetricPoint(Now.AddHours(-1).AddSeconds(i * 15), 9, null))],
            [SeriesNames.WifiSignal] =
            [
                new MetricPoint(Now.AddMinutes(-30), 80, "60:22:32:ef:f0:a3"),
                new MetricPoint(Now.AddMinutes(-20), 40, "60:22:32:ef:f0:b7"),
            ],
            [SeriesNames.SpeedTestDownload] = [new MetricPoint(Now.AddMinutes(-10), 150, null)],
            [SeriesNames.SpeedTestUpload] = [new MetricPoint(Now.AddMinutes(-10), 30, null)],
            [SeriesNames.SpeedTestBufferbloat] = [new MetricPoint(Now.AddMinutes(-10), 175, null)],
        };

        string md = AiExportBuilder.Build([incident], history, new AppSettings { SubscribedDownloadMbps = 200, SubscribedUploadMbps = 40 }, "2.1.0", TimeZoneInfo.Utc, Now);

        Assert.Contains("## Instructions for the AI", md);
        Assert.Contains("Availability (provider-side outages only)", md);
        Assert.Contains("| Internet | provider | 1 |", md);
        Assert.Contains("## Latency per day", md);
        Assert.Contains("+175 ms (D)", md);
        Assert.Contains("75%", md);
        Assert.Contains("AP-1 -> AP-2", md);
        Assert.Contains("Network (SSID): Wi-Fi network A", md);
        Assert.Contains("Timeline:", md);

        Assert.DoesNotContain("HomeNet", md);
        Assert.DoesNotContain("60:22:32", md);
    }

    [Fact]
    public void Plain_export_has_the_same_data_without_the_instructions()
    {
        string plain = AiExportBuilder.BuildPlain([], new Dictionary<string, IReadOnlyList<MetricPoint>>(), new AppSettings(), now: Now);

        Assert.DoesNotContain("Instructions for the AI", plain);
        Assert.Contains("## How to read this data", plain);
    }

    [Fact]
    public void Fixed_terms_recorded_in_another_language_come_back_in_english()
    {
        Assert.Equal("Link speed", LocalizationManager.ToEnglish("Linksnelheid"));
        Assert.Equal("Disabled", LocalizationManager.ToEnglish("Uit"));
        Assert.Equal("HomeNet", LocalizationManager.ToEnglish("HomeNet"));
    }

    private static Incident IncidentOn(string ssid, string bssid)
    {
        var incident = new Incident
        {
            Id = "INC-20261014-0001",
            Cause = RootCause.Internet,
            StartUtc = Now.AddMinutes(-15),
            EndUtc = Now.AddMinutes(-14),
            Status = IncidentStatus.Resolved,
            Headline = "No public address reachable",
            StepsAtStart =
            [
                new StepSnapshotEntry(StepId.Link, StepState.Ok, "Connected to Wi-Fi", null, new Dictionary<string, string>
                {
                    ["Netwerk (SSID)"] = ssid,
                    ["Access point (BSSID)"] = bssid,
                }),
            ],
            RecentChanges = [$"19:44:00  Wi-Fi network changed from Guest to {ssid}"],
        };
        incident.Timeline.Add(new TimelineEntry(Now.AddMinutes(-15), "Incident opened"));
        return incident;
    }
}
