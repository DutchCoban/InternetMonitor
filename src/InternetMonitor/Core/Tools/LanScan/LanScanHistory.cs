using System.Text.Json;
using InternetMonitor.Configuration;

namespace InternetMonitor.Core.Tools.LanScan;

/// <summary>A device the scanner has seen before: what it was last called, when it first and last answered, and the name the user gave it.</summary>
public sealed record KnownDevice(string Key, string? Address, string? Hostname, string? Vendor, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, string? Name);

/// <summary>
/// What the LAN scanner remembers between scans, in <c>lanscan.json</c>: every device it has ever
/// seen, keyed by MAC address (or by IP address when no MAC was found). That is what turns a list
/// of addresses into an answer to the question people actually ask a network scanner - "is there
/// anything here that wasn't here before?" - and what lets a device keep the name the user gave it.
/// </summary>
public sealed class LanScanHistory
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, KnownDevice> _devices;
    private readonly string _path;

    private LanScanHistory(string path, Dictionary<string, KnownDevice> devices)
    {
        _path = path;
        _devices = devices;
    }

    public static string DefaultPath => Path.Combine(AppPaths.Root, "lanscan.json");

    public IReadOnlyCollection<KnownDevice> Devices => _devices.Values;

    /// <summary>The identity a device is remembered by: its MAC address, or its IP address when the scan found none.</summary>
    public static string KeyOf(LanDevice device) =>
        device.MacAddress is { Length: > 0 } mac ? mac.Replace('-', ':').ToUpperInvariant() : device.Address.ToString();

    public static LanScanHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                List<KnownDevice>? stored = JsonSerializer.Deserialize<List<KnownDevice>>(File.ReadAllText(path), Json);
                if (stored is not null)
                {
                    return new LanScanHistory(path, stored.GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.First()));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt file just means every device is new this time.
        }

        return new LanScanHistory(path, []);
    }

    public bool IsKnown(LanDevice device) => _devices.ContainsKey(KeyOf(device));

    public KnownDevice? Lookup(LanDevice device) => _devices.GetValueOrDefault(KeyOf(device));

    /// <summary>Gives a device a name of the user's own (null clears it) and saves.</summary>
    public void SetName(LanDevice device, string? name)
    {
        string key = KeyOf(device);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        KnownDevice known = _devices.GetValueOrDefault(key) ?? new KnownDevice(key, device.Address.ToString(), device.Hostname, device.Vendor, now, now, null);
        _devices[key] = known with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
        Save();
    }

    /// <summary>
    /// Records a finished scan: new devices get their first-seen time, known ones their last-seen
    /// time and whatever name and vendor the scan found. Returns how many were new.
    /// </summary>
    public int RecordScan(IReadOnlyList<LanDevice> devices, DateTimeOffset at)
    {
        int added = 0;
        foreach (LanDevice device in devices)
        {
            string key = KeyOf(device);
            if (_devices.TryGetValue(key, out KnownDevice? known))
            {
                _devices[key] = known with
                {
                    Address = device.Address.ToString(),
                    Hostname = device.Hostname ?? known.Hostname,
                    Vendor = device.Vendor ?? known.Vendor,
                    LastSeenUtc = at,
                };
            }
            else
            {
                _devices[key] = new KnownDevice(key, device.Address.ToString(), device.Hostname, device.Vendor, at, at, null);
                added++;
            }
        }

        Save();
        return added;
    }

    /// <summary>Known devices that did not answer in <paramref name="scanned"/> - gone, or just off right now.</summary>
    public IReadOnlyList<KnownDevice> Missing(IReadOnlyList<LanDevice> scanned)
    {
        HashSet<string> present = [.. scanned.Select(KeyOf)];
        return [.. _devices.Values.Where(d => !present.Contains(d.Key)).OrderByDescending(d => d.LastSeenUtc)];
    }

    private void Save()
    {
        try
        {
            AppPaths.TryEnsureDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_devices.Values.OrderBy(d => d.FirstSeenUtc).ToList(), Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the scan still shows; only the memory of it is lost.
        }
    }
}
