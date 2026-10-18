using System.Net;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Monitors;

/// <summary>
/// What one pipeline cycle saw of the environment, as far as <see cref="NetworkChangeMonitor"/>
/// cares: which adapter carries the traffic, every adapter's address situation, and the primary
/// adapter's gateway (with the hardware address it answered from), DNS servers, IPv6 prefix and
/// Wi-Fi network. Pure data, so the monitor can be driven without a network.
/// </summary>
public sealed record NetworkReading(
    DateTimeOffset AtUtc,
    string? PrimaryId,
    string? PrimaryName,
    IReadOnlyList<AdapterReading> Adapters,
    IPAddress? Gateway,
    string? GatewayMac,
    IReadOnlyList<IPAddress> DnsServers,
    string? Ipv6Prefix,
    string? Ssid,
    bool Vpn)
{
    public AdapterReading? Primary => PrimaryId is null ? null : Adapters.FirstOrDefault(a => a.Id == PrimaryId);

    public AdapterReading? Adapter(string? id) => id is null ? null : Adapters.FirstOrDefault(a => a.Id == id);

    /// <summary>
    /// The reading for a completed cycle. The gateway's hardware address is looked up only when
    /// step 4 reached it this cycle, so the ARP entry is fresh rather than a leftover.
    /// </summary>
    public static NetworkReading From(StepSnapshot snapshot, Func<IPAddress, string?> macOf)
    {
        AdapterFacts? adapter = snapshot.FactsFor<AdapterFacts>(StepId.Adapter);
        IpAddressFacts? ip = snapshot.FactsFor<IpAddressFacts>(StepId.IpAddress);
        LinkFacts? link = snapshot.FactsFor<LinkFacts>(StepId.Link);

        IPAddress? gateway = ip?.Gateway;
        string? mac = gateway is not null && snapshot.StateOf(StepId.Gateway) == StepState.Ok ? macOf(gateway) : null;

        return new NetworkReading(
            snapshot.TakenUtc,
            adapter?.Id,
            adapter?.Name,
            snapshot.Adapters,
            gateway,
            mac,
            ip?.DnsServers ?? [],
            ip?.GlobalIpv6 is { } ipv6 ? IpAddressCheck.Prefix64(ipv6) : null,
            link?.Wifi?.Ssid,
            ip?.Vpn is not null);
    }
}

/// <summary>Which of the monitor's conditions are switched on - Settings → Network changes.</summary>
public sealed record NetworkChangeOptions(bool PathChanged, bool Apipa, bool NetworkChanged, bool GatewayHardware)
{
    public static NetworkChangeOptions All { get; } = new(true, true, true, true);

    public static NetworkChangeOptions None { get; } = new(false, false, false, false);
}

/// <summary>
/// Turns the environment *moving* into incidents, where <see cref="IncidentTracker"/> only ever
/// records it *breaking*.
///
/// The case this exists for: the cable adapter loses its DHCP server, Windows quietly routes
/// everything over Wi-Fi, the cable adapter sits on a 169.254 address, and a device that only
/// lives on the wired network is unreachable - while every check in the pipeline stays green,
/// because the pipeline follows the route Windows chose. Three hours of "prove it was the
/// network" later, the only evidence was memory. These four conditions record it as it happens:
///
///  - <see cref="PathChanged"/>: the traffic moved to another adapter. Open until it is back on the
///    adapter it came from (that is the duration of the detour), or until that adapter is switched
///    off or gone, in which case the move stands and the incident closes saying so - a laptop that
///    deliberately goes on Wi-Fi is not an incident for the rest of its life.
///  - <see cref="Apipa"/>: an adapter other than the primary one, with DHCP on and link up, holds
///    only an automatic private address: its DHCP server did not answer. Open until it has a
///    proper address again or is no longer connected. The primary adapter is step 3's job.
///  - <see cref="NetworkChanged"/>: on the same primary adapter the gateway, the network, the DNS
///    servers, the DHCP server, the IPv6 prefix or the Wi-Fi network went from one known value to
///    another. A moment, not a period: start and end are the same instant.
///  - <see cref="GatewayHardwareChanged"/>: the same gateway address answered from a different
///    hardware address. Also a moment.
///
/// Like <see cref="ChangeDetector"/>, the first reading is only a baseline, and "none to a value"
/// or "a value to none" is never a change here - that is connecting or an outage, both of which
/// the pipeline already reports. A VPN connecting or disconnecting also re-baselines rather than
/// reports, since a full tunnel replaces the DNS servers by design. Everything stored on an
/// incident is in English - see <see cref="LocalizationManager.FormatEnglish"/>.
/// </summary>
public sealed class NetworkChangeMonitor
{
    public const string PathChanged = "pathChanged";
    public const string Apipa = "apipa";
    public const string NetworkChanged = "networkChanged";
    public const string GatewayHardwareChanged = "gatewayHardwareChanged";

    /// <summary>Two readings in a row on 169.254 before an incident: one can be the moment between link-up and the lease.</summary>
    private const int ApipaReadingsToOpen = 2;

    public static readonly IReadOnlyList<string> Keys = [PathChanged, Apipa, NetworkChanged, GatewayHardwareChanged];

    private readonly object _lock = new();
    private readonly IncidentStore _store;
    private readonly ChangeDetector _changes;
    private readonly Dictionary<string, AdapterWatch> _adapters = [];

    private NetworkReading? _last;
    private NetworkFacts? _lastNetwork;
    private Incident? _pathIncident;
    private bool _pathNoted;
    private IPAddress? _lastGateway;
    private string? _lastGatewayMac;

    public NetworkChangeMonitor(IncidentStore store, ChangeDetector changes)
    {
        _store = store;
        _changes = changes;

        // Pick up what the previous run left open, so it is closed here - by what it is about -
        // rather than lingering as "active" for good.
        foreach (Incident open in store.All.Where(i => i.Status == IncidentStatus.Active && i.Cause == RootCause.NetworkChange))
        {
            if (open.ConditionKey == PathChanged && _pathIncident is null && open.Subject is not null)
            {
                _pathIncident = open;
                _pathNoted = open.Timeline.Any(t => t.Text.Contains("still goes over", StringComparison.Ordinal));
            }
            else if (open.ConditionKey == Apipa && open.Subject is { } id)
            {
                Watch(id).Incident = open;
            }
        }
    }

    public event Action<Incident>? IncidentOpened;

    public event Action<Incident>? IncidentClosed;

    /// <summary>Where a condition's incident started, for the filters, the webhook and the export.</summary>
    public static OutageSide SideOf(string? conditionKey) => conditionKey switch
    {
        PathChanged => OutageSide.ThisComputer,
        Apipa or NetworkChanged or GatewayHardwareChanged => OutageSide.HomeNetwork,
        _ => OutageSide.Other,
    };

    /// <summary>One cycle's reading against the previous one. Thread-safe; called from the pipeline's thread.</summary>
    public void Observe(NetworkReading reading, NetworkChangeOptions options)
    {
        List<Incident> opened = [];
        List<Incident> closed = [];

        lock (_lock)
        {
            bool primed = _last is not null;
            ObservePath(reading, options.PathChanged, primed, opened, closed);
            ObserveApipa(reading, options.Apipa, primed, opened, closed);
            if (primed)
            {
                ObserveNetwork(reading, options.NetworkChanged, opened);
                ObserveGatewayHardware(reading, options.GatewayHardware, opened);
            }

            _last = reading;
            _lastNetwork = NetworkFacts.Of(reading);
            if (reading.Gateway is not null && reading.GatewayMac is not null)
            {
                (_lastGateway, _lastGatewayMac) = (reading.Gateway, reading.GatewayMac);
            }
        }

        foreach (Incident incident in closed)
        {
            IncidentClosed?.Invoke(incident);
        }

        foreach (Incident incident in opened)
        {
            IncidentOpened?.Invoke(incident);
        }
    }

    // ---- The connection moved to another adapter --------------------------------------------------

    private void ObservePath(NetworkReading reading, bool enabled, bool primed, List<Incident> opened, List<Incident> closed)
    {
        if (_pathIncident is { } open)
        {
            string origin = open.Subject!;
            AdapterReading? originAdapter = reading.Adapter(origin);
            string originName = originAdapter?.Name ?? Name(_last, origin) ?? origin;

            if (reading.PrimaryId == origin)
            {
                Close(open, reading.AtUtc, Text("incident.timeline.pathRestored", originName, IncidentTracker.FormatDuration(reading.AtUtc - open.StartUtc)), closed);
                _pathIncident = null;
                return;
            }

            if (!enabled)
            {
                Close(open, reading.AtUtc, Text("incident.timeline.watchDisabled"), closed);
                _pathIncident = null;
                return;
            }

            if (originAdapter is null || !originAdapter.Up)
            {
                Close(open, reading.AtUtc, Text("incident.timeline.pathStands", originName), closed);
                _pathIncident = null;
                return;
            }

            if (primed && reading.PrimaryId is not null && reading.PrimaryId != _last!.PrimaryId)
            {
                open.Timeline.Add(new TimelineEntry(reading.AtUtc, Text("incident.timeline.pathMovedAgain", Describe(reading, reading.PrimaryId))));
                _store.Update(open);
            }

            if (!_pathNoted && originAdapter.HasRealAddress && originAdapter.Gateway is not null && reading.PrimaryId is { } currentId)
            {
                // The adapter it left is usable again, and Windows still prefers the other one.
                // Said once: the metric does not change by itself, so neither would a second line.
                _pathNoted = true;
                open.Timeline.Add(new TimelineEntry(reading.AtUtc, Text("incident.timeline.pathNotRestored", originName, originAdapter.Address!, Describe(reading, currentId))));
                _store.Update(open);
            }

            return;
        }

        if (!enabled || !primed)
        {
            return;
        }

        string? previous = _last!.PrimaryId;
        string? current = reading.PrimaryId;
        if (previous is null || current is null || previous == current)
        {
            return;
        }

        string from = Describe(_last, previous);
        string to = Describe(reading, current);
        string fromName = Name(_last, previous) ?? previous;
        string toName = Name(reading, current) ?? current;

        var incident = new Incident
        {
            Id = _store.NextId(reading.AtUtc),
            Cause = RootCause.NetworkChange,
            ConditionKey = PathChanged,
            Subject = previous,
            StartUtc = reading.AtUtc,
            Status = IncidentStatus.Active,
            Headline = Text("networkChange.pathChanged.headline", from, to),
            Explanation = new Explanation(
                Text("networkChange.pathChanged.what", fromName, toName),
                Text("networkChange.pathChanged.cause", fromName, toName),
                Text("networkChange.pathChanged.todo", fromName)),
            RecentChanges = RecentChanges(reading.AtUtc),
        };

        incident.Timeline.Add(new TimelineEntry(incident.StartUtc, Text("incident.timeline.opened", incident.Headline)));
        _pathIncident = incident;
        _pathNoted = false;
        _store.Add(incident);
        opened.Add(incident);
    }

    // ---- An adapter holds an automatic private address -------------------------------------------

    private void ObserveApipa(NetworkReading reading, bool enabled, bool primed, List<Incident> opened, List<Incident> closed)
    {
        HashSet<string> seen = [];
        foreach (AdapterReading adapter in reading.Adapters)
        {
            seen.Add(adapter.Id);
            AdapterWatch watch = Watch(adapter.Id);

            if (adapter.HasRealAddress)
            {
                watch.LastGood = (adapter.Address!, adapter.DhcpServer, reading.AtUtc);
            }

            if (watch.Incident is { } open)
            {
                if (adapter.HasRealAddress)
                {
                    Close(open, reading.AtUtc, Text("incident.timeline.apipaResolved", adapter.Name, adapter.Address!), closed);
                    watch.Incident = null;
                }
                else if (!adapter.Up)
                {
                    Close(open, reading.AtUtc, Text("incident.timeline.apipaGone", adapter.Name), closed);
                    watch.Incident = null;
                }
                else if (!enabled)
                {
                    Close(open, reading.AtUtc, Text("incident.timeline.watchDisabled"), closed);
                    watch.Incident = null;
                }

                watch.Streak = 0;
                continue;
            }

            bool candidate = enabled && primed && adapter.Up && adapter.DhcpEnabled && adapter.IsApipa
                && !adapter.IsLikelyVirtual && adapter.Id != reading.PrimaryId;
            watch.Streak = candidate ? watch.Streak + 1 : 0;
            if (watch.Streak < ApipaReadingsToOpen)
            {
                continue;
            }

            watch.Streak = 0;
            string cause = Text("networkChange.apipa.cause");
            if (watch.LastGood is { } good)
            {
                cause += " " + Text("networkChange.apipa.cause.previous", good.At.ToLocalTime().ToString("HH:mm:ss"), good.Address, good.DhcpServer?.ToString() ?? Text("change.none"));
            }

            var incident = new Incident
            {
                Id = _store.NextId(reading.AtUtc),
                Cause = RootCause.NetworkChange,
                ConditionKey = Apipa,
                Subject = adapter.Id,
                StartUtc = reading.AtUtc,
                Status = IncidentStatus.Active,
                Headline = Text("networkChange.apipa.headline", adapter.Name, adapter.Address!),
                Explanation = new Explanation(
                    Text("networkChange.apipa.what", adapter.Name),
                    cause,
                    Text("networkChange.apipa.todo", adapter.Name)),
                RecentChanges = RecentChanges(reading.AtUtc),
            };

            incident.Timeline.Add(new TimelineEntry(incident.StartUtc, Text("incident.timeline.opened", incident.Headline)));
            watch.Incident = incident;
            _store.Add(incident);
            opened.Add(incident);
        }

        // An adapter that vanished from the list (removed, or disabled so hard Windows stops
        // listing it) is no longer connected either.
        foreach ((string id, AdapterWatch watch) in _adapters)
        {
            if (!seen.Contains(id) && watch.Incident is { } open)
            {
                Close(open, reading.AtUtc, Text("incident.timeline.apipaGone", Name(_last, id) ?? id), closed);
                watch.Incident = null;
            }
        }
    }

    // ---- The network's settings changed on the same adapter --------------------------------------

    private void ObserveNetwork(NetworkReading reading, bool enabled, List<Incident> opened)
    {
        if (!enabled || reading.PrimaryId is null || reading.PrimaryId != _last!.PrimaryId || reading.Vpn != _last.Vpn || _lastNetwork is not { } previous)
        {
            return;
        }

        NetworkFacts current = NetworkFacts.Of(reading);
        List<string> parts = [];
        Part(parts, "gateway", previous.Gateway, current.Gateway);
        Part(parts, "network", previous.Network, current.Network);
        Part(parts, "dnsServers", previous.DnsServers, current.DnsServers);
        Part(parts, "dhcpServer", previous.DhcpServer, current.DhcpServer);
        Part(parts, "ipv6Prefix", previous.Ipv6Prefix, current.Ipv6Prefix);
        Part(parts, "wifiNetwork", previous.Ssid, current.Ssid);
        if (parts.Count == 0)
        {
            return;
        }

        string adapterName = reading.PrimaryName ?? reading.Primary?.Name ?? reading.PrimaryId;
        string summary = string.Join("; ", parts);
        opened.Add(Moment(
            NetworkChanged,
            reading.AtUtc,
            Text("networkChange.networkChanged.headline", adapterName, summary),
            new Explanation(
                Text("networkChange.networkChanged.what", adapterName, summary),
                Text("networkChange.networkChanged.cause"),
                Text("networkChange.networkChanged.todo"))));
    }

    /// <summary>A known value that became a different known value; nothing else counts.</summary>
    private static void Part(List<string> parts, string key, string? previous, string? current)
    {
        if (previous is not null && current is not null && previous != current)
        {
            parts.Add(Text($"networkChange.part.{key}", previous, current));
        }
    }

    // ---- The router answers from different hardware ----------------------------------------------

    private void ObserveGatewayHardware(NetworkReading reading, bool enabled, List<Incident> opened)
    {
        if (!enabled || reading.Gateway is null || reading.GatewayMac is null
            || _lastGateway is null || !_lastGateway.Equals(reading.Gateway)
            || _lastGatewayMac is null || _lastGatewayMac == reading.GatewayMac)
        {
            return;
        }

        opened.Add(Moment(
            GatewayHardwareChanged,
            reading.AtUtc,
            Text("networkChange.gatewayHardwareChanged.headline", reading.Gateway, reading.GatewayMac, _lastGatewayMac),
            new Explanation(
                Text("networkChange.gatewayHardwareChanged.what"),
                Text("networkChange.gatewayHardwareChanged.cause"),
                Text("networkChange.gatewayHardwareChanged.todo", reading.GatewayMac))));
    }

    // ---- Shared -----------------------------------------------------------------------------------

    /// <summary>An incident that is over the moment it is recorded: start and end are the same instant.</summary>
    private Incident Moment(string key, DateTimeOffset at, string headline, Explanation explanation)
    {
        var incident = new Incident
        {
            Id = _store.NextId(at),
            Cause = RootCause.NetworkChange,
            ConditionKey = key,
            StartUtc = at,
            EndUtc = at,
            Status = IncidentStatus.Resolved,
            Headline = headline,
            Explanation = explanation,
            RecentChanges = RecentChanges(at),
        };

        incident.Timeline.Add(new TimelineEntry(at, Text("incident.timeline.opened", headline)));
        _store.Add(incident);
        return incident;
    }

    private void Close(Incident incident, DateTimeOffset at, string timelineText, List<Incident> closed)
    {
        incident.EndUtc = at;
        incident.Status = IncidentStatus.Resolved;
        incident.Timeline.Add(new TimelineEntry(at, timelineText));
        _store.Update(incident);
        closed.Add(incident);
    }

    private IReadOnlyList<string> RecentChanges(DateTimeOffset before) =>
        [.. _changes.Recent(before).Select(c => $"{c.AtUtc.ToLocalTime():HH:mm:ss}  {c.DescribeEnglish()}")];

    private AdapterWatch Watch(string id)
    {
        if (!_adapters.TryGetValue(id, out AdapterWatch? watch))
        {
            watch = new AdapterWatch();
            _adapters[id] = watch;
        }

        return watch;
    }

    private static string? Name(NetworkReading? reading, string id) =>
        reading?.Adapter(id)?.Name ?? (reading?.PrimaryId == id ? reading.PrimaryName : null);

    /// <summary>"Ethernet (192.168.1.23 via 192.168.1.1)" - an adapter with the network it is on, as far as known.</summary>
    private static string Describe(NetworkReading? reading, string id)
    {
        string name = Name(reading, id) ?? id;
        AdapterReading? adapter = reading?.Adapter(id);
        IPAddress? address = adapter?.Address;
        IPAddress? gateway = adapter?.Gateway ?? (reading?.PrimaryId == id ? reading.Gateway : null);

        return (address, gateway) switch
        {
            (null, _) => Text("networkChange.adapterNoAddress", name),
            (_, null) => Text("networkChange.adapterWithAddress", name, address),
            _ => Text("networkChange.adapterWithNetwork", name, address, gateway),
        };
    }

    private static string Text(string key, params object[] args) =>
        args.Length == 0 ? LocalizationManager.GetEnglish(key) : LocalizationManager.FormatEnglish(key, args);

    private sealed class AdapterWatch
    {
        public int Streak { get; set; }

        public Incident? Incident { get; set; }

        /// <summary>The last proper address this adapter held, and from which DHCP server, for the incident's story.</summary>
        public (IPAddress Address, IPAddress? DhcpServer, DateTimeOffset At)? LastGood { get; set; }
    }

    /// <summary>The primary adapter's network settings as comparable strings; null where unknown.</summary>
    private sealed record NetworkFacts(string? Gateway, string? Network, string? DnsServers, string? DhcpServer, string? Ipv6Prefix, string? Ssid)
    {
        public static NetworkFacts Of(NetworkReading reading)
        {
            AdapterReading? primary = reading.Primary;
            return new NetworkFacts(
                (reading.Gateway ?? primary?.Gateway)?.ToString(),
                primary?.Network,
                reading.DnsServers.Count == 0 ? null : string.Join(", ", reading.DnsServers),
                primary?.DhcpServer?.ToString(),
                reading.Ipv6Prefix,
                reading.Ssid);
        }
    }
}
