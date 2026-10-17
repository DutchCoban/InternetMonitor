using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace InternetMonitor.Net;

/// <summary>Whether the probe could run at all - Unavailable when UDP port 68 could not be bound.</summary>
public enum DhcpProbeStatus
{
    Ok,
    Unavailable,
}

public readonly record struct DhcpOfferInfo(IPAddress Server, IPAddress? OfferedAddress);

public readonly record struct DhcpProbeResult(DhcpProbeStatus Status, IReadOnlyList<DhcpOfferInfo> Offers);

/// <summary>
/// Broadcasts a single DHCPDISCOVER and collects whichever DHCPOFFERs come back, to let a caller
/// compare the set of responding servers against the known gateway and flag anything else as a
/// possible rogue DHCP server.
///
/// This binds UDP port 68, the same port Windows' own DHCP Client service listens on. That service
/// is virtually always running, but empirically does not hold the port exclusively - binding
/// alongside it (with <see cref="SocketOptionName.ReuseAddress"/>) succeeds and a real round trip
/// works. If that assumption is ever wrong on some system, the bind throws a
/// <see cref="SocketException"/> and this returns <see cref="DhcpProbeStatus.Unavailable"/> rather
/// than throwing - the condition simply never fires on that machine instead of crashing anything.
///
/// The client hardware address sent is random, not the machine's real MAC. That is fine here: the
/// broadcast flag is set, so a compliant server replies via IP broadcast (not unicast to the
/// offered address), which reaches this socket over Ethernet broadcast regardless of what chaddr
/// said. This never intends to actually lease an address - no DHCPREQUEST ever follows the offer.
/// </summary>
public static class DhcpDiscoverProbe
{
    private const int ClientPort = 68;
    private const int ServerPort = 67;
    private const int MaxPacketSize = 576;
    private const uint MagicCookie = 0x63825363;
    private const byte OpBootRequest = 1;
    private const byte OpBootReply = 2;
    private const byte OptionMessageType = 53;
    private const byte OptionServerIdentifier = 54;
    private const byte OptionEnd = 255;
    private const byte DhcpOffer = 2;

    public static async Task<DhcpProbeResult> DiscoverAsync(TimeSpan listenWindow, CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.EnableBroadcast = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, ClientPort));

            uint transactionId = (uint)Random.Shared.Next();
            byte[] discover = BuildDiscover(transactionId);
            await socket.SendToAsync(discover, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, ServerPort), cancellationToken).ConfigureAwait(false);

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(listenWindow);

            var offers = new Dictionary<IPAddress, DhcpOfferInfo>();
            var buffer = new byte[MaxPacketSize];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

            try
            {
                while (true)
                {
                    SocketReceiveFromResult received = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, remote, budget.Token)
                        .ConfigureAwait(false);

                    if (TryParseOffer(buffer.AsSpan(0, received.ReceivedBytes), transactionId, (IPEndPoint)received.RemoteEndPoint, out DhcpOfferInfo offer))
                    {
                        offers[offer.Server] = offer;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // Listen window elapsed - this is the normal way this loop ends.
            }

            return new DhcpProbeResult(DhcpProbeStatus.Ok, offers.Values.ToList());
        }
        catch (SocketException)
        {
            return new DhcpProbeResult(DhcpProbeStatus.Unavailable, []);
        }
    }

    /// <summary>
    /// A minimal DHCPDISCOVER: the BOOTP header with the broadcast flag set (so servers answer to
    /// the broadcast address - this probe has no address of its own to be answered at), a random
    /// locally-administered client MAC, the magic cookie, and only the message-type option.
    /// </summary>
    internal static byte[] BuildDiscover(uint transactionId)
    {
        var packet = new byte[240 + 4];
        packet[0] = OpBootRequest;
        packet[1] = 1; // htype: Ethernet
        packet[2] = 6; // hlen: MAC address length
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), transactionId);
        packet[10] = 0x80; // flags: broadcast

        Random.Shared.NextBytes(packet.AsSpan(28, 6)); // chaddr - see type doc comment
        packet[28] &= 0xFE; // clear multicast bit
        packet[28] |= 0x02; // set locally-administered bit

        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(236), MagicCookie);
        packet[240] = OptionMessageType;
        packet[241] = 1;
        packet[242] = 1; // DHCPDISCOVER
        packet[243] = OptionEnd;

        return packet;
    }

    internal static bool TryParseOffer(ReadOnlySpan<byte> packet, uint expectedTransactionId, IPEndPoint remote, out DhcpOfferInfo offer)
    {
        offer = default;

        // The fixed BOOTP header is 236 bytes plus the 4-byte magic cookie; options follow at 240.
        if (packet.Length < 240 || packet[0] != OpBootReply)
        {
            return false;
        }

        // xid: only replies to this probe's own DISCOVER count, not the DHCP traffic of other clients.
        if (BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4)) != expectedTransactionId)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(236, 4)) != MagicCookie)
        {
            return false;
        }

        IPAddress? serverIdentifier = null;
        bool isOffer = false;

        // Options are code/length/value triplets until the End option. A truncated or malformed
        // option ends the walk rather than reading past the packet.
        int pos = 240;
        while (pos < packet.Length)
        {
            byte optionCode = packet[pos];
            if (optionCode == OptionEnd)
            {
                break;
            }

            if (pos + 1 >= packet.Length)
            {
                break;
            }

            byte length = packet[pos + 1];
            int valueStart = pos + 2;
            if (valueStart + length > packet.Length)
            {
                break;
            }

            if (optionCode == OptionMessageType && length == 1)
            {
                isOffer = packet[valueStart] == DhcpOffer;
            }
            else if (optionCode == OptionServerIdentifier && length == 4)
            {
                serverIdentifier = new IPAddress(packet.Slice(valueStart, 4));
            }

            pos = valueStart + length;
        }

        if (!isOffer)
        {
            return false;
        }

        // yiaddr (offset 16) is the address being offered; siaddr (offset 20) is a boot server,
        // which servers often leave empty. The Server Identifier option is the reliable name of the
        // offering server, so it wins; the sender's address is the last resort.
        IPAddress yiaddr = new(packet.Slice(16, 4));
        IPAddress siaddr = new(packet.Slice(20, 4));

        IPAddress server = serverIdentifier
            ?? (siaddr.Equals(IPAddress.Any) ? remote.Address : siaddr);
        IPAddress? offeredAddress = yiaddr.Equals(IPAddress.Any) ? null : yiaddr;

        offer = new DhcpOfferInfo(server, offeredAddress);
        return true;
    }
}
