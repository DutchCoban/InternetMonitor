using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Tools.Traceroute;

public sealed record TracerouteResult(
    string Target,
    IPAddress? TargetAddress,
    IReadOnlyList<TracerouteHop> Hops,
    bool ReachedDestination,
    DateTimeOffset StartedUtc,
    TimeSpan Duration);

/// <summary>
/// Traceroute, run wide instead of deep.
///
/// A classic traceroute walks TTL 1, 2, 3... one at a time, three probes each, waiting out a
/// timeout whenever a hop is silent. On a twenty-hop route with a couple of quiet routers that is
/// most of a minute, and v1's sequential single-probe version took roughly that long. There is no
/// reason for it: the probes are independent, so this sends a whole wave of TTLs at once and
/// finishes a typical route in a few seconds.
///
/// Waves rather than one giant fan-out, for two reasons. Hops arrive in order, so the list fills
/// top to bottom the way a person reads it; and the run stops as soon as the destination answers,
/// instead of always paying for thirty TTLs when the route is twelve long.
/// </summary>
public sealed class TracerouteRunner
{
    private const int MaxHops = 30;
    private const int ProbesPerHop = 3;
    private const int WaveSize = 8;

    private static readonly TimeSpan PerProbeTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Runs a trace, reporting each hop as it is resolved. <paramref name="enrich"/> is given each
    /// hop after it is reported, to fill in reverse DNS and ownership without holding up the
    /// measurement. <paramref name="localNetwork"/> is the caller's own address and subnet mask -
    /// passed through to <see cref="HopAnnotator"/> so that private address space belonging to the
    /// user's own LAN can be told apart from private address space an ISP happens to use
    /// internally, which is otherwise indistinguishable (both are RFC 1918).
    /// </summary>
    public async Task<TracerouteResult> RunAsync(
        string target,
        IProgress<TracerouteHop>? progress,
        Func<TracerouteHop, CancellationToken, Task>? enrich,
        CancellationToken cancellationToken,
        (IPAddress Address, IPAddress? Mask)? localNetwork = null)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        long startTicks = Environment.TickCount64;

        IPAddress? destination = await ResolveAsync(target, cancellationToken).ConfigureAwait(false);
        if (destination is null)
        {
            return new TracerouteResult(target, null, [], false, started, TimeSpan.Zero);
        }

        var hops = new List<TracerouteHop>();
        var enrichTasks = new List<Task>();
        bool reached = false;

        for (int waveStart = 1; waveStart <= MaxHops && !reached; waveStart += WaveSize)
        {
            int waveEnd = Math.Min(waveStart + WaveSize - 1, MaxHops);

            TracerouteHop[] wave = await Task.WhenAll(
                Enumerable.Range(waveStart, waveEnd - waveStart + 1)
                    .Select(ttl => ProbeHopAsync(destination, ttl, cancellationToken)))
                .ConfigureAwait(false);

            foreach (TracerouteHop hop in wave)
            {
                hops.Add(hop);
                progress?.Report(hop);

                if (enrich is not null)
                {
                    // Enrichment is network I/O of its own (reverse DNS, registry lookups) and
                    // must never delay the next wave, so it runs alongside and is awaited at the
                    // end.
                    enrichTasks.Add(enrich(hop, cancellationToken));
                }

                if (hop.ReachedDestination)
                {
                    reached = true;
                    break;
                }
            }
        }

        try
        {
            await Task.WhenAll(enrichTasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Enrichment is decoration. A failed lookup leaves a hop without an owner, which is
            // exactly what happens for an address no registry knows about anyway.
        }

        HopAnnotator.Annotate(hops, reached, localNetwork);

        return new TracerouteResult(
            target,
            destination,
            hops,
            reached,
            started,
            TimeSpan.FromMilliseconds(Environment.TickCount64 - startTicks));
    }

    private static async Task<TracerouteHop> ProbeHopAsync(IPAddress destination, int ttl, CancellationToken cancellationToken)
    {
        PingOutcome[] outcomes = await Task.WhenAll(
            Enumerable.Range(0, ProbesPerHop)
                .Select(_ => IcmpPing.SendWithTtlAsync(destination, ttl, PerProbeTimeout, cancellationToken)))
            .ConfigureAwait(false);

        return new TracerouteHop(
            ttl,
            [.. outcomes.Select(o => new HopProbe(o.Status, o.RoundtripMs, o.Responder))]);
    }

    private static async Task<IPAddress?> ResolveAsync(string target, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(target, out IPAddress? parsed))
        {
            return parsed;
        }

        DnsOutcome outcome = await DnsResolve
            .ResolveAsync(target, TimeSpan.FromSeconds(3), cancellationToken)
            .ConfigureAwait(false);

        return outcome.Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? outcome.Addresses.FirstOrDefault();
    }

    /// <summary>
    /// Best-effort reverse DNS plus an ownership lookup for a hop. Used as the enrich callback.
    /// </summary>
    public static Func<TracerouteHop, CancellationToken, Task> Enricher(IpOwnershipLookup? ownership) =>
        async (hop, cancellationToken) =>
        {
            if (hop.Responders.Count == 0)
            {
                return;
            }

            IPAddress address = hop.Responders[0];

            Task<string?> reverse = DnsResolve.TryReverseAsync(address, TimeSpan.FromSeconds(2), cancellationToken);
            Task<IpOwner?> owner = ownership is null
                ? Task.FromResult<IpOwner?>(null)
                : ownership.LookupAsync(address, cancellationToken);

            await Task.WhenAll(reverse, owner).ConfigureAwait(false);

            hop.Hostname = await reverse.ConfigureAwait(false);
            hop.Owner = await owner.ConfigureAwait(false);
        };
}
