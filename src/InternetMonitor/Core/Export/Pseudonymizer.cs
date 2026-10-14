using System.Text.Json;
using System.Text.RegularExpressions;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Export;

/// <summary>
/// Replaces the identifiers in an export meant to be pasted into an AI tool - Wi-Fi network names,
/// access point and MAC addresses - with stable stand-ins: "Wi-Fi network A", "AP-1", "MAC-1".
///
/// Stable is the point. Dropping the values would also drop the patterns that matter ("the drops
/// started right after switching to the other access point"); a consistent stand-in keeps every
/// such pattern while the real name or hardware address stays on the machine. IP addresses are left
/// alone: a private address identifies nobody, and the public ones in these records are well-known
/// resolvers, not the user's own.
/// </summary>
public sealed partial class Pseudonymizer
{
    private readonly Dictionary<string, string> _networks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _accessPoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _macs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _apLabels;
    private readonly HashSet<string> _ssidLabels;

    public Pseudonymizer()
    {
        _ssidLabels = [.. LocalizationManager.TranslationsOf("measure.wifiNetwork")];
        _apLabels = [.. LocalizationManager.TranslationsOf("measure.wifiAccessPoint")];
    }

    /// <summary>
    /// Learns the network names to look out for before any text is rewritten: from the Wi-Fi
    /// measurements frozen into incidents, and from v1's Wi-Fi history metadata, which recorded
    /// the network name alongside each signal reading. A name only ever seen inside free text (a
    /// change description) is still caught, because those descriptions are built from the same
    /// measurements.
    /// </summary>
    public void Learn(IEnumerable<Incident> incidents, IReadOnlyList<MetricPoint> wifiHistory)
    {
        foreach (Incident incident in incidents)
        {
            foreach (StepSnapshotEntry step in incident.StepsAtStart.Concat(incident.StepsAtResolution ?? []))
            {
                foreach ((string label, string value) in step.Measurements)
                {
                    if (_ssidLabels.Contains(label))
                    {
                        NetworkName(value);
                    }
                }
            }
        }

        foreach (MetricPoint point in wifiHistory)
        {
            if (point.Metadata is { } metadata && metadata.StartsWith("%7B", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using JsonDocument json = JsonDocument.Parse(Uri.UnescapeDataString(metadata));
                    if (json.RootElement.TryGetProperty("Ssid", out JsonElement ssid) && ssid.GetString() is { Length: > 0 } name)
                    {
                        NetworkName(name);
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (WifiHistory.AccessPointOf(point.Metadata) is { } bssid)
            {
                AccessPoint(bssid);
            }
        }
    }

    public string NetworkName(string ssid)
    {
        if (!_networks.TryGetValue(ssid, out string? alias))
        {
            alias = $"Wi-Fi network {(char)('A' + Math.Min(_networks.Count, 25))}";
            _networks[ssid] = alias;
        }

        return alias;
    }

    public string AccessPoint(string bssid)
    {
        string key = bssid.Replace('-', ':').ToLowerInvariant();
        if (!_accessPoints.TryGetValue(key, out string? alias))
        {
            alias = $"AP-{_accessPoints.Count + 1}";
            _accessPoints[key] = alias;
        }

        return alias;
    }

    /// <summary>One measurement's value, rewritten when its label marks it as an identifier.</summary>
    public string Measurement(string label, string value) =>
        _ssidLabels.Contains(label) ? NetworkName(value)
        : _apLabels.Contains(label) ? AccessPoint(value)
        : Text(value);

    /// <summary>
    /// Rewrites free text: every known network name, then every hardware address. A hardware
    /// address already known as an access point keeps its AP alias; any other one (the adapter's
    /// own MAC, a device on the LAN) becomes a MAC alias.
    /// </summary>
    public string Text(string text)
    {
        foreach ((string ssid, string alias) in _networks.OrderByDescending(n => n.Key.Length))
        {
            text = text.Replace(ssid, alias, StringComparison.Ordinal);
        }

        return HardwareAddress().Replace(text, m =>
        {
            string key = m.Value.Replace('-', ':').ToLowerInvariant();
            if (_accessPoints.TryGetValue(key, out string? ap))
            {
                return ap;
            }

            if (!_macs.TryGetValue(key, out string? mac))
            {
                mac = $"MAC-{_macs.Count + 1}";
                _macs[key] = mac;
            }

            return mac;
        });
    }

    [GeneratedRegex(@"\b[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}\b")]
    private static partial Regex HardwareAddress();
}
