using System.Net;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>The SNTP arithmetic over a synthetic server reply: four timestamps in, an offset and a round trip out.</summary>
public sealed class SntpClientTests
{
    private static readonly IPAddress Server = IPAddress.Parse("192.0.2.123");

    private static byte[] Reply(DateTimeOffset t2, DateTimeOffset t3, int stratum = 2, int mode = 4)
    {
        var packet = new byte[48];
        packet[0] = (byte)(0x20 | mode);
        packet[1] = (byte)stratum;
        SntpClient.WriteTimestamp(packet.AsSpan(32), t2);
        SntpClient.WriteTimestamp(packet.AsSpan(40), t3);
        return packet;
    }

    [Fact]
    public void Timestamps_round_trip_through_the_ntp_format()
    {
        var at = new DateTimeOffset(2026, 10, 17, 12, 34, 56, 789, TimeSpan.Zero);
        var field = new byte[8];

        SntpClient.WriteTimestamp(field, at);

        Assert.Equal(at.UtcTicks, SntpClient.ReadTimestamp(field).UtcTicks, 10L);
    }

    [Fact]
    public void A_clock_that_is_two_minutes_behind_shows_as_a_positive_offset()
    {
        var local = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = local;
        DateTimeOffset t4 = local.AddMilliseconds(40);
        DateTimeOffset serverReceived = local.AddMinutes(2).AddMilliseconds(20);
        DateTimeOffset serverSent = serverReceived.AddMilliseconds(1);

        SntpResult? result = SntpClient.Parse(Reply(serverReceived, serverSent), t1, t4, Server);

        Assert.NotNull(result);
        Assert.InRange(result.Offset.TotalSeconds, 119.9, 120.1);
        Assert.InRange(result.RoundTrip.TotalMilliseconds, 38, 40);
        Assert.Equal(2, result.Stratum);
    }

    [Fact]
    public void A_kiss_of_death_or_a_non_server_packet_is_not_an_answer()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.Null(SntpClient.Parse(Reply(now, now, stratum: 0), now, now, Server));
        Assert.Null(SntpClient.Parse(Reply(now, now, mode: 3), now, now, Server));
        Assert.Null(SntpClient.Parse(new byte[20], now, now, Server));
    }
}
