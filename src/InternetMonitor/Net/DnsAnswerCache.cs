using System.Collections.Concurrent;
using System.Net;

namespace InternetMonitor.Net;

/// <summary>
/// Remembers successful lookups made through explicit DNS servers for a short while.
///
/// Step 7 resolves every target through the adapter's own DNS servers rather than the Windows
/// resolver, so it gets no help from the Windows cache. At a 3-second interval that was a burst of
/// uncached queries every few seconds, all at the same moment step 6 asks the same server -
/// pointless load, because testing DNS is step 6's job, and enough for a rate-limiting router or
/// firewall to start dropping some of them. One answer per name per minute is what a normal
/// resolver cache would send as well.
///
/// Only successes are kept: a failed lookup is asked again on the very next cycle, so a target
/// that stops resolving is noticed as quickly as before.
/// </summary>
public sealed class DnsAnswerCache
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Above this the cache is simply emptied; a monitor resolves a handful of names, not thousands.</summary>
    private const int MaxEntries = 256;

    private readonly ConcurrentDictionary<string, (IReadOnlyList<IPAddress> Addresses, long ExpiresAt)> _entries = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly Func<long> _clockMs;

    public DnsAnswerCache(TimeSpan lifetime, Func<long>? clockMs = null)
    {
        _lifetime = lifetime;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
    }

    public static DnsAnswerCache Shared { get; } = new(DefaultLifetime);

    public bool TryGet(string hostname, IReadOnlyList<IPAddress> servers, IPAddress? source, out IReadOnlyList<IPAddress> addresses)
    {
        string key = Key(hostname, servers, source);
        if (_entries.TryGetValue(key, out var entry))
        {
            if (_clockMs() < entry.ExpiresAt)
            {
                addresses = entry.Addresses;
                return true;
            }

            _entries.TryRemove(key, out _);
        }

        addresses = [];
        return false;
    }

    public void Store(string hostname, IReadOnlyList<IPAddress> servers, IPAddress? source, IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
        {
            return;
        }

        if (_entries.Count >= MaxEntries)
        {
            _entries.Clear();
        }

        _entries[Key(hostname, servers, source)] = (addresses, _clockMs() + (long)_lifetime.TotalMilliseconds);
    }

    /// <summary>The servers and source address are part of the key: an answer obtained over one adapter says nothing about another.</summary>
    private static string Key(string hostname, IReadOnlyList<IPAddress> servers, IPAddress? source) =>
        $"{hostname.ToLowerInvariant()}|{string.Join(",", servers)}|{source}";
}
