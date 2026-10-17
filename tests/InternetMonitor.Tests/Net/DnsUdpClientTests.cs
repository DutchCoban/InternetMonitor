using System.Net;
using System.Net.Sockets;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>
/// The DNS client against a UDP responder on 127.0.0.1: what it makes of an error code, a TXT
/// record, a reply that is not to its question, and a server that never answers.
/// </summary>
public sealed class DnsUdpClientTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(800);

    [Fact]
    public async Task An_nxdomain_reply_is_answered_but_not_a_success()
    {
        using var server = new FakeDnsServer(query => [Reply(query, rcode: 3)]);

        DnsQueryResult result = await server.Ask("missing.example.test", DnsRecordType.A);

        Assert.True(result.Answered);
        Assert.False(result.Success);
        Assert.Equal(3, result.ResponseCode);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public async Task A_txt_answer_is_read_with_its_pieces_joined()
    {
        using var server = new FakeDnsServer(query => [Reply(query, txt: ["\"AS1234 | 192.0.2.0/24 | NL", " | ripencc | example\""])]);

        DnsQueryResult result = await server.Ask("1.2.0.192.origin.asn.example", DnsRecordType.Txt);

        Assert.True(result.Success, result.Error);
        Assert.Equal("\"AS1234 | 192.0.2.0/24 | NL | ripencc | example\"", Assert.Single(result.TextRecords));
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public async Task A_reply_to_someone_elses_question_is_ignored_and_the_real_one_taken()
    {
        // Every query gets two datagrams back: first a reply under another transaction's id
        // pointing at the wrong address, then the real one. Accepting the stray would show.
        using var server = new FakeDnsServer(query =>
        {
            byte[] stray = Reply(query, address: [192, 0, 2, 99]);
            stray[0] ^= 0x55;
            return [stray, Reply(query, address: [192, 0, 2, 7])];
        });

        DnsQueryResult result = await server.Ask("www.example.test", DnsRecordType.A);

        Assert.True(result.Success, result.Error);
        Assert.Equal(IPAddress.Parse("192.0.2.7"), Assert.Single(result.Addresses));
    }

    [Fact]
    public async Task A_server_that_never_answers_is_a_timeout_within_the_budget()
    {
        using var server = new FakeDnsServer(_ => []);

        DnsQueryResult result = await server.Ask("www.example.test", DnsRecordType.A);

        Assert.False(result.Answered);
        Assert.Equal("Timed out", result.Error);
        Assert.InRange(result.ElapsedMs, Budget.TotalMilliseconds * 0.9, Budget.TotalMilliseconds + 1500);

        // Two sends: the query and its half-time resend. Windows may drop the first datagram of a
        // new UDP flow even on loopback, so one is the least the server can have seen.
        Assert.InRange(server.Queries, 1, 2);
    }

    [Fact]
    public async Task Records_of_another_type_in_the_answer_are_left_out()
    {
        using var server = new FakeDnsServer(query => [Reply(query, address: IPAddress.Parse("2001:db8::7").GetAddressBytes(), type: 28)]);

        DnsQueryResult result = await server.Ask("www.example.test", DnsRecordType.A);

        Assert.True(result.Success, result.Error);
        Assert.Empty(result.Addresses);
    }

    /// <summary>Echoes the question with the given response code and, optionally, one A/AAAA record or TXT strings.</summary>
    private static byte[] Reply(byte[] query, int rcode = 0, byte[]? address = null, byte type = 1, string[]? txt = null)
    {
        var reply = new List<byte>(query);
        reply[2] = 0x81;
        reply[3] = (byte)(0x80 | rcode);
        reply[6] = 0;
        reply[7] = (byte)(address is null && txt is null ? 0 : 1);

        if (address is not null)
        {
            reply.AddRange([0xC0, 0x0C, 0, type, 0, 1, 0, 0, 0, 60, 0, (byte)address.Length]);
            reply.AddRange(address);
        }
        else if (txt is not null)
        {
            var data = new List<byte>();
            foreach (string piece in txt)
            {
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes(piece);
                data.Add((byte)bytes.Length);
                data.AddRange(bytes);
            }

            reply.AddRange([0xC0, 0x0C, 0, 16, 0, 1, 0, 0, 0, 60, (byte)(data.Count >> 8), (byte)(data.Count & 0xFF)]);
            reply.AddRange(data);
        }

        return [.. reply];
    }

    /// <summary>A UDP socket on 127.0.0.1 that answers every query with the datagrams one function gives, in order; none means stay silent.</summary>
    private sealed class FakeDnsServer : IDisposable
    {
        private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private readonly CancellationTokenSource _stop = new();
        private int _queries;

        public FakeDnsServer(Func<byte[], byte[][]> respond)
        {
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _ = Task.Run(async () =>
            {
                var buffer = new byte[512];
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try
                {
                    while (true)
                    {
                        SocketReceiveFromResult r = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, from, _stop.Token);
                        Interlocked.Increment(ref _queries);
                        foreach (byte[] reply in respond(buffer.AsSpan(0, r.ReceivedBytes).ToArray()))
                        {
                            await _socket.SendToAsync(reply, SocketFlags.None, r.RemoteEndPoint, _stop.Token);
                        }
                    }
                }
                catch (Exception)
                {
                    // Stopped, or the socket is gone.
                }
            });
        }

        public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public int Queries => _queries;

        public Task<DnsQueryResult> Ask(string name, DnsRecordType type) =>
            DnsUdpClient.QueryAsync(IPAddress.Loopback, name, type, Budget, CancellationToken.None, port: Port);

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            _stop.Dispose();
        }
    }
}
