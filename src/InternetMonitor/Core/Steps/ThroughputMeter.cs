using System.Net.NetworkInformation;

namespace InternetMonitor.Core.Steps;

/// <summary>How busy this computer's connection was over the interval since the previous cycle.</summary>
/// <param name="Utilization">
/// The busier direction as a fraction of what the line can carry (0..1, can exceed 1 briefly), or
/// null when there is nothing to compare against.
/// </param>
public sealed record ThroughputSample(double DownMbps, double UpMbps, double? Utilization)
{
    /// <summary>At or above this the line is "busy": queues in the router start to fill, and latency rises with them.</summary>
    public const double BusyUtilization = 0.7;

    public bool Busy => Utilization >= BusyUtilization;
}

/// <summary>
/// Turns the adapter's byte counters into a traffic rate per cycle, passively - no extra traffic.
///
/// The point is to explain a latency spike. High latency on a line that was nearly full is
/// bufferbloat in the user's own router (fixable at home, nothing to do with the provider); high
/// latency on an idle line is not. Without this the two look identical.
///
/// "Full" is judged against the last speed test when there is one - the only measurement of what
/// the line can actually carry - and otherwise against the adapter's own link speed, which is an
/// upper bound and so errs towards "not busy". Only this computer's traffic is visible: another
/// device filling the line looks like an idle line from here.
/// </summary>
public sealed class ThroughputMeter
{
    private readonly object _lock = new();
    private readonly Func<long> _clockMs;

    private string? _adapterId;
    private long _received;
    private long _sent;
    private long _atMs;

    public ThroughputMeter(Func<long>? clockMs = null) => _clockMs = clockMs ?? (() => Environment.TickCount64);

    /// <summary>Download capacity from the latest speed test, in Mbit/s; null until one has run.</summary>
    public double? CapacityDownMbps { get; set; }

    /// <summary>Upload capacity from the latest speed test, in Mbit/s; null until one has run.</summary>
    public double? CapacityUpMbps { get; set; }

    public ThroughputSample? Measure(NetworkInterface adapter)
    {
        try
        {
            IPInterfaceStatistics stats = adapter.GetIPStatistics();
            double? linkMbps = adapter.Speed > 0 ? adapter.Speed / 1_000_000.0 : null;
            return Measure(adapter.Id, stats.BytesReceived, stats.BytesSent, linkMbps);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The counter arithmetic, separate from reading the counters so it can be tested. Returns null
    /// for the first reading on an adapter (nothing to diff against yet), after a switch to another
    /// adapter, after a counter reset, and for intervals too short to give a meaningful rate.
    /// </summary>
    public ThroughputSample? Measure(string adapterId, long bytesReceived, long bytesSent, double? linkMbps)
    {
        lock (_lock)
        {
            long now = _clockMs();
            bool sameAdapter = adapterId == _adapterId;
            long elapsedMs = now - _atMs;
            long receivedDelta = bytesReceived - _received;
            long sentDelta = bytesSent - _sent;

            (_adapterId, _received, _sent, _atMs) = (adapterId, bytesReceived, bytesSent, now);

            if (!sameAdapter || elapsedMs < 1000 || receivedDelta < 0 || sentDelta < 0)
            {
                return null;
            }

            double seconds = elapsedMs / 1000.0;
            double down = receivedDelta * 8 / 1_000_000.0 / seconds;
            double up = sentDelta * 8 / 1_000_000.0 / seconds;

            double? downCapacity = CapacityDownMbps ?? linkMbps;
            double? upCapacity = CapacityUpMbps ?? linkMbps;
            double? utilization = downCapacity is > 0 || upCapacity is > 0
                ? Math.Max(
                    downCapacity is > 0 ? down / downCapacity.Value : 0,
                    upCapacity is > 0 ? up / upCapacity.Value : 0)
                : null;

            return new ThroughputSample(down, up, utilization);
        }
    }
}
