using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Diagnosis;

public enum ChangeKind
{
    IpAddress,
    Gateway,
    DnsServers,
    Adapter,
    LinkState,
}

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
    private bool _primed;

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

    /// <summary>Everything that changed in the window leading up to <paramref name="before"/>.</summary>
    public IReadOnlyList<EnvironmentChange> Recent(DateTimeOffset before)
    {
        lock (_lock)
        {
            return [.. _changes.Where(c => c.AtUtc <= before).OrderByDescending(c => c.AtUtc)];
        }
    }
}
