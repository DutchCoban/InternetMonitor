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
                return [.. _incidents.OrderByDescending(i => i.StartUtc)];
            }
        }
    }

    public Incident? Active
    {
        get
        {
            lock (_lock)
            {
                return _incidents.FirstOrDefault(i => i.Status == IncidentStatus.Active);
            }
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
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>
    /// Builds the next id for today: INC-20261012-0001. Scoped per day so the number stays small
    /// and the date is readable in a support ticket without decoding anything.
    /// </summary>
    public string NextId(DateTimeOffset now)
    {
        string prefix = $"INC-{now.ToLocalTime():yyyyMMdd}-";
        lock (_lock)
        {
            int highest = 0;
            foreach (Incident incident in _incidents)
            {
                if (incident.Id.StartsWith(prefix, StringComparison.Ordinal)
                    && int.TryParse(incident.Id.AsSpan(prefix.Length), out int number))
                {
                    highest = Math.Max(highest, number);
                }
            }

            return $"{prefix}{highest + 1:0000}";
        }
    }

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
