using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace InternetMonitor.Net;

/// <summary>One time-server answer: how far this computer's clock is from the server's, and how long the question took.</summary>
/// <param name="Offset">Server time minus local time: positive when this clock is behind.</param>
public sealed record SntpResult(TimeSpan Offset, TimeSpan RoundTrip, IPAddress Server, int Stratum);

/// <summary>
/// A minimal SNTP client (RFC 4330): one 48-byte UDP question to port 123, one answer, four
/// timestamps. In the same spirit as <see cref="DnsUdpClient"/> - just enough protocol for one
/// purpose. The purpose is the clock check: a clock that is minutes off makes every TLS handshake
/// fail with an error that looks like "the site is down", and nothing else in the app would catch
/// that.
/// </summary>
public static class SntpClient
{
    private const int PacketSize = 48;
    private const int Port = 123;

    /// <summary>Seconds between the NTP epoch (1900) and the Unix epoch (1970).</summary>
    private const long NtpToUnixSeconds = 2_208_988_800L;

    public static async Task<SntpResult?> QueryAsync(string server, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(server, budget.Token).ConfigureAwait(false);
            IPAddress? address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            if (address is null)
            {
                return null;
            }

            using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            var request = new byte[PacketSize];
            request[0] = 0x23; // LI 0, version 4, mode 3 (client)

            DateTimeOffset t1 = DateTimeOffset.UtcNow;
            WriteTimestamp(request.AsSpan(40), t1);
            await socket.SendToAsync(request, SocketFlags.None, new IPEndPoint(address, Port), budget.Token).ConfigureAwait(false);

            var response = new byte[PacketSize];
            EndPoint remote = new IPEndPoint(address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            SocketReceiveFromResult received = await socket.ReceiveFromAsync(response, SocketFlags.None, remote, budget.Token).ConfigureAwait(false);
            DateTimeOffset t4 = DateTimeOffset.UtcNow;

            return received.ReceivedBytes < PacketSize ? null : Parse(response, t1, t4, address);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the server's receive (T2) and transmit (T3) timestamps and computes the clock offset
    /// and round trip from them and the client's own send (T1) and receive (T4) times - the
    /// standard SNTP arithmetic. Null for a server that says it is not synchronised (stratum 0, a
    /// "kiss-of-death") or an answer that is not a server reply.
    /// </summary>
    public static SntpResult? Parse(ReadOnlySpan<byte> packet, DateTimeOffset t1, DateTimeOffset t4, IPAddress server)
    {
        if (packet.Length < PacketSize)
        {
            return null;
        }

        int mode = packet[0] & 0x07;
        int stratum = packet[1];
        if (mode != 4 || stratum == 0 || stratum > 15)
        {
            return null;
        }

        DateTimeOffset t2 = ReadTimestamp(packet[32..40]);
        DateTimeOffset t3 = ReadTimestamp(packet[40..48]);

        TimeSpan offset = ((t2 - t1) + (t3 - t4)) / 2;
        TimeSpan roundTrip = (t4 - t1) - (t3 - t2);
        return new SntpResult(offset, roundTrip, server, stratum);
    }

    /// <summary>An NTP timestamp: 32 bits of seconds since 1900 and 32 bits of fraction.</summary>
    public static DateTimeOffset ReadTimestamp(ReadOnlySpan<byte> field)
    {
        uint seconds = BinaryPrimitives.ReadUInt32BigEndian(field[..4]);
        uint fraction = BinaryPrimitives.ReadUInt32BigEndian(field[4..8]);
        long unixSeconds = seconds - NtpToUnixSeconds;
        long ticks = (long)(fraction * (TimeSpan.TicksPerSecond / 4_294_967_296.0));
        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).AddTicks(ticks);
    }

    public static void WriteTimestamp(Span<byte> field, DateTimeOffset at)
    {
        long unixSeconds = at.ToUnixTimeSeconds();
        long ticks = at.UtcTicks - DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcTicks;
        BinaryPrimitives.WriteUInt32BigEndian(field[..4], (uint)(unixSeconds + NtpToUnixSeconds));
        BinaryPrimitives.WriteUInt32BigEndian(field[4..8], (uint)(ticks * (4_294_967_296.0 / TimeSpan.TicksPerSecond)));
    }
}
