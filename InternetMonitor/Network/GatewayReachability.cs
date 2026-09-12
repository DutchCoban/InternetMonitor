using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace InternetMonitor.Network;

/// <summary>
/// Default-gateway reachability without ICMP (some client networks actively block ping).
/// Discovery reads the local routing table only (no packets sent). The reachability check
/// attempts a TCP connection to a few common ports; both an outright connect and an actively
/// refused connection (RST) prove the host answered at the IP/TCP layer, so both count as
/// reachable - only a timeout (no answer at all) means unreachable.
///
/// Known limitation: a gateway with no TCP listener on any of these ports will read as
/// unreachable even if it is functioning correctly. This is an inherent trade-off of avoiding
/// ICMP, not a bug - worth re-checking once deployed against a real client-site gateway.
/// </summary>
internal static class GatewayReachability
{
    private static readonly int[] ProbePorts = [80, 443, 53];
    private static readonly TimeSpan PerPortTimeout = TimeSpan.FromMilliseconds(800);

    public static IPAddress? DiscoverDefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>
    /// Reachable plus, when reachable, how long the successful probe took. All ports are probed
    /// concurrently (not one after another) since any single one succeeding is sufficient - this
    /// returns as soon as the first success comes back, and only waits for every port to finish
    /// if none of them succeed.
    /// </summary>
    public static async Task<(bool Reachable, TimeSpan? Latency)> CheckAsync(IPAddress gateway, CancellationToken cancellationToken)
    {
        List<Task<TimeSpan?>> pending = ProbePorts
            .Select(port => TryConnectAsync(gateway, port, cancellationToken))
            .ToList();

        while (pending.Count > 0)
        {
            Task<TimeSpan?> completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            TimeSpan? latency = await completed.ConfigureAwait(false);
            if (latency is not null)
            {
                return (true, latency);
            }
        }

        return (false, null);
    }

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
