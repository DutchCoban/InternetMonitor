using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Core.Tools.SpeedTest;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

public sealed class ThroughputTests
{
    [Fact]
    public void First_reading_has_nothing_to_compare_against()
    {
        var meter = new ThroughputMeter(() => 0);
        Assert.Null(meter.Measure("wifi", 1_000, 1_000, 100));
    }

    [Fact]
    public void Rate_is_bits_per_second_over_the_elapsed_interval()
    {
        long now = 0;
        var meter = new ThroughputMeter(() => now);
        meter.Measure("wifi", 0, 0, null);

        now = 4_000;
        ThroughputSample sample = meter.Measure("wifi", 50_000_000, 5_000_000, null)!;

        Assert.Equal(100, sample.DownMbps, 3);
        Assert.Equal(10, sample.UpMbps, 3);
    }

    [Fact]
    public void Utilization_is_judged_against_the_speed_test_when_known_else_the_link()
    {
        long now = 0;
        var meter = new ThroughputMeter(() => now);
        meter.Measure("wifi", 0, 0, 1000);
        now = 1_000;

        // 80 Mbit/s against a 1 Gbit/s link: quiet.
        Assert.False(meter.Measure("wifi", 10_000_000, 0, 1000)!.Busy);

        // The same 80 Mbit/s against a measured 100 Mbit/s line: busy.
        meter.CapacityDownMbps = 100;
        now = 2_000;
        ThroughputSample sample = meter.Measure("wifi", 20_000_000, 0, 1000)!;
        Assert.Equal(0.8, sample.Utilization!.Value, 3);
        Assert.True(sample.Busy);
    }

    [Fact]
    public void Switching_adapter_or_a_counter_reset_starts_over()
    {
        long now = 0;
        var meter = new ThroughputMeter(() => now);
        meter.Measure("wifi", 5_000, 5_000, null);

        now = 2_000;
        Assert.Null(meter.Measure("ethernet", 9_000, 9_000, null));

        now = 4_000;
        Assert.Null(meter.Measure("ethernet", 100, 100, null));
    }

    [Theory]
    [InlineData(2, "A")]
    [InlineData(29, "B")]
    [InlineData(45, "C")]
    [InlineData(150, "D")]
    [InlineData(400, "F")]
    public void Bufferbloat_grade(double increaseMs, string grade) => Assert.Equal(grade, SpeedTestResult.Grade(increaseMs));

    [Fact]
    public void Loaded_increase_takes_the_worse_direction_and_needs_a_baseline()
    {
        Assert.Equal(88, new SpeedTestResult(10, 1, 100, 20, IdlePingMs: 12, LoadedPingDownloadMs: 40, LoadedPingUploadMs: 100).LoadedIncreaseMs);
        Assert.Null(new SpeedTestResult(10, 1, 100, 20, IdlePingMs: null, LoadedPingDownloadMs: 40).LoadedIncreaseMs);
        Assert.Null(new SpeedTestResult(10, 1, 100, 20, IdlePingMs: 12).LoadedIncreaseMs);
    }

    [Fact]
    public void Latency_warning_names_a_busy_line()
    {
        var context = new StepContext(new CheckTargets()) { Throughput = new ThroughputSample(85, 3, 0.85) };
        (string key, object[] args) = LatencyClassifier.WarningDetail(context);
        Assert.Equal("step.latency.busy", key);
        Assert.Equal(["85", "3"], args);

        context.Throughput = new ThroughputSample(2, 0.1, 0.02);
        Assert.Equal("step.latency.high", LatencyClassifier.WarningDetail(context).Key);
    }
}
