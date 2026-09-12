using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InternetMonitor.Network.Probes;

/// <summary>
/// Minimal DNS-over-UDP A-record query against an explicit server - used to check whether a
/// specific public resolver (e.g. 9.9.9.9) can resolve a hostname the system's own configured
/// resolver couldn't. .NET's <see cref="System.Net.Dns"/> always goes through the OS-configured
/// resolver with no way to target a particular server, so this is a small hand-rolled client
/// (RFC 1035 message format), in the same spirit as the SNTP client in TimeSyncProbe.cs. It only
/// checks the response header's RCODE/ANCOUNT - not a full resolver, just "did this server
/// resolve the name," which is all the diagnosis needs.
/// </summary>
internal static class PublicDnsProbe
{
    private const int DnsPort = 53;

    public static async Task<bool> CanResolveAsync(IPAddress server, string hostname, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            ushort id = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
            byte[] query = BuildQuery(id, hostname);
            await udp.SendAsync(query, new IPEndPoint(server, DnsPort), cts.Token).ConfigureAwait(false);

            UdpReceiveResult result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            return IsSuccessfulAnswer(result.Buffer, id);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] BuildQuery(ushort id, string hostname)
    {
        using var ms = new MemoryStream();
        WriteUInt16BigEndian(ms, id);
        WriteUInt16BigEndian(ms, 0x0100); // standard query, recursion desired
        WriteUInt16BigEndian(ms, 1); // QDCOUNT
        WriteUInt16BigEndian(ms, 0); // ANCOUNT
        WriteUInt16BigEndian(ms, 0); // NSCOUNT
        WriteUInt16BigEndian(ms, 0); // ARCOUNT

        foreach (string label in hostname.Split('.'))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0); // root label terminator

        WriteUInt16BigEndian(ms, 1); // QTYPE = A
        WriteUInt16BigEndian(ms, 1); // QCLASS = IN

        return ms.ToArray();
    }

    /// <summary>Matches the response to our query by ID, then checks RCODE=NOERROR and at least one answer record - doesn't parse the answer section itself (would need to handle name-compression pointers), which "did it resolve" doesn't require.</summary>
    private static bool IsSuccessfulAnswer(byte[] response, ushort expectedId)
    {
        if (response.Length < 12)
        {
            return false;
        }

        ushort id = ReadUInt16BigEndian(response, 0);
        if (id != expectedId)
        {
            return false;
        }

        int rcode = response[3] & 0x0F;
        int answerCount = ReadUInt16BigEndian(response, 6);
        return rcode == 0 && answerCount > 0;
    }

    private static void WriteUInt16BigEndian(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static ushort ReadUInt16BigEndian(byte[] buffer, int offset) =>
        (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
}
