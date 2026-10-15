using System.Net;
using System.Net.Sockets;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>
/// Regression coverage for incident INC-20261014-0004: a single DNS datagram lost for a moment on
/// Wi-Fi was reported as "your DNS server is not answering", because the query was sent exactly
/// once and the step had no second chance within its budget.
/// </summary>
public sealed class DnsTests
{
    [Fact]
    public async Task QueryAsync_resends_and_succeeds_when_the_first_packet_is_lost()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.LocalEndPoint!).Port;

        // The server only answers a query that arrives after the client's half-time resend point,
        // so only the resend can be answered. Whether the original reaches it does not matter -
        // and on Windows it may not: the firewall can drop the very first datagram of a new UDP
        // flow, even on loopback, which is exactly the kind of loss the resend exists for.
        var started = System.Diagnostics.Stopwatch.StartNew();
        Task serve = Task.Run(async () =>
        {
            var buffer = new byte[512];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (true)
            {
                SocketReceiveFromResult r = await server.ReceiveFromAsync(buffer, SocketFlags.None, from);
                if (started.ElapsedMilliseconds >= 400)
                {
                    byte[] reply = BuildAnswer(buffer.AsSpan(0, r.ReceivedBytes), [192, 0, 2, 7]);
                    await server.SendToAsync(reply, SocketFlags.None, r.RemoteEndPoint);
                    return;
                }
            }
        });

        DnsQueryResult result = await DnsUdpClient.QueryAsync(IPAddress.Loopback, "www.example.test", DnsRecordType.A, TimeSpan.FromMilliseconds(1000), CancellationToken.None, port: port);
        await serve.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Success, result.Error);
        Assert.Equal(IPAddress.Parse("192.0.2.7"), Assert.Single(result.Addresses));
        Assert.InRange(result.ElapsedMs, 400, 1000);
    }

    [Fact]
    public async Task QueryAsync_reads_an_ipv6_address_from_an_aaaa_answer()
    {
        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.LocalEndPoint!).Port;
        byte[] expected = IPAddress.Parse("2001:db8::7").GetAddressBytes();

        // Answers every query (the client's resend included - the first datagram of a new flow can
        // be dropped on Windows), until the client stops asking.
        using var stop = new CancellationTokenSource();
        Task serve = Task.Run(async () =>
        {
            var buffer = new byte[512];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (true)
                {
                    SocketReceiveFromResult r = await server.ReceiveFromAsync(buffer, SocketFlags.None, from, stop.Token);
                    await server.SendToAsync(BuildAnswer(buffer.AsSpan(0, r.ReceivedBytes), expected, type: 28), SocketFlags.None, r.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        DnsQueryResult result = await DnsUdpClient.QueryAsync(IPAddress.Loopback, "ipv6.example.test", DnsRecordType.Aaaa, TimeSpan.FromMilliseconds(1000), CancellationToken.None, port: port);
        await stop.CancelAsync();
        await serve;

        Assert.True(result.Success, result.Error);
        Assert.Equal(IPAddress.Parse("2001:db8::7"), Assert.Single(result.Addresses));
    }

    [Fact]
    public void Cache_returns_a_stored_answer_until_it_expires()
    {
        long now = 0;
        var cache = new DnsAnswerCache(TimeSpan.FromSeconds(60), () => now);
        IPAddress[] servers = [IPAddress.Parse("1.1.1.1")];
        IPAddress[] answer = [IPAddress.Parse("192.0.2.1")];

        cache.Store("Nu.nl", servers, null, answer);

        now = 59_000;
        Assert.True(cache.TryGet("nu.nl", servers, null, out IReadOnlyList<IPAddress> hit));
        Assert.Equal(answer, hit);

        now = 60_000;
        Assert.False(cache.TryGet("nu.nl", servers, null, out _));
    }

    [Fact]
    public void Cache_keeps_no_failures_and_separates_adapters()
    {
        var cache = new DnsAnswerCache(TimeSpan.FromSeconds(60), () => 0);
        IPAddress[] wifiDns = [IPAddress.Parse("1.1.1.1")];
        IPAddress[] ethernetDns = [IPAddress.Parse("192.168.1.1")];

        cache.Store("nu.nl", wifiDns, null, []);
        Assert.False(cache.TryGet("nu.nl", wifiDns, null, out _));

        cache.Store("nu.nl", wifiDns, null, [IPAddress.Parse("192.0.2.1")]);
        Assert.False(cache.TryGet("nu.nl", ethernetDns, null, out _));
        Assert.False(cache.TryGet("nu.nl", wifiDns, IPAddress.Parse("10.0.0.5"), out _));
    }

    /// <summary>Echoes the question back with one A (or, with type 28, AAAA) record pointing at it.</summary>
    private static byte[] BuildAnswer(ReadOnlySpan<byte> query, byte[] address, byte type = 1)
    {
        var reply = new List<byte>(query.ToArray());
        reply[2] = 0x81; // response, recursion desired
        reply[3] = 0x80; // recursion available, NOERROR
        reply[7] = 1;    // one answer
        reply.AddRange([0xC0, 0x0C, 0, type, 0, 1, 0, 0, 0, 60, 0, (byte)address.Length]);
        reply.AddRange(address);
        return [.. reply];
    }
}
