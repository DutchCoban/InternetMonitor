using System.Globalization;
using InternetMonitor.Configuration;

namespace InternetMonitor.Core.History;

public readonly record struct MetricPoint(DateTimeOffset Timestamp, double Value, string? Metadata);

/// <summary>
/// The rolling history behind every chart: kept in memory, persisted to
/// %AppData%\InternetMonitor\history\&lt;series&gt;.csv, pruned to a configurable retention window.
///
/// The on-disk format is deliberately identical to v1's - "utcTicks,value[,metadata]", one file
/// per series, filename percent-encoded - and the series names for the four latency measurements
/// are v1's probe ids. A user upgrading keeps their charts instead of starting from an empty
/// graph; see <see cref="SeriesNames"/> for the mapping and what it costs.
///
/// Two performance properties are inherited from v1 and are not incidental:
///
///  - <see cref="Record"/> is a lock-and-append with no pruning and no disk I/O. It runs on the
///    completion path of every step of every cycle, and must stay cheap no matter how large
///    history has grown. At a long retention across many series the lists reach six figures, and
///    rescanning one on each append would be wasted work at exactly the wrong moment.
///  - Pruning and flushing happen on a background loop. <see cref="Get"/> can therefore return a
///    few minutes of already-expired points, which is invisible in a chart and worth the
///    trade.
///
/// What is new in v2 is <see cref="Sample"/>. The pipeline now runs five times as often when a
/// window is open, and letting that drive history would make retention and chart density depend on
/// whether someone happened to be looking. Sample enforces a minimum spacing per series, so the
/// extra foreground cycles update the live display and nothing else.
/// </summary>
public sealed class MetricStore : IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(2);

    private readonly string _directory;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<MetricPoint>> _series = [];

    /// <summary>
    /// The last copy <see cref="Get(string)"/> handed out per series, reused until the series
    /// changes. A chart or an export asks for a whole series at a time; copying six figures of
    /// points for every such call, when nothing has been added since the last one, is what this saves.
    /// </summary>
    private readonly Dictionary<string, MetricPoint[]> _snapshots = [];
    private readonly Dictionary<string, DateTimeOffset> _lastFlushed = [];
    private readonly Dictionary<string, DateTimeOffset> _lastSampled = [];
    private readonly Task _loadTask;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _flushLoop;

    private TimeSpan _retention;

    public MetricStore()
        : this(TimeSpan.FromDays(30))
    {
    }

    /// <summary>
    /// Takes the retention up front rather than only through <see cref="SetRetention"/>: loading
    /// starts here and prunes as it reads, so a retention set afterwards would come too late for
    /// the files already read - a 90-day history would be cut to the default on every start.
    /// </summary>
    public MetricStore(TimeSpan retention)
        : this(retention, AppPaths.HistoryDir)
    {
    }

    /// <summary>A store over any directory - the tests' way to keep each one apart; the app always uses <see cref="AppPaths.HistoryDir"/>.</summary>
    internal MetricStore(TimeSpan retention, string directory)
    {
        _retention = retention;
        _directory = directory;
        _loadTask = Task.Run(LoadAllAsync);
        _flushLoop = RunFlushLoopAsync(_shutdown.Token);
    }

    /// <summary>The minimum spacing between recorded points for one series. See the class summary.</summary>
    public TimeSpan MinimumSampleInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Completes once the files on disk have been read. Live charts can start drawing before that
    /// and fill in as history arrives, but anything that summarises the whole period - a report,
    /// an export - has to wait for it, or it would summarise a fraction of the data.
    /// </summary>
    public Task Loaded => _loadTask;

    public void SetRetention(TimeSpan retention)
    {
        lock (_lock)
        {
            _retention = retention;
        }
    }

    /// <summary>
    /// Records a point if enough time has passed since the last one for this series. Returns true
    /// when it was kept, so a caller can tell whether a chart needs refreshing.
    /// </summary>
    public bool Sample(string seriesId, DateTimeOffset timestamp, double value, string? metadata = null)
    {
        lock (_lock)
        {
            if (_lastSampled.TryGetValue(seriesId, out DateTimeOffset last)
                && timestamp - last < MinimumSampleInterval)
            {
                return false;
            }

            _lastSampled[seriesId] = timestamp;
        }

        Record(seriesId, timestamp, value, metadata);
        return true;
    }

    public void Record(string seriesId, DateTimeOffset timestamp, double value, string? metadata = null)
    {
        lock (_lock)
        {
            if (!_series.TryGetValue(seriesId, out List<MetricPoint>? points))
            {
                points = [];
                _series[seriesId] = points;
            }

            points.Add(new MetricPoint(timestamp, value, metadata));
            _snapshots.Remove(seriesId);
        }
    }

    /// <summary>Every series that has data, including ones no chart shows (v1's per-address pings, for example).</summary>
    public IReadOnlyList<string> SeriesIds
    {
        get
        {
            lock (_lock)
            {
                return [.. _series.Where(s => s.Value.Count > 0).Select(s => s.Key).Order(StringComparer.Ordinal)];
            }
        }
    }

    /// <summary>A series as it is now. The list is shared between callers and never changes; a new one is made after the next change.</summary>
    public IReadOnlyList<MetricPoint> Get(string seriesId) => Snapshot(seriesId);

    /// <summary>The points at or after <paramref name="from"/>, as a view over the current snapshot - no copy.</summary>
    public IReadOnlyList<MetricPoint> Get(string seriesId, DateTimeOffset from)
    {
        MetricPoint[] snapshot = Snapshot(seriesId);

        // Points are in time order, so the window is a tail slice found by binary search rather
        // than by filtering a six-figure list.
        int start = LowerBound(snapshot, from);
        return new ArraySegment<MetricPoint>(snapshot, start, snapshot.Length - start);
    }

    private MetricPoint[] Snapshot(string seriesId)
    {
        lock (_lock)
        {
            if (_snapshots.TryGetValue(seriesId, out MetricPoint[]? snapshot))
            {
                return snapshot;
            }

            snapshot = _series.TryGetValue(seriesId, out List<MetricPoint>? points) ? [.. points] : [];
            _snapshots[seriesId] = snapshot;
            return snapshot;
        }
    }

    public void Clear(string seriesId)
    {
        lock (_lock)
        {
            _series.Remove(seriesId);
            _snapshots.Remove(seriesId);
            _lastFlushed.Remove(seriesId);
            _lastSampled.Remove(seriesId);
        }

        try
        {
            File.Delete(PathFor(seriesId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the in-memory clear is what the UI reflects immediately, and an
            // orphaned file is re-read as expired on the next start.
        }
    }

    /// <summary>Binary search for the first point after <paramref name="until"/>; the list is in time order.</summary>
    public static int UpperBound(IReadOnlyList<MetricPoint> points, DateTimeOffset until)
    {
        int low = 0;
        int high = points.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (points[mid].Timestamp <= until)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>Binary search for the first point at or after <paramref name="from"/>; the list is in time order.</summary>
    public static int LowerBound(IReadOnlyList<MetricPoint> points, DateTimeOffset from)
    {
        int low = 0;
        int high = points.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (points[mid].Timestamp < from)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private string PathFor(string seriesId) => Path.Combine(_directory, Uri.EscapeDataString(seriesId) + ".csv");

    private async Task LoadAllAsync()
    {
        string[] files;
        try
        {
            Directory.CreateDirectory(_directory);
            files = Directory.GetFiles(_directory, "*.csv");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string file in files)
        {
            string seriesId = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file));
            List<MetricPoint> points;
            try
            {
                points = await ReadAndCompactAsync(file).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                continue;
            }

            lock (_lock)
            {
                if (_series.TryGetValue(seriesId, out List<MetricPoint>? existing))
                {
                    // Record() may already have appended for this series while the file was being
                    // read. Everything on disk was flushed by a previous run, so it is strictly
                    // older than anything added this run - prepending keeps the order correct
                    // whichever finished first.
                    existing.InsertRange(0, points);
                }
                else
                {
                    _series[seriesId] = points;
                }

                _snapshots.Remove(seriesId);

                _lastFlushed[seriesId] = points.Count > 0 ? points[^1].Timestamp : DateTimeOffset.MinValue;
            }
        }
    }

    /// <summary>
    /// Reads one series file, dropping malformed lines and points older than the retention. When
    /// anything was dropped the file is rewritten, so it does not keep growing between starts -
    /// the flush loop only ever appends.
    /// </summary>
    private async Task<List<MetricPoint>> ReadAndCompactAsync(string path)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _retention;
        var points = new List<MetricPoint>();
        int lines = 0;

        // Streamed rather than read into one string array first: a 90-day series is half a
        // million lines, and holding every line as a string next to the points it becomes would
        // double the memory the load needs at its peak.
        await foreach (string line in File.ReadLinesAsync(path).ConfigureAwait(false))
        {
            lines++;
            if (TryParseLine(line, out MetricPoint point) && point.Timestamp >= cutoff)
            {
                points.Add(point);
            }
        }

        points.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        if (points.Count != lines)
        {
            await WriteAllAsync(path, points).ConfigureAwait(false);
        }

        // The list grew by doubling while reading; give the slack back, since this series will now
        // only ever grow by a point every few seconds.
        points.TrimExcess();
        return points;
    }

    /// <summary>
    /// Reads one "ticks,value[,metadata]" line. False for anything that is not one - including a
    /// tick count outside what a <see cref="DateTimeOffset"/> can hold, which would otherwise throw
    /// and take the whole load down with it. A damaged line is simply not a point.
    /// </summary>
    public static bool TryParseLine(string line, out MetricPoint point)
    {
        point = default;

        int firstComma = line.IndexOf(',');
        if (firstComma < 0
            || !long.TryParse(line.AsSpan(0, firstComma), out long ticks)
            || ticks < DateTimeOffset.MinValue.Ticks
            || ticks > DateTimeOffset.MaxValue.Ticks)
        {
            return false;
        }

        int secondComma = line.IndexOf(',', firstComma + 1);
        ReadOnlySpan<char> valueSpan = secondComma < 0
            ? line.AsSpan(firstComma + 1)
            : line.AsSpan(firstComma + 1, secondComma - firstComma - 1);

        if (!double.TryParse(valueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return false;
        }

        point = new MetricPoint(new DateTimeOffset(ticks, TimeSpan.Zero), value, secondComma < 0 ? null : line[(secondComma + 1)..]);
        return true;
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        // Load before flush is an ordering invariant, not a coincidence of timing: a flush that
        // ran before the load had merged a series' file would compute the wrong "new since last
        // flush" slice for it.
        await _loadTask.ConfigureAwait(false);

        try
        {
            await Monitors.BackgroundLoop.EveryAsync(FlushInterval, _ => FlushAsync(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task FlushAsync()
    {
        var pending = new List<(string SeriesId, List<MetricPoint> Points)>();

        lock (_lock)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - _retention;
            foreach ((string seriesId, List<MetricPoint> points) in _series)
            {
                DateTimeOffset since = _lastFlushed.GetValueOrDefault(seriesId, DateTimeOffset.MinValue);
                int firstNew = points.FindIndex(p => p.Timestamp > since);
                if (firstNew >= 0)
                {
                    pending.Add((seriesId, points.GetRange(firstNew, points.Count - firstNew)));
                }

                // Expired points are a prefix of a time-ordered list: find where it ends and drop
                // it in one move, rather than testing every point in the series once a minute.
                int expired = LowerBound(points, cutoff);
                if (expired > 0)
                {
                    points.RemoveRange(0, expired);
                    _snapshots.Remove(seriesId);
                }
            }
        }

        foreach ((string seriesId, List<MetricPoint> points) in pending)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                await File.AppendAllLinesAsync(PathFor(seriesId), ToLines(points)).ConfigureAwait(false);

                lock (_lock)
                {
                    _lastFlushed[seriesId] = points[^1].Timestamp;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Persistence must never break monitoring. The watermark only advances on success,
                // so this slice is retried on the next flush.
            }
        }
    }

    private static Task WriteAllAsync(string path, List<MetricPoint> points) =>
        File.WriteAllLinesAsync(path, ToLines(points));

    private static IEnumerable<string> ToLines(List<MetricPoint> points) =>
        points.Select(p => p.Metadata is { Length: > 0 } metadata
            ? $"{p.Timestamp.UtcTicks},{p.Value.ToString(CultureInfo.InvariantCulture)},{metadata}"
            : $"{p.Timestamp.UtcTicks},{p.Value.ToString(CultureInfo.InvariantCulture)}");

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await _flushLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        // One final unconditional flush, so exiting does not lose whatever was recorded since the
        // last periodic tick.
        await FlushAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
