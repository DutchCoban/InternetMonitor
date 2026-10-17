using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Tools.Mtu;

/// <summary>What one measurement found. <see cref="PathMtu"/> is null when the target never answered at all.</summary>
/// <param name="LargestPayload">The largest ICMP payload that arrived unfragmented; the MTU is that plus the IPv4 and ICMP headers.</param>
/// <param name="AtCeiling">The probe's largest size got through, so the path MTU is at least <see cref="PathMtu"/>.</param>
public sealed record PathMtuResult(int? PathMtu, int LargestPayload, bool TargetAnswered, bool AtCeiling);

/// <summary>
/// Finds the largest packet that reaches a target without being fragmented - the path MTU - by
/// pinging with the don't-fragment flag and a binary search over the payload size. The usual
/// answer is 1500; 1492 means a PPPoE line; anything lower means a tunnel or VPN sits in the path,
/// which is the classic cause of "some sites stall half-way through loading".
///
/// Only the don't-fragment pings <see cref="IcmpPing"/> already sends for traceroute, so no raw
/// socket and no admin rights - the same constraint as everything else in this app.
/// </summary>
public static class PathMtuProbe
{
    /// <summary>IPv4 header plus ICMP echo header: what the payload is short of the packet size.</summary>
    public const int HeaderBytes = 28;

    /// <summary>A payload the smallest sane path carries; a target that does not answer this does not answer pings.</summary>
    public const int SmallestPayload = 32;

    /// <summary>1500 bytes on the wire: the probe does not look for jumbo frames.</summary>
    public const int LargestPayload = 1500 - HeaderBytes;

    /// <summary>A size is tried twice before it counts as too big, so one lost packet does not understate the path.</summary>
    private const int Attempts = 2;

    public static async Task<PathMtuResult> MeasureAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<bool> Fits(int payload) => FitsAsync(target, payload, timeout, cancellationToken);

        if (!await Fits(SmallestPayload).ConfigureAwait(false))
        {
            return new PathMtuResult(null, 0, false, false);
        }

        int largest = await FindLargestAsync(SmallestPayload, LargestPayload, Fits).ConfigureAwait(false);
        return new PathMtuResult(largest + HeaderBytes, largest, true, largest == LargestPayload);
    }

    /// <summary>
    /// The largest size in [<paramref name="low"/>, <paramref name="high"/>] for which <paramref name="fits"/>
    /// holds, by binary search; <paramref name="low"/> is known to fit. Pure apart from the callback,
    /// so the search itself is testable without a network.
    /// </summary>
    public static async Task<int> FindLargestAsync(int low, int high, Func<int, Task<bool>> fits)
    {
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            if (await fits(mid).ConfigureAwait(false))
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private static async Task<bool> FitsAsync(IPAddress target, int payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            PingOutcome outcome = await IcmpPing.SendSizedAsync(target, payload, dontFragment: true, timeout, cancellationToken).ConfigureAwait(false);
            if (outcome.Success)
            {
                return true;
            }

            // A router saying "too big" is the definitive answer; a silent drop is retried once.
            if (outcome.Status == IPStatus.PacketTooBig)
            {
                return false;
            }
        }

        return false;
    }
}
