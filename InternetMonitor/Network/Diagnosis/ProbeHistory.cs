namespace InternetMonitor.Network.Diagnosis;

/// <summary>
/// In-memory rolling history of each probe's chartable value, pruned by timestamp (not sample
/// count) so it stays correct regardless of poll interval or extra manual "Check now"
/// runs. Resets on app restart - no persisted storage, per the deliberately small scope of the
/// sparkline feature.
/// </summary>
public sealed class ProbeHistory
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly object _lock = new();
    private readonly Dictionary<string, List<(DateTimeOffset Timestamp, double Value)>> _byProbe = new();

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
            Prune(list);
        }
    }

    public IReadOnlyList<(DateTimeOffset Timestamp, double Value)> Get(string probeId)
    {
        lock (_lock)
        {
            if (!_byProbe.TryGetValue(probeId, out List<(DateTimeOffset Timestamp, double Value)>? list))
            {
                return [];
            }

            Prune(list);
            return list.ToList();
        }
    }

    public void Clear(string probeId)
    {
        lock (_lock)
        {
            _byProbe.Remove(probeId);
        }
    }

    // Pruned against true wall-clock time (not the timestamp of whatever was just recorded) so
    // this stays correct regardless of insertion order, and so history for a probe that stops
    // being recorded (e.g. an endpoint removed from config) still ages out when read later.
    private static void Prune(List<(DateTimeOffset Timestamp, double Value)> list)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - Window;
        list.RemoveAll(p => p.Timestamp < cutoff);
    }
}
