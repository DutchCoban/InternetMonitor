using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InternetMonitor.Net;

public enum DnsRecordType : ushort
{
    A = 1,
    Txt = 16,
}

public sealed record DnsQueryResult(
    bool Answered,
    int ResponseCode,
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<string> TextRecords,
    double ElapsedMs,
    string? Error)
{
    public bool Success => Answered && ResponseCode == 0;
}

/// <summary>
/// A minimal DNS client that queries one specific server over UDP.
///
/// .NET's <see cref="Dns"/> only talks to whatever resolver Windows is configured with, and has
/// no concept of TXT records at all. Both gaps matter here:
///
///  - Step 6 needs to ask a *different* resolver the same question the system resolver just
///    failed, which is the only way to tell "DNS is broken" apart from "your DNS server
///    specifically is broken".
///  - The traceroute ownership lookup (phase 6) is a TXT query against Team Cymru.
///
/// So this speaks the wire format directly - about as much protocol as v1's SNTP client, and in
/// the same spirit. It handles exactly what those two callers need: one question, A or TXT
/// answers, with name decompression, and no caching, no TCP fallback and no EDNS.
/// </summary>
public static class DnsUdpClient
{
    private const int HeaderSize = 12;
    private const int MaxResponseSize = 4096;

    /// <summary>
    /// Bounds name decompression. A malicious or corrupt response can point a compression offset
    /// back at itself; without a ceiling that is an infinite loop inside the monitoring cycle.
    /// </summary>
    private const int MaxNameJumps = 64;

    public static async Task<DnsQueryResult> QueryAsync(
        IPAddress server,
        string name,
        DnsRecordType type,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IPAddress? source = null)
    {
        long startedAt = Environment.TickCount64;

        try
        {
            byte[] query = BuildQuery(name, type, out ushort transactionId);

            using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (!TcpConnect.TryBind(socket, source))
            {
                return new DnsQueryResult(false, -1, [], [], Environment.TickCount64 - startedAt, "Local address not available");
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);

            await socket.SendToAsync(query, SocketFlags.None, new IPEndPoint(server, 53), budget.Token).ConfigureAwait(false);

            var buffer = new byte[MaxResponseSize];
            EndPoint remote = new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

            while (true)
            {
                SocketReceiveFromResult received = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, remote, budget.Token)
                    .ConfigureAwait(false);

                double elapsed = Environment.TickCount64 - startedAt;
                if (received.ReceivedBytes < HeaderSize)
                {
                    continue;
                }

                // UDP is connectionless, so a stray datagram from anything else on the machine can
                // land here. Matching the transaction id is what makes the answer ours.
                if (BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2)) != transactionId)
                {
                    continue;
                }

                return Parse(buffer.AsSpan(0, received.ReceivedBytes), type, elapsed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DnsQueryResult(false, -1, [], [], Environment.TickCount64 - startedAt, "Timed out");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException or FormatException or IndexOutOfRangeException)
        {
            return new DnsQueryResult(false, -1, [], [], Environment.TickCount64 - startedAt, ex.Message);
        }
    }

    /// <summary>
    /// Asks every server in <paramref name="servers"/> the same question at once and returns the
    /// first successful answer with the server that gave it, cancelling the rest. When none
    /// answers, returns the last failure and a null server. Racing rather than asking in turn keeps
    /// a dead server from costing a full timeout before the next one is even tried.
    /// </summary>
    public static async Task<(IPAddress? Server, DnsQueryResult Result)> QueryFirstAnswerAsync(
        IReadOnlyList<IPAddress> servers,
        string name,
        DnsRecordType type,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IPAddress? source = null)
    {
        DnsQueryResult last = new(false, -1, [], [], 0, "No servers");
        if (servers.Count == 0)
        {
            return (null, last);
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<(IPAddress Server, DnsQueryResult Result)>> pending = servers
            .Select(async server => (server, await QueryAsync(server, name, type, timeout, race.Token, source).ConfigureAwait(false)))
            .ToList();

        try
        {
            while (pending.Count > 0)
            {
                Task<(IPAddress Server, DnsQueryResult Result)> finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);

                (IPAddress server, DnsQueryResult result) = await finished.ConfigureAwait(false);
                if (result.Success && (type != DnsRecordType.A || result.Addresses.Count > 0))
                {
                    return (server, result);
                }

                last = result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            race.Cancel();
        }

        return (null, last);
    }

    private static byte[] BuildQuery(string name, DnsRecordType type, out ushort transactionId)
    {
        transactionId = (ushort)Random.Shared.Next(1, ushort.MaxValue);

        var message = new List<byte>(64);
        message.AddRange(BigEndian(transactionId));
        message.AddRange(BigEndian(0x0100)); // standard query, recursion desired
        message.AddRange(BigEndian(1));      // one question
        message.AddRange(BigEndian(0));      // no answers
        message.AddRange(BigEndian(0));      // no authority records
        message.AddRange(BigEndian(0));      // no additional records

        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63)
            {
                throw new FormatException($"DNS label too long: {label}");
            }

            message.Add((byte)bytes.Length);
            message.AddRange(bytes);
        }

        message.Add(0); // root label terminates the name
        message.AddRange(BigEndian((ushort)type));
        message.AddRange(BigEndian(1)); // IN

        return [.. message];
    }

    private static DnsQueryResult Parse(ReadOnlySpan<byte> response, DnsRecordType type, double elapsedMs)
    {
        int responseCode = response[3] & 0x0F;
        int questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..6]);
        int answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..8]);

        int offset = HeaderSize;
        for (int i = 0; i < questionCount; i++)
        {
            offset = SkipName(response, offset);
            offset += 4; // QTYPE + QCLASS
        }

        var addresses = new List<IPAddress>();
        var texts = new List<string>();

        for (int i = 0; i < answerCount && offset < response.Length; i++)
        {
            offset = SkipName(response, offset);
            if (offset + 10 > response.Length)
            {
                break;
            }

            ushort recordType = BinaryPrimitives.ReadUInt16BigEndian(response[offset..(offset + 2)]);
            ushort dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(offset + 8)..(offset + 10)]);
            offset += 10;

            if (offset + dataLength > response.Length)
            {
                break;
            }

            ReadOnlySpan<byte> data = response.Slice(offset, dataLength);
            if (recordType == (ushort)DnsRecordType.A && dataLength == 4)
            {
                addresses.Add(new IPAddress(data.ToArray()));
            }
            else if (recordType == (ushort)DnsRecordType.Txt)
            {
                // TXT RDATA is a sequence of length-prefixed strings; a long record is split
                // across several of them and the pieces are meant to be concatenated.
                var builder = new StringBuilder();
                int cursor = 0;
                while (cursor < data.Length)
                {
                    int length = data[cursor++];
                    if (cursor + length > data.Length)
                    {
                        break;
                    }

                    builder.Append(Encoding.ASCII.GetString(data.Slice(cursor, length)));
                    cursor += length;
                }

                texts.Add(builder.ToString());
            }

            offset += dataLength;
        }

        // CNAME chains mean the answer section can hold records of other types too; only the ones
        // actually asked for are reported.
        return new DnsQueryResult(
            true,
            responseCode,
            type == DnsRecordType.A ? addresses : [],
            type == DnsRecordType.Txt ? texts : [],
            elapsedMs,
            null);
    }

    /// <summary>
    /// Advances past a (possibly compressed) name and returns the offset of whatever follows it.
    /// A compression pointer ends the name in the wire stream, so the first pointer encountered is
    /// also the end of this name even though the name itself continues elsewhere.
    /// </summary>
    private static int SkipName(ReadOnlySpan<byte> message, int offset)
    {
        int jumps = 0;
        while (offset < message.Length)
        {
            byte length = message[offset];
            if (length == 0)
            {
                return offset + 1;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (++jumps > MaxNameJumps)
                {
                    throw new FormatException("Compression pointer loop in DNS response");
                }

                return offset + 2;
            }

            offset += length + 1;
        }

        return offset;
    }

    private static byte[] BigEndian(ushort value) => [(byte)(value >> 8), (byte)(value & 0xFF)];

    private static byte[] BigEndian(int value) => BigEndian((ushort)value);
}
