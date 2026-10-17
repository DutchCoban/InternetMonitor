using System.Text;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>
/// A DNS reply is accepted only when it is a reply to the question that was asked. The transaction
/// id alone is sixteen bits; a datagram that merely guesses it right must not pass as the answer.
/// </summary>
public sealed class DnsAnswerMatchingTests
{
    private static byte[] Query(string name, ushort type = 1)
    {
        var bytes = new List<byte> { 0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (string label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return [.. bytes];
    }

    private static byte[] ReplyTo(byte[] query)
    {
        byte[] reply = [.. query, 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 192, 0, 2, 1];
        reply[2] |= 0x80; // QR: this is a response
        reply[7] = 1;     // one answer
        return reply;
    }

    [Fact]
    public void A_reply_that_echoes_the_question_is_the_answer()
    {
        byte[] query = Query("www.example.test");

        Assert.True(DnsUdpClient.IsAnswerTo(ReplyTo(query), query));
    }

    [Fact]
    public void A_query_is_not_an_answer_even_with_the_right_id()
    {
        byte[] query = Query("www.example.test");

        Assert.False(DnsUdpClient.IsAnswerTo(query, query));
    }

    [Fact]
    public void A_reply_to_a_different_name_or_type_is_not_the_answer()
    {
        byte[] query = Query("www.example.test");

        Assert.False(DnsUdpClient.IsAnswerTo(ReplyTo(Query("www.example.tesu")), query));
        Assert.False(DnsUdpClient.IsAnswerTo(ReplyTo(Query("www.example.test", type: 28)), query));
        Assert.False(DnsUdpClient.IsAnswerTo(ReplyTo(Query("example.test")), query));
    }

    [Fact]
    public void A_resolver_that_changes_the_case_of_the_name_still_answers()
    {
        byte[] query = Query("www.Example.TEST");

        Assert.True(DnsUdpClient.IsAnswerTo(ReplyTo(Query("WWW.example.test")), query));
    }

    [Fact]
    public void A_truncated_datagram_is_not_the_answer()
    {
        byte[] query = Query("www.example.test");

        Assert.False(DnsUdpClient.IsAnswerTo(ReplyTo(query).AsSpan(0, 20), query));
        Assert.False(DnsUdpClient.IsAnswerTo(new byte[12], query));
    }
}
