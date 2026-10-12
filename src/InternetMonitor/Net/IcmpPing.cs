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
