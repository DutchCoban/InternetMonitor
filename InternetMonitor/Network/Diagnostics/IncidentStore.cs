using System.Text.Json;
using System.Text.Json.Serialization;

namespace InternetMonitor.Network.Diagnostics;

/// <summary>Persists incidents as JSON in %AppData%\InternetMonitor\incidents.json, independent of the diagnostic log level.</summary>
public sealed class IncidentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private List<Incident> _incidents;

    public IncidentStore() : this(DefaultPath())
    {
    }

    /// <summary>Testability seam - production code always uses the parameterless constructor.</summary>
    public IncidentStore(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _path = path;
        _incidents = Load();
    }

    private static string DefaultPath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetMonitor");
        return Path.Combine(dir, "incidents.json");
    }

    public IReadOnlyList<Incident> All
    {
        get { lock (_lock) { return _incidents.ToList(); } }
    }

    public string NextId()
    {
        lock (_lock)
        {
            string datePart = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
            int sequence = _incidents.Count(i => i.Id.Contains(datePart)) + 1;
            return $"INC-{datePart}-{sequence:D4}";
        }
    }

    public void Add(Incident incident)
    {
        lock (_lock)
        {
            _incidents.Add(incident);
            TrySave();
        }
    }

    /// <summary>
    /// Replaces the stored incident with the same <see cref="Incident.Id"/> and persists.
    /// In practice the tracker always mutates the very object already held in <see cref="_incidents"/>
    /// and passes that same reference back here, so a plain re-save would "work" - but that's an
    /// implementation detail of the caller, not a guarantee of this API. Finding-and-replacing by
    /// Id makes this method correct on its own terms, independent of caller behavior.
    /// </summary>
    public void Update(Incident incident)
    {
        lock (_lock)
        {
            int index = _incidents.FindIndex(i => i.Id == incident.Id);
            if (index >= 0)
            {
                _incidents[index] = incident;
            }
            else
            {
                _incidents.Add(incident);
            }

            TrySave();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _incidents.Clear();
            TrySave();
        }
    }

    private List<Incident> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<Incident>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void TrySave()
    {
        try
        {
            string json = JsonSerializer.Serialize(_incidents, JsonOptions);
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting incidents must never break monitoring.
        }
    }
}
