using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace InternetMonitor.Network;

/// <summary>
/// Shared ICMP ping-with-timeout helper, used wherever a probe wants a best-effort latency
/// reading that must not fail the probe outright when ICMP is filtered (common on corporate/
/// consumer firewalls) - callers fall back to another already-available latency measurement
/// (e.g. a TCP connect time) when this returns null.
/// </summary>
internal static class IcmpPing
{
    public static async Task<TimeSpan?> TryPingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var ping = new Ping();
            var sw = Stopwatch.StartNew();
            PingReply reply = await ping.SendPingAsync(address, (int)timeout.TotalMilliseconds).WaitAsync(cancellationToken).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? sw.Elapsed : null;
        }
        catch (Exception ex) when (ex is PingException or OperationCanceledException)
        {
            return null;
        }
    }
}
