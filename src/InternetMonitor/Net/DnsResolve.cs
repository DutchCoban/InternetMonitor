using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace InternetMonitor.Net;

public readonly record struct DnsOutcome(
    IReadOnlyList<IPAddress> Addresses,
    double ElapsedMs,
    SocketError? Error,
    bool TimedOut)
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
