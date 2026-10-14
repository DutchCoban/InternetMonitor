using InternetMonitor.Core.History;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.Tools.SpeedTest;

/// <summary>
/// The one place a finished speed test is written down, whether it ran on schedule or because
/// someone pressed the button - so History, the exports and the provider report never depend on
/// which path a result came from.
///
/// It also hands the measured speeds to the <see cref="ThroughputMeter"/>: a speed test is the only
/// measurement of what the line can actually carry, which is what "the line was busy" is judged
/// against.
/// </summary>
public static class SpeedTestRecorder
{
    public static void Record(MetricStore history, ThroughputMeter meter, SpeedTestResult result, DateTimeOffset at)
    {
        history.Sample(SeriesNames.SpeedTestDownload, at, result.DownloadMbps);
        history.Sample(SeriesNames.SpeedTestUpload, at, result.UploadMbps);

        if (result.PingMs > 0)
        {
            history.Sample(SeriesNames.SpeedTestPing, at, result.PingMs);
        }

        if (result.LoadedIncreaseMs is { } increase)
        {
            history.Sample(SeriesNames.SpeedTestBufferbloat, at, increase);
        }

        ApplyCapacity(meter, result.DownloadMbps, result.UploadMbps);
    }

    /// <summary>Restores the line capacity from the last recorded speed test, so "busy" means something from the first cycle after a restart.</summary>
    public static void RestoreCapacity(MetricStore history, ThroughputMeter meter)
    {
        IReadOnlyList<MetricPoint> down = history.Get(SeriesNames.SpeedTestDownload);
        IReadOnlyList<MetricPoint> up = history.Get(SeriesNames.SpeedTestUpload);
        ApplyCapacity(meter, down.Count > 0 ? down[^1].Value : 0, up.Count > 0 ? up[^1].Value : 0);
    }

    private static void ApplyCapacity(ThroughputMeter meter, double downMbps, double upMbps)
    {
        // A failed or aborted test measures next to nothing; taking that as the line's capacity
        // would make every cycle afterwards look like a saturated line.
        if (downMbps >= 1)
        {
            meter.CapacityDownMbps = downMbps;
        }

        if (upMbps >= 1)
        {
            meter.CapacityUpMbps = upMbps;
        }
    }
}
