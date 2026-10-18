using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.History;

/// <summary>This computer's traffic over one recording interval: the average rate across it and the fastest single cycle inside it.</summary>
internal readonly record struct TrafficInterval(double AvgDownMbps, double AvgUpMbps, double PeakDownMbps, double PeakUpMbps, double Seconds);

/// <summary>
/// Folds the per-cycle traffic rates between two stored history points into one point.
///
/// With a window open the pipeline cycles every few seconds but the history keeps one point per
/// fifteen; storing whichever cycle happened to land on the quarter minute would miss a download
/// that ran in between. Each cycle's rate is weighted by its own length, so the mean is the byte
/// count over the whole interval divided by its length - the same number a single long reading
/// would have given - and the fastest cycle is kept beside it as the interval's peak. Cycles never
/// overlap, so no lock is needed.
/// </summary>
internal sealed class TrafficAccumulator
{
    private double _seconds;
    private double _downMbpsSeconds;
    private double _upMbpsSeconds;
    private double _peakDown;
    private double _peakUp;

    /// <summary>Adds one cycle's reading. A sample without a known interval counts as one second, i.e. a plain average.</summary>
    public void Add(ThroughputSample sample)
    {
        double seconds = sample.IntervalSeconds > 0 ? sample.IntervalSeconds : 1;
        _seconds += seconds;
        _downMbpsSeconds += sample.DownMbps * seconds;
        _upMbpsSeconds += sample.UpMbps * seconds;
        _peakDown = Math.Max(_peakDown, sample.DownMbps);
        _peakUp = Math.Max(_peakUp, sample.UpMbps);
    }

    /// <summary>The interval so far, or null when nothing has been added since the last reset.</summary>
    public TrafficInterval? Current => _seconds > 0
        ? new TrafficInterval(_downMbpsSeconds / _seconds, _upMbpsSeconds / _seconds, _peakDown, _peakUp, _seconds)
        : null;

    /// <summary>Starts a new interval: after a point was stored, or after a cycle without a reading (an adapter switch, a counter reset).</summary>
    public void Reset()
    {
        _seconds = 0;
        _downMbpsSeconds = 0;
        _upMbpsSeconds = 0;
        _peakDown = 0;
        _peakUp = 0;
    }
}
