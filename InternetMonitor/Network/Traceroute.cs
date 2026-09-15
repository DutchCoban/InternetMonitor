using System.Net;
using System.Net.NetworkInformation;

namespace InternetMonitor.Network;

/// <summary>One hop's result. <see cref="Address"/> is null when the hop timed out (nothing replied at that TTL) - reported anyway so the UI can render a "*" row and keep going, rather than treating a silent hop as a stopping condition.</summary>
public sealed record TracerouteHop(int Ttl, IPAddress? Address, double? LatencyMs, bool ReachedDestination);

/// <summary>
/// TTL-increment traceroute built on the same <see cref="System.Net.NetworkInformation.Ping"/> +
/// <see cref="PingOptions.Ttl"/> approach <see cref="Probes.PingLatencyProbe"/> already uses - no
/// raw sockets, no elevation required. This app already has hard-won experience that ICMP is
/// commonly filtered (see GatewayReachability, IcmpPing) - a hop that never replies is reported as
/// an inconclusive gap (<see cref="TracerouteHop.Address"/> null) rather than aborting the trace,
/// matching that same philosophy.
/// </summary>
public static class Traceroute
{
    private const int MaxHops = 30;
    private static readonly TimeSpan PerHopTimeout = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(string target, IProgress<TracerouteHop> progress, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var options = new PingOptions { DontFragment = true };
        byte[] buffer = [.. "internet-monitor-traceroute"u8];

        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Ttl = ttl;

            try
            {
                PingReply reply = await ping.SendPingAsync(target, (int)PerHopTimeout.TotalMilliseconds, buffer, options)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);

                bool reachedDestination = reply.Status == IPStatus.Success;
                if (reply.Status is IPStatus.Success or IPStatus.TtlExpired)
                {
                    progress.Report(new TracerouteHop(ttl, reply.Address, reply.RoundtripTime, reachedDestination));
                }
                else
                {
                    // TimedOut, DestinationUnreachable, etc. - no usable address for this hop.
                    progress.Report(new TracerouteHop(ttl, null, null, false));
                }

                if (reachedDestination)
                {
                    return;
                }
            }
            catch (PingException)
            {
                progress.Report(new TracerouteHop(ttl, null, null, false));
            }
        }
    }
}
