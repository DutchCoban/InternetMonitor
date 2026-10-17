using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;

namespace InternetMonitor.Core.Incidents;

/// <summary>
/// Reads and writes %AppData%\InternetMonitor\incidents.json.
///
/// The file is shared with v1, so the loader has to cope with records written by either version.
/// v1 records are recognised by their "Classification" property and mapped across; see
/// <see cref="ReadLegacy"/> for what survives the translation and what cannot. Nothing is
/// rewritten on load - v1's records are kept as-is until the incident is touched - so downgrading
/// is not a data-loss event.
///
/// Every write is the whole file under a lock, and every failure is swallowed: persisting
/// incidents must never break monitoring. That is v1's rule and it is the right one; the in-memory
/// list is what the UI shows, and a failed save costs a record, not a session.
/// </summary>
public sealed class IncidentStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private readonly List<Incident> _incidents;
    private IReadOnlyList<Incident>? _sorted;

    public IncidentStore()
    {
        _incidents = Load();
    }

    /// <summary>
    /// Raised after any change to the list, so an open Incidents page updates itself the moment an
    /// incident opens or closes rather than on a poll. Raised on whichever thread made the change,
    /// so UI subscribers must marshal.
    /// </summary>
    public event Action? Changed;

    public IReadOnlyList<Incident> All
    {
        get
        {
            lock (_lock)
            {
                // Sorted once per change, not once per read: the Status page asks for this every
                // cycle, and the list only changes when an incident opens or closes.
                return _sorted ??= [.. _incidents.OrderByDescending(i => i.StartUtc)];
            }
        }
    }

    /// <summary>
    /// The open incident of the step pipeline, if any - what <see cref="IncidentTracker"/> resumes
    /// after a restart. Network-health incidents are left out: they belong to
    /// <see cref="Monitors.NetworkHealthMonitor"/>, which resumes its own through <see cref="ActiveFor"/>.
    /// </summary>
    public Incident? Active
    {
        get
        {
            lock (_lock)
            {
                return _incidents.FirstOrDefault(i => i.Status == IncidentStatus.Active && i.ConditionKey is null);
            }
        }
    }

    /// <summary>The open network-health incident for one condition, if any.</summary>
    public Incident? ActiveFor(string conditionKey)
    {
        lock (_lock)
        {
            return _incidents.FirstOrDefault(i => i.Status == IncidentStatus.Active && i.ConditionKey == conditionKey);
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                return _incidents.Count(i => i.Status == IncidentStatus.Active);
            }
        }
    }

    public void Add(Incident incident)
    {
        lock (_lock)
        {
            _incidents.Add(incident);
            _sorted = null;
        }

        Save();
        Changed?.Invoke();
    }

    public void Update(Incident incident)
    {
        lock (_lock)
        {
            int index = _incidents.FindIndex(i => i.Id == incident.Id);
            if (index >= 0)
            {
                _incidents[index] = incident;
                _sorted = null;
            }
        }

        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _incidents.Clear();
            _sorted = null;
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Builds the id for an incident opening now: INC-20261017-213816.729, the local date and the
    /// local time to the millisecond. The time, not a counter: a counter restarts when the list is
    /// cleared, and "number 0001" would then mean two different outages to a user and a help desk.
    /// No colons - the id is also a file name. Two incidents in the same millisecond (two
    /// network-health conditions can open together) take the next millisecond.
    /// </summary>
    public string NextId(DateTimeOffset now)
    {
        DateTimeOffset local = now.ToLocalTime();
        lock (_lock)
        {
            string id = IdFor(local);
            while (_incidents.Any(i => i.Id == id))
            {
                local = local.AddMilliseconds(1);
                id = IdFor(local);
            }

            return id;
        }
    }

    /// <summary>The id for an incident that opened at <paramref name="at"/>, read as local time: INC-20261017-213816.729.</summary>
    public static string IdFor(DateTimeOffset at) => $"INC-{at.ToLocalTime():yyyyMMdd}-{at.ToLocalTime():HHmmss}.{at.ToLocalTime():fff}";

    private static List<Incident> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.IncidentsFile))
            {
                return [];
            }

            JsonNode? root = JsonNode.Parse(File.ReadAllText(AppPaths.IncidentsFile));
            if (root is not JsonArray array)
            {
                return [];
            }

            var incidents = new List<Incident>();
            foreach (JsonNode? node in array)
            {
                if (node is not JsonObject obj)
                {
                    continue;
                }

                // v1 and v2 share incidents.json; only v1 records have a "Classification" field.
                // One unreadable record is skipped rather than costing the whole history.
                Incident? incident = obj.ContainsKey("Classification") ? ReadLegacy(obj) : ReadCurrent(obj);
                if (incident is not null)
                {
                    incidents.Add(incident);
                }
            }

            return incidents;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static Incident? ReadCurrent(JsonObject obj)
    {
        try
        {
            return obj.Deserialize<Incident>(Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Translates a v1 record. The classification, times, status and headline come across intact.
    /// v1's SnapshotAtStart was a dictionary of probe id to a dictionary of detail strings, which
    /// does not map onto v2's typed step entries without inventing step states that were never
    /// recorded - so it is preserved as recent-changes-style text lines instead of being forced
    /// into a shape it never had. The incident stays readable; it just does not gain detail it
    /// never contained.
    /// </summary>
    private static Incident? ReadLegacy(JsonObject obj)
    {
        try
        {
            string? id = obj["Id"]?.GetValue<string>();
            if (id is null || obj["StartUtc"] is null)
            {
                return null;
            }

            DateTimeOffset start = obj["StartUtc"]!.GetValue<DateTimeOffset>();
            DateTimeOffset? end = obj["EndUtc"] is { } endNode && endNode.GetValueKind() != System.Text.Json.JsonValueKind.Null
                ? endNode.GetValue<DateTimeOffset>()
                : null;

            var incident = new Incident
            {
                Id = id,
                Cause = RootCauseExtensions.FromLegacyName(obj["Classification"]?.GetValue<string>()),
                StartUtc = start,
                EndUtc = end,
                Status = end is null ? IncidentStatus.Active : IncidentStatus.Resolved,
                Headline = obj["Diagnosis"]?.GetValue<string>() ?? string.Empty,
            };

            if (obj["SnapshotAtStart"] is JsonObject snapshot)
            {
                incident.RecentChanges =
                [
                    .. snapshot
                        .Where(kv => kv.Value is JsonObject)
                        .Select(kv => $"{kv.Key}: {Summarize((JsonObject)kv.Value!)}"),
                ];
            }

            return incident;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Summarize(JsonObject probe) =>
        probe["Summary"]?.GetValue<string>()
        ?? probe["Status"]?.GetValue<string>()
        ?? string.Empty;

    private void Save()
    {
        try
        {
            AppPaths.TryEnsureDirectory(AppPaths.Root);

            string json;
            lock (_lock)
            {
                json = JsonSerializer.Serialize(_incidents, Json);
            }

            string temp = AppPaths.IncidentsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.IncidentsFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Persisting incidents must never break monitoring.
        }
    }
}
