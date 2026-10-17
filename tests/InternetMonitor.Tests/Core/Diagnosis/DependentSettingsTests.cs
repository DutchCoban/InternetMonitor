using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Diagnosis;

/// <summary>Network-health monitoring is on when a condition is on, and the detailed-measurements switch adds measurements at the Extended level.</summary>
[Collection("Localization")]
public sealed class DependentSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"im-log-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Monitoring_is_on_exactly_when_a_condition_is_on()
    {
        var settings = new AppSettings();
        Assert.False(settings.NetworkHealthMonitoringEnabled);
        Assert.Empty(settings.EnabledNetworkHealthConditions);

        settings.PacketLoss = new NetworkHealthThreshold(true, 20, 5);
        Assert.True(settings.NetworkHealthMonitoringEnabled);
        Assert.Equal(["packetLoss"], settings.EnabledNetworkHealthConditions);
    }

    [Theory]
    [InlineData(DiagnosticLogLevel.Extended, false, false)]
    [InlineData(DiagnosticLogLevel.Extended, true, true)]
    [InlineData(DiagnosticLogLevel.Full, false, true)]
    public void Detailed_measurements_follow_the_level_and_the_switch(DiagnosticLogLevel level, bool detailed, bool expectMeasurements)
    {
        var settings = new AppSettings
        {
            DiagnosticLogLevel = level,
            LogDetailedMeasurements = detailed,
            LogSuccessfulChecks = true,
            DiagnosticLogDirectory = _directory,
        };
        var logger = new DiagnosticLogger(settings);
        var result = new StepResult(
            StepId.Adapter, StepState.Ok, "WiFi is present", null, 1, TimeSpan.Zero, DateTimeOffset.UtcNow,
            [new Measurement("Adapter", "WiFi")],
            new AdapterFacts(true, true, "WiFi", null, null, null, null, true, false, 1));
        var snapshot = new StepSnapshot([result], DateTimeOffset.UtcNow);

        logger.LogCycle(snapshot, RootCauseAnalyzer.Analyze(snapshot), null);

        string log = File.ReadAllText(logger.LogPath);
        Assert.Contains("WiFi is present", log);
        Assert.Equal(expectMeasurements, log.Contains("\"Adapter\":\"WiFi\""));
    }
}
