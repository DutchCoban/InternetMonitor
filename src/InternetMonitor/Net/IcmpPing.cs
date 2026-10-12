using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace InternetMonitor.Net;

/// <summary>The outcome of one ICMP echo. <paramref name="RoundtripMs"/> is null unless the reply arrived.</summary>
public readonly record struct PingOutcome(IPStatus Status, double? RoundtripMs, IPAddress? Responder)
{
    public bool Success => Status == IPStatus.Success;
}

/// <summary>
/// A ping that never throws. Every network helper in this app follows the same contract: a failure
/// comes back as a result value, not an exception, because monitoring code that has to guard every
/// call with try/catch ends up swallowing the one exception that mattered.
/// </summary>
public static class IcmpPing
{
    private static readonly byte[] Payload = "internet-monitor"u8.ToArray();

    public static async Task<PingOutcome> SendAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            PingReply reply = await ping
                .SendPingAsync(address, timeout, Payload)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return new PingOutcome(
                reply.Status,
                reply.Status == IPStatus.Success ? reply.RoundtripTime : null,
                reply.Address);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is PingException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // A PingException here usually means the address is unroutable or the raw-socket
            // handle could not be created at all; neither is distinguishable from an unreachable
            // host as far as the diagnosis is concerned.
            return new PingOutcome(IPStatus.Unknown, null, null);
        }
    }

    /// <summary>
    /// Sends one echo with a fixed TTL, for traceroute. A router that discards the packet answers
    /// with TtlExpired and its own address, which is how each hop is discovered.
    ///
    /// A fresh <see cref="Ping"/> per call is deliberate: Ping does not support concurrent sends
    /// on one instance, and traceroute deliberately has many in flight at once.
    /// </summary>
    public static async Task<PingOutcome> SendWithTtlAsync(
        IPAddress address,
        int ttl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var options = new PingOptions(ttl, dontFragment: true);
        var watch = Stopwatch.StartNew();

        try
        {
            PingReply reply = await ping
                .SendPingAsync(address, timeout, Payload, options)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            watch.Stop();

            // Windows does not fill in RoundtripTime for a TTL-expired reply - it comes back as
            // zero - so every intermediate hop of a traceroute would read 0 ms if this trusted it.
            // The measured elapsed time is the honest number for those, and is used whenever the
            // reported round-trip is absent.
            double? latency = reply.Status is IPStatus.Success or IPStatus.TtlExpired
                ? reply.RoundtripTime > 0 ? reply.RoundtripTime : watch.Elapsed.TotalMilliseconds
                : null;

            return new PingOutcome(
                reply.Status,
                latency,
                reply.Address is { } responder && !responder.Equals(IPAddress.Any) ? responder : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is PingException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return new PingOutcome(IPStatus.Unknown, null, null);
        }
    }

    /// <summary>
    /// Pings several addresses at once and returns every outcome. Used by the public-IP step,
    /// where one unreachable resolver says nothing - only all of them failing does.
    /// </summary>
    public static async Task<IReadOnlyList<(IPAddress Address, PingOutcome Outcome)>> SendAllAsync(
        IReadOnlyList<IPAddress> addresses,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        PingOutcome[] outcomes = await Task.WhenAll(
            addresses.Select(a => SendAsync(a, timeout, cancellationToken))).ConfigureAwait(false);

        return [.. addresses.Zip(outcomes, (address, outcome) => (address, outcome))];
    }
}
