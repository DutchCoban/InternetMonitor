using System.Net;
using System.Net.Sockets;

namespace InternetMonitor.Network;

/// <summary>Shared DNS resolution probe.</summary>
internal static class DnsProbe
{
    public static async Task<bool> CheckAsync(string hostname, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, cts.Token).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}
