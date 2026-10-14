using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace InternetMonitor.Net;

public readonly record struct DnsOutcome(
    IReadOnlyList<IPAddress> Addresses,
    double ElapsedMs,
    SocketError? Error,
    bool TimedOut,
    bool FromCache = false)
{
    public bool Success => Addresses.Count > 0;
}

public static class DnsResolve
{
    /// <summary>
    /// Resolves a hostname through the system resolver, with a hard time budget.
    ///
    /// The budget is enforced with a linked token rather than by trusting the resolver: Windows'
    /// own DNS client has its own retry schedule that can run for many seconds past the point
    /// where a monitoring cycle has already decided the answer, and a check that blocks the whole
    /// cycle waiting for it is worse than one that reports a timeout.
    /// </summary>
    public static async Task<DnsOutcome> ResolveAsync(string hostname, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(hostname, timeoutCts.Token).ConfigureAwait(false);
            return new DnsOutcome(addresses, stopwatch.Elapsed.TotalMilliseconds, null, false);
        }
        catch (SocketException ex)
        {
            return new DnsOutcome([], stopwatch.Elapsed.TotalMilliseconds, ex.SocketErrorCode, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DnsOutcome([], stopwatch.Elapsed.TotalMilliseconds, null, true);
        }
        catch (ArgumentException)
        {
            // A malformed hostname from user configuration - not a network condition.
            return new DnsOutcome([], 0, SocketError.HostNotFound, false);
        }
    }

    /// <summary>
    /// Resolves through specific DNS servers - the adapter's own - from a specific local address,
    /// so a lookup made on behalf of one adapter really goes over that adapter. A null server list
    /// means "no adapter context" and uses the system resolver exactly like
    /// <see cref="ResolveAsync"/>; an empty list means the adapter has no DNS server at all, which
    /// is reported as a failed lookup rather than quietly borrowing another adapter's resolver.
    ///
    /// Successful answers through explicit servers are reused for a minute (see
    /// <see cref="DnsAnswerCache"/>), the way the Windows resolver would have cached them.
    /// </summary>
    public static async Task<DnsOutcome> ResolveViaAsync(
        string hostname,
        IReadOnlyList<IPAddress>? servers,
        IPAddress? source,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(hostname, out IPAddress? literal))
        {
            return new DnsOutcome([literal], 0, null, false);
        }

        if (servers is null)
        {
            return await ResolveAsync(hostname, timeout, cancellationToken).ConfigureAwait(false);
        }

        if (servers.Count == 0)
        {
            return new DnsOutcome([], 0, SocketError.HostNotFound, false);
        }

        if (DnsAnswerCache.Shared.TryGet(hostname, servers, source, out IReadOnlyList<IPAddress> cached))
        {
            return new DnsOutcome(cached, 0, null, false, FromCache: true);
        }

        (IPAddress? _, DnsQueryResult result) = await DnsUdpClient
            .QueryFirstAnswerAsync(servers, hostname, DnsRecordType.A, timeout, cancellationToken, source)
            .ConfigureAwait(false);

        if (result.Success && result.Addresses.Count > 0)
        {
            DnsAnswerCache.Shared.Store(hostname, servers, source, result.Addresses);
            return new DnsOutcome(result.Addresses, result.ElapsedMs, null, false);
        }

        bool timedOut = !result.Answered && result.Error == "Timed out";
        return new DnsOutcome([], result.ElapsedMs, timedOut ? null : SocketError.HostNotFound, timedOut);
    }

    /// <summary>
    /// Best-effort reverse lookup, used for traceroute hop names. Returns null rather than
    /// throwing or waiting: a hop with no PTR record is completely normal and must not hold up
    /// the rest of the trace.
    /// </summary>
    public static async Task<string?> TryReverseAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            // No cancellable overload takes an IPAddress, so the budget is applied with WaitAsync.
            // The lookup itself keeps running in the background after that, which is acceptable
            // here: it is a best-effort name for a traceroute row, not a measurement.
            IPHostEntry entry = await Dns.GetHostEntryAsync(address)
                .WaitAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }
}
