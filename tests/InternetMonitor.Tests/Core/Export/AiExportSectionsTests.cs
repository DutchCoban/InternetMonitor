using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Export;

/// <summary>The sections of the AI export that only appear with the matching data: each one renders what it is given.</summary>
[Collection("Localization")]
public sealed class AiExportSectionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 20, 0, 0, TimeSpan.Zero);

    private static string Export(IReadOnlyList<Incident> incidents, Dictionary<string, IReadOnlyList<MetricPoint>> history, AppSettings? settings = null) =>
        AiExportBuilder.Build(incidents, history, settings ?? new AppSettings(), "2.3.0", TimeZoneInfo.Utc, Now);

    [Fact]
    public void An_incident_shows_its_explanation_note_route_changes_and_steps()
    {
        var incident = new Incident
        {
            Id = "INC-20261017-190000.000",
            Cause = RootCause.Internet,
            StartUtc = Now.AddHours(-1),
            EndUtc = Now.AddMinutes(-50),
            Status = IncidentStatus.Resolved,
            Headline = "No public address reachable",
            Explanation = new Explanation("Nothing beyond the router answered.", "The line to the provider.", "Restart the modem."),
            AffectedSteps = [StepId.PublicIp, StepId.Dns],
            Note = "Provider ticket 4711, engineer on Monday",
            RecentChanges = ["Gateway changed from 10.0.0.1 to 10.0.0.2"],
            StepsAtStart = [new StepSnapshotEntry(StepId.PublicIp, StepState.Failed, "No public address reachable", "none answered", new Dictionary<string, string> { ["Latency"] = "- ms" })],
            StepsAtResolution = [new StepSnapshotEntry(StepId.PublicIp, StepState.Ok, "Reachable (9 ms)", null, new Dictionary<string, string>())],
            Traceroute = new IncidentTraceroute("9.9.9.9", Now.AddHours(-1), false, [new IncidentHop(1, "10.0.0.1", "router.lan", null, 1.2), new IncidentHop(2, null, null, null, null)]),
        };

        string md = Export([incident], []);

        Assert.Contains("- What happened: Nothing beyond the router answered.", md);
        Assert.Contains("- Likely cause: The line to the provider.", md);
        Assert.Contains("- Affected steps: PublicIp, Dns", md);
        Assert.Contains("- User's note: Provider ticket 4711, engineer on Monday", md);
        Assert.Contains("- Route to 9.9.9.9 when it opened (did not reach the target):", md);
        Assert.Contains("10.0.0.1", md);
        Assert.Contains("- Changed in the 30 minutes before (local time):", md);
        Assert.Contains("Gateway changed from 10.0.0.1 to 10.0.0.2", md);
        Assert.Contains("- PublicIp: Failed - No public address reachable - none answered", md);
        Assert.Contains("    - Latency: - ms", md);
        Assert.Contains("- Steps when it resolved: PublicIp Ok", md);
    }

    [Fact]
    public void Packet_loss_jitter_traffic_and_warnings_render_their_tables()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricPoint>>
        {
            [SeriesNames.PacketLoss] = [.. Enumerable.Range(0, 30).Select(i => new MetricPoint(Now.AddMinutes(-i), i % 10 == 0 ? 10 : 0, null))],
            [SeriesNames.Jitter] = [.. Enumerable.Range(0, 30).Select(i => new MetricPoint(Now.AddMinutes(-i), 4 + (i % 3), null))],
            [SeriesNames.TrafficDown] = [.. Enumerable.Range(0, 20).Select(i => new MetricPoint(Now.AddMinutes(-i), 5 + i, null))],
            [SeriesNames.TrafficUp] = [.. Enumerable.Range(0, 20).Select(i => new MetricPoint(Now.AddMinutes(-i), 1, null))],
            [SeriesNames.SpeedTestDownload] = [new MetricPoint(Now.AddMinutes(-5), 100, null)],
            ["internet:9.9.9.9"] = [new MetricPoint(Now.AddMinutes(-5), 12, null)],
            ["time"] = [new MetricPoint(Now.AddMinutes(-5), 0.2, null)],
        };
        var warning = new Incident { Id = "INC-1", Cause = RootCause.NetworkHealth, StartUtc = Now.AddMinutes(-30), Headline = "High packet loss", ConditionKey = "packetLoss" };
        var settings = new AppSettings { PacketLoss = new NetworkHealthThreshold(true, 20, 5) };

        string md = Export([warning], history, settings);

        Assert.Contains("- Average packet loss:", md);
        Assert.Contains("| 2026-10-17 |", md);
        Assert.Contains("- Download: average", md);
        Assert.Contains("- Samples above 70% of the last measured download speed:", md);
        Assert.Contains("- packetLoss: 1 time(s), most recently 2026-10-17 19:30:00", md);
        Assert.Contains("| Ping to 9.9.9.9 (v1) | ms |", md);
        Assert.Contains("| Time sync (v1) | s (clock offset) |", md);
        Assert.Contains("Network-health monitoring: packetLoss", md);
    }

    [Fact]
    public void Configured_endpoints_and_the_subscription_appear_in_the_context()
    {
        var settings = new AppSettings { SubscribedDownloadMbps = 500, SubscribedUploadMbps = 50 };
        settings.ApplicationEndpoints.Add(new EndpointConfig("a", "My site", EndpointKind.Url, Url: "https://example.test"));
        settings.ApplicationEndpoints.Add(new EndpointConfig("b", "Off", EndpointKind.Url, Url: "https://off.test", Enabled: false));

        string md = Export([], [], settings);

        Assert.Contains("- User-configured endpoints (step 7): 1: My site", md);
        Assert.Contains("- Subscription speed: 500 Mbit/s down, 50 Mbit/s up", md);
    }
}
