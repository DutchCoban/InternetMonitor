using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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

    /// <summary>
    /// <paramref name="source"/> pins the echo to one local address - see <see cref="SendFromAsync"/>.
    /// Null leaves the choice to Windows' routing table, as before.
    /// </summary>
    public static Task<PingOutcome> SendAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken, IPAddress? source) =>
        source is { AddressFamily: AddressFamily.InterNetwork } && address.AddressFamily == AddressFamily.InterNetwork
            ? SendFromAsync(source, address, timeout, cancellationToken)
            : SendAsync(address, timeout, cancellationToken);

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
        CancellationToken cancellationToken,
        IPAddress? source = null)
    {
        PingOutcome[] outcomes = await Task.WhenAll(
            addresses.Select(a => SendAsync(a, timeout, cancellationToken, source))).ConfigureAwait(false);

        return [.. addresses.Zip(outcomes, (address, outcome) => (address, outcome))];
    }

    /// <summary>
    /// An IPv4 echo sent from a specific local address. <see cref="Ping"/> has no way to choose
    /// the source, so this goes to IcmpSendEcho2Ex directly. Pinning the source is what makes the
    /// step pipeline describe one path: with several adapters up, an unpinned echo leaves by
    /// whichever adapter Windows' routing prefers, which need not be the adapter steps 1-4 just
    /// described. Windows' default strong-host send model only uses routes on the interface that
    /// owns the source address, so an adapter with no way out answers "unreachable" here - which
    /// is the truth about that adapter.
    ///
    /// The native call blocks for up to the timeout, so it runs on the thread pool.
    /// </summary>
    private static async Task<PingOutcome> SendFromAsync(IPAddress source, IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await Task.Run(() => SendFromBlocking(source, destination, timeout), cancellationToken)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return new PingOutcome(IPStatus.Unknown, null, null);
        }
    }

    private static PingOutcome SendFromBlocking(IPAddress source, IPAddress destination, TimeSpan timeout)
    {
        IntPtr handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return new PingOutcome(IPStatus.Unknown, null, null);
        }

        const int ReplyBufferSize = 1024;
        IntPtr reply = Marshal.AllocHGlobal(ReplyBufferSize);
        try
        {
            uint count = IcmpSendEcho2Ex(
                handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                ToNative(source),
                ToNative(destination),
                Payload,
                (ushort)Payload.Length,
                IntPtr.Zero,
                reply,
                ReplyBufferSize,
                (uint)Math.Max(1, timeout.TotalMilliseconds));

            if (count == 0)
            {
                // No reply at all: the error code is the IP_STATUS (timed out, unreachable...),
                // whose values IPStatus mirrors one for one.
                // A source address with no route out fails before anything is sent, with a plain
                // Win32 "unreachable" code rather than an IP_STATUS - mapped so the Diagnostics
                // screen says "unreachable" rather than "unknown".
                int error = Marshal.GetLastWin32Error();
                IPStatus failure = error switch
                {
                    ErrorNetworkUnreachable => IPStatus.DestinationNetworkUnreachable,
                    ErrorHostUnreachable => IPStatus.DestinationHostUnreachable,
                    _ when Enum.IsDefined(typeof(IPStatus), error) => (IPStatus)error,
                    _ => IPStatus.Unknown,
                };
                return new PingOutcome(failure, null, null);
            }

            // ICMP_ECHO_REPLY starts with the responder address, the status and the round trip.
            var responder = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(reply, 0)));
            var status = (IPStatus)Marshal.ReadInt32(reply, 4);
            uint roundtrip = (uint)Marshal.ReadInt32(reply, 8);

            return new PingOutcome(status, status == IPStatus.Success ? roundtrip : null, responder);
        }
        finally
        {
            Marshal.FreeHGlobal(reply);
            IcmpCloseHandle(handle);
        }
    }

    private const int ErrorNetworkUnreachable = 1231;
    private const int ErrorHostUnreachable = 1232;

    private static uint ToNative(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes(), 0);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern IntPtr IcmpCreateFile();

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern bool IcmpCloseHandle(IntPtr handle);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint IcmpSendEcho2Ex(
        IntPtr icmpHandle,
        IntPtr eventHandle,
        IntPtr apcRoutine,
        IntPtr apcContext,
        uint sourceAddress,
        uint destinationAddress,
        byte[] requestData,
        ushort requestSize,
        IntPtr requestOptions,
        IntPtr replyBuffer,
        uint replySize,
        uint timeout);
}
