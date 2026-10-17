using System.Buffers.Binary;
using System.Net;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

/// <summary>The DHCPDISCOVER the probe sends and what it accepts as an offer in reply - over bytes, no socket.</summary>
public sealed class DhcpPacketTests
{
    private static readonly IPEndPoint Sender = new(IPAddress.Parse("192.168.1.1"), 67);

    [Fact]
    public void The_discover_is_a_broadcast_boot_request_with_a_locally_administered_mac_and_one_option()
    {
        byte[] packet = DhcpDiscoverProbe.BuildDiscover(0x12345678);

        Assert.Equal(244, packet.Length);
        Assert.Equal((byte)1, packet[0]);
        Assert.Equal((byte)1, packet[1]);
        Assert.Equal((byte)6, packet[2]);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal((byte)0x80, packet[10]);
        Assert.Equal(0x02, packet[28] & 0x03);
        Assert.Equal(0x63825363u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(236)));
        Assert.Equal([53, 1, 1, 255], packet[240..244]);
    }

    [Fact]
    public void An_offer_names_the_server_from_its_identifier_option_and_the_address_it_offers()
    {
        byte[] packet = Offer(0xCAFE, offered: "192.168.1.50", serverIdentifier: "192.168.1.1");

        Assert.True(DhcpDiscoverProbe.TryParseOffer(packet, 0xCAFE, Sender, out DhcpOfferInfo offer));
        Assert.Equal(IPAddress.Parse("192.168.1.1"), offer.Server);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), offer.OfferedAddress);
    }

    [Fact]
    public void Without_an_identifier_the_boot_server_field_names_the_server_and_then_the_sender_does()
    {
        byte[] viaSiaddr = Offer(1, offered: "10.0.0.9", siaddr: "10.0.0.2");
        Assert.True(DhcpDiscoverProbe.TryParseOffer(viaSiaddr, 1, Sender, out DhcpOfferInfo fromField));
        Assert.Equal(IPAddress.Parse("10.0.0.2"), fromField.Server);

        byte[] viaSender = Offer(1, offered: "0.0.0.0");
        Assert.True(DhcpDiscoverProbe.TryParseOffer(viaSender, 1, Sender, out DhcpOfferInfo fromSender));
        Assert.Equal(Sender.Address, fromSender.Server);
        Assert.Null(fromSender.OfferedAddress);
    }

    [Fact]
    public void Replies_that_are_not_this_probes_offer_are_rejected()
    {
        byte[] good = Offer(7, offered: "10.0.0.9", serverIdentifier: "10.0.0.1");
        Assert.True(DhcpDiscoverProbe.TryParseOffer(good, 7, Sender, out _));

        Assert.False(DhcpDiscoverProbe.TryParseOffer(good, 8, Sender, out _), "another transaction");
        Assert.False(DhcpDiscoverProbe.TryParseOffer(good.AsSpan(0, 239), 7, Sender, out _), "too short");

        byte[] request = (byte[])good.Clone();
        request[0] = 1;
        Assert.False(DhcpDiscoverProbe.TryParseOffer(request, 7, Sender, out _), "a request, not a reply");

        byte[] noCookie = (byte[])good.Clone();
        noCookie[236] = 0;
        Assert.False(DhcpDiscoverProbe.TryParseOffer(noCookie, 7, Sender, out _), "no magic cookie");

        byte[] ack = Offer(7, offered: "10.0.0.9", serverIdentifier: "10.0.0.1", messageType: 5);
        Assert.False(DhcpDiscoverProbe.TryParseOffer(ack, 7, Sender, out _), "an ACK is not an offer");

        // The message-type option claims more bytes than the packet has: the walk stops, no offer is seen.
        byte[] truncated = [.. good.AsSpan(0, 240).ToArray(), 53, 9, 2];
        Assert.False(DhcpDiscoverProbe.TryParseOffer(truncated, 7, Sender, out _), "truncated option");
    }

    private static byte[] Offer(uint xid, string offered, string? serverIdentifier = null, string? siaddr = null, byte messageType = 2)
    {
        var packet = new List<byte>(new byte[240]);
        packet[0] = 2;
        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packet)[4..8], xid);
        IPAddress.Parse(offered).GetAddressBytes().CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packet)[16..20]);
        if (siaddr is not null)
        {
            IPAddress.Parse(siaddr).GetAddressBytes().CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packet)[20..24]);
        }

        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packet)[236..240], 0x63825363);

        packet.AddRange([1, 4, 255, 255, 255, 0]); // a subnet mask option first, to be walked past
        packet.AddRange([53, 1, messageType]);
        if (serverIdentifier is not null)
        {
            packet.AddRange([54, 4]);
            packet.AddRange(IPAddress.Parse(serverIdentifier).GetAddressBytes());
        }

        packet.Add(255);
        return [.. packet];
    }
}
