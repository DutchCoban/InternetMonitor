using System.Globalization;

namespace InternetMonitor.Network.Diagnosis;

/// <summary>
/// Rolling history of each probe's chartable value, kept in memory and persisted to
/// %AppData%\InternetMonitor\history\&lt;probe-id&gt;.csv (one file per probe, filename
/// percent-encoded via <see cref="Uri.EscapeDataString(string)"/> since probe ids like
/// "endpoint:{guid}"/"internet:{address}" contain ':', which Windows filenames can't) so history
/// survives an app restart. Retention is user-configurable (see
/// AppSettings.HistoryRetentionMinutes / DiagnosticsCoordinator.UpdateHistoryRetention) -
/// <see cref="SetRetention"/> changes it live.
///
/// <see cref="Record"/> is a pure, lock-only append with no pruning and no disk I/O - it runs on
/// the probe-completion hot path (every probe, every cycle) and must stay cheap regardless of how
/// large history has grown (at a long retention window across many probes, lists can reach well
/// into six figures - rescanning one on every single append, like the previous 15-minute-only
/// design did, would be wasted work at that scale). Pruning and disk persistence both happen off
/// that path, on a periodic background loop, so <see cref="Get"/> can occasionally return a few
/// minutes of already-expired data - an acceptable, unnoticeable staleness for a UI chart.
/// </summary>
public sealed class ProbeHistory : IAsyncDisposable
{
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(2);

    private readonly string _historyDir;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<(DateTimeOffset Timestamp, double Value)>> _byProbe = new();
    private readonly Dictionary<string, DateTimeOffset> _lastFlushedTimestamp = new();
    private TimeSpan _window = DefaultWindow;

    private readonly Task _loadTask;
    private readonly CancellationTokenSource _flushCts = new();
    private readonly Task _flushLoopTask;

    public ProbeHistory()
    {
        _historyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetMonitor", "history");
        _loadTask = Task.Run(LoadAllFromDiskAsync);
        _flushLoopTask = RunFlushLoopAsync(_flushCts.Token);
    }

    /// <summary>Changes the retention window live. Takes effect on the next background prune pass (up to ~2 minutes) - not retroactive for anything already pruned.</summary>
    public void SetRetention(TimeSpan window)
    {
        lock (_lock)
        {
            _window = window;
        }
    }

    public void Record(string probeId, DateTimeOffset timestamp, double value)
    {
        lock (_lock)
        {
            if (!_byProbe.TryGetValue(probeId, out List<(DateTimeOffset Timestamp, double Value)>? list))
            {
                list = [];
                _byProbe[probeId] = list;
            }

            list.Add((timestamp, value));
        }
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, double Value)> Get(string probeId)
    {
        lock (_lock)
        {
            return _byProbe.TryGetValue(probeId, out List<(DateTimeOffset Timestamp, double Value)>? list)
                ? list.ToList()
                : [];
        }
    }

    public void Clear(string probeId)
    {
        lock (_lock)
        {
            _byProbe.Remove(probeId);
            _lastFlushedTimestamp.Remove(probeId);
        }

        try
        {
            File.Delete(PathForProbe(probeId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort - an orphaned file just means Clear() didn't fully take on disk; the
            // in-memory clear (what the UI actually reflects immediately) already happened above.
        }
    }

    private string PathForProbe(string probeId) => Path.Combine(_historyDir, Uri.EscapeDataString(probeId) + ".csv");

    private async Task LoadAllFromDiskAsync()
    {
        string[] files;
        try
        {
            Directory.CreateDirectory(_historyDir);
            files = Directory.GetFiles(_historyDir, "*.csv");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string file in files)
        {
            string probeId = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(file));
            List<(DateTimeOffset Timestamp, double Value)> diskPoints;
            try
            {
                diskPoints = await ReadAndCompactAsync(file).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                continue;
            }

            lock (_lock)
            {
                if (_byProbe.TryGetValue(probeId, out List<(DateTimeOffset Timestamp, double Value)>? existing))
                {
                    // Record() may already have appended fresh points for this probe while this
                    // file was being read. Disk points were all flushed by a *previous* run,
                    // before this process even started, so they're always strictly older than
                    // anything Record() could have added this run - prepending preserves
                    // ascending order correctly regardless of which finished first.
                    existing.InsertRange(0, diskPoints);
                }
                else
                {
                    _byProbe[probeId] = diskPoints;
                }

                _lastFlushedTimestamp[probeId] = diskPoints.Count > 0 ? diskPoints[^1].Timestamp : DateTimeOffset.MinValue;
            }
        }
    }

    /// <summary>
    /// Reads a probe's history file, filters to the current retention window, and rewrites it
    /// with just the retained lines - the only place disk-side compaction happens. A file can
    /// only grow past the window between app restarts (points recorded during a long-running
    /// session are never individually re-checked against the window on disk), self-correcting on
    /// the next one.
    /// </summary>
    private async Task<List<(DateTimeOffset Timestamp, double Value)>> ReadAndCompactAsync(string path)
    {
        string[] lines = await File.ReadAllLinesAsync(path).ConfigureAwait(false);
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - _window;
        var points = new List<(DateTimeOffset Timestamp, double Value)>(lines.Length);
        foreach (string line in lines)
        {
            int comma = line.IndexOf(',');
            if (comma < 0)
            {
                continue;
            }

            if (!long.TryParse(line.AsSpan(0, comma), out long ticks))
            {
                continue;
            }

            if (!double.TryParse(line.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }

            var timestamp = new DateTimeOffset(ticks, TimeSpan.Zero);
            if (timestamp >= cutoff)
            {
                points.Add((timestamp, value));
            }
        }

        points.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        if (points.Count != lines.Length)
        {
            await WriteAllAsync(path, points).ConfigureAwait(false);
        }

        return points;
    }

    private async Task RunFlushLoopAsync(CancellationToken cancellationToken)
    {
        // Load-then-flush is an explicit ordering invariant, not an accident of timing: without
        // this, a flush tick that somehow ran before the load finished merging disk-derived
        // points into _byProbe could compute the wrong "new since last flush" slice for a probe
        // whose file hadn't been read yet.
        await _loadTask.ConfigureAwait(false);

        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await FlushOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    private async Task FlushOnceAsync()
    {
        var toFlush = new List<(string ProbeId, List<(DateTimeOffset Timestamp, double Value)> NewPoints)>();
        lock (_lock)
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - _window;
            foreach ((string probeId, List<(DateTimeOffset Timestamp, double Value)> list) in _byProbe)
            {
                DateTimeOffset since = _lastFlushedTimestamp.GetValueOrDefault(probeId, DateTimeOffset.MinValue);
                int firstNew = list.FindIndex(p => p.Timestamp > since);
                if (firstNew >= 0)
                {
                    toFlush.Add((probeId, list.GetRange(firstNew, list.Count - firstNew)));
                }

                list.RemoveAll(p => p.Timestamp < cutoff);
            }
        }

        foreach ((string probeId, List<(DateTimeOffset Timestamp, double Value)> newPoints) in toFlush)
        {
            try
            {
                await AppendAsync(PathForProbe(probeId), newPoints).ConfigureAwait(false);
                lock (_lock)
                {
                    _lastFlushedTimestamp[probeId] = newPoints[^1].Timestamp;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Persistence must never break monitoring - retried next flush cycle since the
                // watermark only advances on success.
            }
        }
    }

    private async Task AppendAsync(string path, List<(DateTimeOffset Timestamp, double Value)> points)
    {
        Directory.CreateDirectory(_historyDir);
        await File.AppendAllLinesAsync(path, ToLines(points)).ConfigureAwait(false);
    }

    private static Task WriteAllAsync(string path, List<(DateTimeOffset Timestamp, double Value)> points) =>
        File.WriteAllLinesAsync(path, ToLines(points));

    private static IEnumerable<string> ToLines(List<(DateTimeOffset Timestamp, double Value)> points) =>
        points.Select(p => $"{p.Timestamp.UtcTicks},{p.Value.ToString(CultureInfo.InvariantCulture)}");

    public async ValueTask DisposeAsync()
    {
        _flushCts.Cancel();
        try
        {
            await _flushLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        // One final, unconditional flush so a normal "Exit" doesn't lose whatever was recorded
        // since the last periodic tick.
        await FlushOnceAsync().ConfigureAwait(false);
        _flushCts.Dispose();
    }
}
