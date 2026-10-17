using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Export;

/// <summary>
/// Replaces the identifiers in an export meant to be pasted into an AI tool - Wi-Fi network names,
/// access point and MAC addresses, and a public IPv4 address this computer itself has - with
/// stable stand-ins: "Wi-Fi network A", "AP-1", "MAC-1", "IP-1". A VPN's adapter name (which names
/// the employer or the VPN service) is reduced to whether it carried all traffic or only some.
///
/// Stable is the point. Dropping the values would also drop the patterns that matter ("the drops
/// started right after switching to the other access point"); a consistent stand-in keeps every
/// such pattern while the real name or hardware address stays on the machine.
///
/// Private, link-local, carrier-grade-NAT and loopback addresses are left alone: they identify
/// nobody. A public IPv4 address is replaced unless it is one the app was told to use - the public
/// ping targets and resolvers, which are the app's, and the adapter's own DNS servers, which name
/// the provider rather than the user. Whatever public address is left is this computer's own, on a
/// line without NAT, and is the one thing in these records that would point at a household.
/// </summary>
public sealed partial class Pseudonymizer
{
    private readonly Dictionary<string, (string Alias, Regex Pattern)> _networks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _accessPoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _macs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _addresses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keptAddresses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _apLabels;
    private readonly HashSet<string> _ssidLabels;
    private readonly HashSet<string> _vpnLabels;
    private readonly HashSet<string> _addressLabels;
    private readonly HashSet<string> _dnsServerLabels;
    private readonly string[] _fullTunnelSuffixes;

    /// <param name="keepAddresses">
    /// Public addresses that are the app's own targets rather than the user's: the configured ping
    /// addresses and resolvers. They stay readable, so "9.9.9.9 unreachable" still says which one.
    /// </param>
    public Pseudonymizer(IEnumerable<IPAddress>? keepAddresses = null)
    {
        _ssidLabels = [.. LocalizationManager.TranslationsOf("measure.wifiNetwork")];
        _apLabels = [.. LocalizationManager.TranslationsOf("measure.wifiAccessPoint")];
        _vpnLabels = [.. LocalizationManager.TranslationsOf("measure.vpn")];
        _addressLabels = [.. LocalizationManager.TranslationsOf("measure.address"), .. LocalizationManager.TranslationsOf("measure.gateway")];
        _dnsServerLabels = [.. LocalizationManager.TranslationsOf("measure.dnsServers")];

        // "{0} (all traffic)" in every language, minus the name placeholder.
        _fullTunnelSuffixes = [.. LocalizationManager.TranslationsOf("measure.vpn.full").Select(t => t.Replace("{0}", string.Empty, StringComparison.Ordinal))];

        foreach (IPAddress address in keepAddresses ?? [])
        {
            _keptAddresses.Add(address.ToString());
        }
    }

    /// <summary>
    /// Learns the network names and addresses to look out for before any text is rewritten: from
    /// the Wi-Fi and address measurements frozen into incidents, and from v1's Wi-Fi history
    /// metadata, which recorded the network name alongside each signal reading. A name only ever
    /// seen inside free text (a change description) is still caught, because those descriptions
    /// are built from the same measurements. The adapter's DNS servers are learned as addresses
    /// to keep, so a public resolver the provider hands out stays readable.
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
                    else if (_addressLabels.Contains(label))
                    {
                        Address(value);
                    }
                    else if (_dnsServerLabels.Contains(label))
                    {
                        foreach (string server in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            _keptAddresses.Add(server);
                        }
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

    /// <summary>The stand-in for a network name; a blank name has nothing to hide and comes back as it is.</summary>
    public string NetworkName(string ssid)
    {
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return ssid;
        }

        if (!_networks.TryGetValue(ssid, out (string Alias, Regex Pattern) entry))
        {
            string alias = $"Wi-Fi network {(char)('A' + Math.Min(_networks.Count, 25))}";

            // The name as a whole word only: a network called "home" must not rewrite "homework".
            var pattern = new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(ssid) + @"(?![\p{L}\p{N}_])", RegexOptions.CultureInvariant);
            entry = (alias, pattern);
            _networks[ssid] = entry;
        }

        return entry.Alias;
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

    /// <summary>
    /// The stand-in for an address of this computer's own, or the value itself when it identifies
    /// nobody: not IPv4, private, link-local, carrier-grade NAT, loopback, or one of the addresses
    /// to keep.
    /// </summary>
    public string Address(string value)
    {
        if (!IPAddress.TryParse(value.Trim(), out IPAddress? address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || HopAnnotator.IsUnroutable(address))
        {
            return value;
        }

        string key = address.ToString();
        if (_keptAddresses.Contains(key))
        {
            return value;
        }

        if (!_addresses.TryGetValue(key, out string? alias))
        {
            alias = $"IP-{_addresses.Count + 1}";
            _addresses[key] = alias;
        }

        return alias;
    }

    /// <summary>One measurement's value, rewritten when its label marks it as an identifier.</summary>
    public string Measurement(string label, string value) =>
        _ssidLabels.Contains(label) ? NetworkName(value)
        : _apLabels.Contains(label) ? AccessPoint(value)
        : _addressLabels.Contains(label) ? Address(value)
        : _vpnLabels.Contains(label) ? (_fullTunnelSuffixes.Any(s => value.EndsWith(s, StringComparison.Ordinal)) ? "VPN (all traffic)" : "VPN (some traffic only)")
        : Text(value);

    /// <summary>
    /// Rewrites free text: every known network name, then every public IPv4 address that is not
    /// one to keep, then every hardware address. A hardware address already known as an access
    /// point keeps its AP alias; any other one (the adapter's own MAC, a device on the LAN) becomes
    /// a MAC alias.
    /// </summary>
    public string Text(string text)
    {
        foreach ((string Alias, Regex Pattern) network in _networks.OrderByDescending(n => n.Key.Length).Select(n => n.Value))
        {
            text = network.Pattern.Replace(text, network.Alias);
        }

        text = Ipv4Address().Replace(text, m => Address(m.Value));

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

    [GeneratedRegex(@"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d.])")]
    private static partial Regex Ipv4Address();
}
