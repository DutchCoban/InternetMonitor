using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace InternetMonitor.Net;

/// <summary>
/// The result of a TCP connect attempt.
///
/// <see cref="Refused"/> is reported separately from failure on purpose: a refused connection
/// proves something is alive at that address and answered, which is exactly what a reachability
/// check wants to know. Only a timeout or an unreachable error means nothing is there.
/// </summary>
public readonly record struct TcpOutcome(bool Connected, bool Refused, double? ElapsedMs, SocketError? Error)
{
    /// <summary>True when something answered, whether it accepted the connection or actively refused it.</summary>
    public bool Answered => Connected || Refused;
}

public static class TcpConnect
{
    public static async Task<TcpOutcome> TryConnectAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(address, port, timeoutCts.Token).ConfigureAwait(false);
            return new TcpOutcome(true, false, stopwatch.Elapsed.TotalMilliseconds, null);
        }
        catch (SocketException ex)
        {
            return new TcpOutcome(
                false,
                ex.SocketErrorCode == SocketError.ConnectionRefused,
                stopwatch.Elapsed.TotalMilliseconds,
                ex.SocketErrorCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled (shutdown, or a newer cycle superseding this one) - distinct
            // from our own timeout below, and worth propagating rather than reporting as a result.
            throw;
        }
        catch (OperationCanceledException)
        {
            return new TcpOutcome(false, false, stopwatch.Elapsed.TotalMilliseconds, SocketError.TimedOut);
        }
        catch (ObjectDisposedException)
        {
            return new TcpOutcome(false, false, null, SocketError.OperationAborted);
        }
    }

    /// <summary>
    /// Races a connect against several ports at once and returns the first to answer. Used for
    /// gateway reachability, where a router may well refuse 80 but answer 53 - either proves it is
    /// there, and waiting through them in sequence would triple the worst case for no new
    /// information.
    /// </summary>
    public static async Task<(int Port, TcpOutcome Outcome)?> FirstAnsweringPortAsync(
        IPAddress address,
        IReadOnlyList<int> ports,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        List<Task<(int Port, TcpOutcome Outcome)>> pending = ports
            .Select(async port => (port, await TryConnectAsync(address, port, timeout, raceCts.Token).ConfigureAwait(false)))
            .ToList();

        try
        {
            while (pending.Count > 0)
            {
                Task<(int Port, TcpOutcome Outcome)> finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);

                (int port, TcpOutcome outcome) = await finished.ConfigureAwait(false);
                if (outcome.Answered)
                {
                    return (port, outcome);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            // Stop the losers rather than leaving sockets dangling until their own timeout; the
            // answer is already known.
            raceCts.Cancel();
        }

        return null;
    }
}
