using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Diagnosis;

/// <summary>What moved in the environment between two cycles; each kind has a <c>change.*</c> text.</summary>
public enum ChangeKind
{
    IpAddress,
    Gateway,
    DnsServers,
    Adapter,
    LinkState,
    WifiNetwork,
    WifiAccessPoint,
    WifiSignal,
    VpnConnected,
    VpnDisconnected,
}

/// <summary>
/// One change, with the values before and after where they are worth showing (never a hardware
/// address or a VPN's name).
/// </summary>
public sealed record EnvironmentChange(ChangeKind Kind, string? From, string? To, DateTimeOffset AtUtc)
{
    private string Key => $"change.{char.ToLowerInvariant(Kind.ToString()[0])}{Kind.ToString()[1..]}";

    public string Describe()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        return loc.Format(Key, From ?? loc.Get("change.none"), To ?? loc.Get("change.none"));
    }

    /// <summary>Same as <see cref="Describe"/>, resolved in English - see <see cref="LocalizationManager.FormatEnglish"/> for why.</summary>
    public string DescribeEnglish() => LocalizationManager.FormatEnglish(
        Key,
        From ?? LocalizationManager.GetEnglish("change.none"),
        To ?? LocalizationManager.GetEnglish("change.none"));
}

/// <summary>
/// Watches for the environment changing underneath the checks: a new IP address, a different
/// gateway, different DNS servers, a different adapter, a link coming and going.
///
/// This is cheap to compute and often *is* the answer. "Your IP address changed eight seconds
/// before the connection broke" explains a DHCP renewal going wrong, a VPN connecting, or a laptop
/// roaming to a different access point - and none of those are visible in any single check's
/// result, only in the difference between two of them. v1 kept a snapshot at the start and end of
/// an incident but never diffed consecutive cycles, so this class of cause was invisible to it.
///
/// A short rolling window is kept rather than only the last change, so an incident can report
/// everything that moved in the minutes before it opened.
///
/// For Wi-Fi it also notices the radio side moving: another network, another access point on the
/// same network (a roam), and the signal falling sharply. Those are compared only while Wi-Fi
/// details are actually known - a cable, or Windows withholding them, is not a change.
/// </summary>
public sealed class ChangeDetector
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private const int MaxChanges = 200;

    private readonly object _lock = new();
    private readonly List<EnvironmentChange> _changes = [];

    private string? _lastAddress;
    private string? _lastGateway;
    private string? _lastDnsServers;
    private string? _lastAdapter;
    private string? _lastLinkState;
    private string? _lastSsid;
    private string? _lastBssid;
    private bool _lastVpn;
    private bool _primed;

    /// <summary>
    /// A drop of at least this many percentage points below the best signal of the last
    /// <see cref="SignalWindow"/> is worth reporting: a few points either way is ordinary radio
    /// noise, thirty is someone walking into another room or a microwave switching on.
    /// </summary>
    private const int SignalDropPoints = 30;

    private static readonly TimeSpan SignalWindow = TimeSpan.FromMinutes(5);
    private readonly Queue<(DateTimeOffset At, int Percent)> _signals = new();
    private DateTimeOffset _lastSignalDropAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Compares a snapshot against the previous one and records what moved. Returns only the
    /// changes found this cycle.
    /// </summary>
    public IReadOnlyList<EnvironmentChange> Observe(StepSnapshot snapshot)
    {
        IpAddressFacts? ip = snapshot.FactsFor<IpAddressFacts>(StepId.IpAddress);
        AdapterFacts? adapter = snapshot.FactsFor<AdapterFacts>(StepId.Adapter);
        LinkFacts? link = snapshot.FactsFor<LinkFacts>(StepId.Link);

        string? address = ip?.Address?.ToString();
        string? gateway = ip?.Gateway?.ToString();
        string? dnsServers = ip is null ? null : string.Join(", ", ip.DnsServers);
        string? adapterName = adapter?.Name;
        string? linkState = link?.Link.ToString();
        WifiInfo? wifi = link?.Wifi;
        bool vpn = ip?.Vpn is not null;

        var found = new List<EnvironmentChange>();

        lock (_lock)
        {
            // The first snapshot establishes a baseline. Without this every startup would report
            // five changes from nothing, which is noise rather than news.
            if (!_primed)
            {
                _primed = true;
                (_lastAddress, _lastGateway, _lastDnsServers, _lastAdapter, _lastLinkState) =
                    (address, gateway, dnsServers, adapterName, linkState);
                (_lastSsid, _lastBssid) = (wifi?.Ssid, wifi?.Bssid);
                _lastVpn = vpn;
                RecordSignal(snapshot.TakenUtc, wifi?.SignalPercent);
                return [];
            }

            void Check(ChangeKind kind, ref string? previous, string? current)
            {
                if (previous == current)
                {
                    return;
                }

                found.Add(new EnvironmentChange(kind, previous, current, snapshot.TakenUtc));
                previous = current;
            }

            Check(ChangeKind.IpAddress, ref _lastAddress, address);
            Check(ChangeKind.Gateway, ref _lastGateway, gateway);
            Check(ChangeKind.DnsServers, ref _lastDnsServers, dnsServers);
            Check(ChangeKind.Adapter, ref _lastAdapter, adapterName);
            Check(ChangeKind.LinkState, ref _lastLinkState, linkState);
            CheckWifi(found, wifi, snapshot.TakenUtc);

            // Only while step 3 ran: without its facts there is no telling either way. The VPN's
            // name stays out of the text, like an access point's address does.
            if (ip is not null && vpn != _lastVpn)
            {
                found.Add(new EnvironmentChange(vpn ? ChangeKind.VpnConnected : ChangeKind.VpnDisconnected, null, null, snapshot.TakenUtc));
                _lastVpn = vpn;
            }

            _changes.AddRange(found);

            DateTimeOffset cutoff = snapshot.TakenUtc - Window;
            _changes.RemoveAll(c => c.AtUtc < cutoff);
            if (_changes.Count > MaxChanges)
            {
                _changes.RemoveRange(0, _changes.Count - MaxChanges);
            }
        }

        return found;
    }

    /// <summary>
    /// The Wi-Fi part of <see cref="Observe"/>. A different network name is reported as such; a
    /// different access point only when the name stayed the same, because a new network implies a
    /// new access point and saying both is the same news twice. The access point's address itself
    /// is left out of the text - "another access point" is what helps, the MAC address does not.
    /// </summary>
    private void CheckWifi(List<EnvironmentChange> found, WifiInfo? wifi, DateTimeOffset at)
    {
        if (wifi?.Ssid is { } ssid)
        {
            if (_lastSsid is not null && ssid != _lastSsid)
            {
                found.Add(new EnvironmentChange(ChangeKind.WifiNetwork, _lastSsid, ssid, at));
            }
            else if (wifi.Bssid is { } bssid && _lastBssid is not null && bssid != _lastBssid)
            {
                found.Add(new EnvironmentChange(ChangeKind.WifiAccessPoint, null, null, at));
            }

            _lastSsid = ssid;
            _lastBssid = wifi.Bssid ?? _lastBssid;
        }

        int? signal = wifi?.SignalPercent;
        if (signal is { } now && _signals.Count > 0)
        {
            int best = _signals.Max(s => s.Percent);
            if (best - now >= SignalDropPoints && at - _lastSignalDropAt > SignalWindow)
            {
                found.Add(new EnvironmentChange(ChangeKind.WifiSignal, $"{best}%", $"{now}%", at));
                _lastSignalDropAt = at;
            }
        }

        RecordSignal(at, signal);
    }

    private void RecordSignal(DateTimeOffset at, int? signal)
    {
        if (signal is { } percent)
        {
            _signals.Enqueue((at, percent));
        }

        while (_signals.Count > 0 && at - _signals.Peek().At > SignalWindow)
        {
            _signals.Dequeue();
        }
    }

    /// <summary>Everything that changed in the window leading up to <paramref name="before"/>.</summary>
    public IReadOnlyList<EnvironmentChange> Recent(DateTimeOffset before)
    {
        lock (_lock)
        {
            return [.. _changes.Where(c => c.AtUtc <= before).OrderByDescending(c => c.AtUtc)];
        }
    }
}
