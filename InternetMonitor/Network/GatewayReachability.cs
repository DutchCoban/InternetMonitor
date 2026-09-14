using System.Net;
using System.Net.Sockets;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network;

/// <summary>
/// Default-gateway reachability, racing ICMP against a TCP fallback for networks that block
/// ping. Discovery reads the gateway address of <see cref="ActiveInterfaceSelector.GetActive"/>'s
/// interface specifically (the local routing table only - no packets sent) rather than
/// independently re-scanning every interface, so this always agrees with whichever adapter
/// NetworkInterfaceProbe/IpAddressProbe/DnsResolutionProbe are describing that cycle - on a
/// machine with more than one active adapter, two independent guesses could otherwise land on
/// different adapters and produce a confusing, inconsistent diagnosis.
///
/// The reachability check races a plain ICMP ping against a TCP connection attempt to a few
/// common ports, all started at once - most gateways answer ICMP, and a ping reply is a clean,
/// unambiguous positive signal, but trying it first and only then starting the TCP fallback
/// would cost two serial timeouts when neither answers. Racing them keeps the same accuracy at
/// half the worst-case wait. Both an outright TCP connect and an actively refused connection
/// (RST) prove the host answered at the IP/TCP layer, so both count as reachable - only a
/// timeout (no answer at all, on either ICMP or every probed port) means unreachable.
///
/// Known limitation: a gateway that answers neither ICMP nor has a TCP listener on any of these
/// ports will still read as unreachable even if it is functioning correctly. That combination is
/// rare in practice (most gateways answer at least one of the two).
/// </summary>
internal static class GatewayReachability
{
    private static readonly int[] ProbePorts = [80, 443, 53];
    private static readonly TimeSpan PerPortTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan IcmpTimeout = TimeSpan.FromMilliseconds(800);

    public static IPAddress? DiscoverDefaultGateway() =>
        ActiveInterfaceSelector.GetActive()?
            .GetIPProperties().GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>
    /// Reachable plus, when reachable, how long the successful probe took and which method
    /// answered ("ICMP" or "TCP:{port}"). ICMP and every TCP port are probed concurrently (not
    /// one after another, and not ICMP-then-TCP) since any single one succeeding is sufficient -
    /// this returns as soon as the first success comes back, and only waits for everything to
    /// finish if none of them succeed.
    /// </summary>
    public static async Task<(bool Reachable, TimeSpan? Latency, string? Method)> CheckAsync(IPAddress gateway, CancellationToken cancellationToken)
    {
        List<Task<(string Method, TimeSpan? Latency)>> pending =
        [
            WrapAsync("ICMP", TryPingAsync(gateway, cancellationToken)),
            .. ProbePorts.Select(port => WrapAsync($"TCP:{port}", TryConnectAsync(gateway, port, cancellationToken))),
        ];

        while (pending.Count > 0)
        {
            Task<(string Method, TimeSpan? Latency)> completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            (string method, TimeSpan? latency) = await completed.ConfigureAwait(false);
            if (latency is not null)
            {
                return (true, latency, method);
            }
        }

        return (false, null, null);
    }

    private static async Task<(string Method, TimeSpan? Latency)> WrapAsync(string method, Task<TimeSpan?> inner) =>
        (method, await inner.ConfigureAwait(false));

    private static Task<TimeSpan?> TryPingAsync(IPAddress gateway, CancellationToken cancellationToken) =>
        IcmpPing.TryPingAsync(gateway, IcmpTimeout, cancellationToken);

    private static async Task<TimeSpan?> TryConnectAsync(IPAddress gateway, int port, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(PerPortTimeout);
        using var client = new TcpClient();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(gateway, port, cts.Token).ConfigureAwait(false);
            return sw.Elapsed;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return sw.Elapsed;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }
}
