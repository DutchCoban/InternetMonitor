using System.Net;
using System.Net.NetworkInformation;

namespace InternetMonitor.Tests.Net;

/// <summary>
/// A made-up adapter: the few things the selector, the VPN detector and step 3 read, with every
/// other member left to the base class. Nothing here touches the machine's real adapters.
/// </summary>
internal sealed class FakeAdapter(
    string id,
    string name,
    string description = "",
    NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
    OperationalStatus status = OperationalStatus.Up,
    long speed = 1_000_000_000,
    FakeIpProperties? ip = null) : NetworkInterface
{
    public override string Id => id;

    public override string Name => name;

    public override string Description => description;

    public override NetworkInterfaceType NetworkInterfaceType => type;

    public override OperationalStatus OperationalStatus => status;

    public override long Speed => speed;

    public override bool IsReceiveOnly => false;

    public override bool SupportsMulticast => true;

    public override IPInterfaceProperties GetIPProperties() => ip ?? new FakeIpProperties();

    public override PhysicalAddress GetPhysicalAddress() => PhysicalAddress.None;

    public override bool Supports(NetworkInterfaceComponent networkInterfaceComponent) => networkInterfaceComponent == NetworkInterfaceComponent.IPv4;
}

/// <summary>What step 3 reads from an adapter: its addresses, gateways, DNS servers, DHCP flag and interface index.</summary>
internal sealed class FakeIpProperties(
    IReadOnlyList<(IPAddress Address, IPAddress? Mask)>? unicast = null,
    IReadOnlyList<IPAddress>? gateways = null,
    IReadOnlyList<IPAddress>? dns = null,
    bool dhcp = true,
    int index = 7,
    bool noIpv4 = false,
    IPAddress? dhcpServer = null) : IPInterfaceProperties
{
    public override UnicastIPAddressInformationCollection UnicastAddresses { get; } = new FakeUnicastCollection(unicast ?? []);

    public override GatewayIPAddressInformationCollection GatewayAddresses { get; } = new FakeGatewayCollection(gateways ?? []);

    public override IPAddressCollection DnsAddresses { get; } = new FakeAddressCollection(dns ?? []);

    public override IPAddressCollection DhcpServerAddresses { get; } = new FakeAddressCollection(dhcpServer is null ? [] : [dhcpServer]);

    public override IPAddressCollection WinsServersAddresses { get; } = new FakeAddressCollection([]);

    public override IPAddressInformationCollection AnycastAddresses => throw new NotSupportedException();

    public override MulticastIPAddressInformationCollection MulticastAddresses => throw new NotSupportedException();

    public override string DnsSuffix => string.Empty;

    public override bool IsDnsEnabled => true;

    public override bool IsDynamicDnsEnabled => false;

    public override IPv4InterfaceProperties GetIPv4Properties() =>
        noIpv4 ? throw new NetworkInformationException() : new FakeIpv4Properties(dhcp, index);

    public override IPv6InterfaceProperties GetIPv6Properties() => throw new NetworkInformationException();
}

internal sealed class FakeIpv4Properties(bool dhcp, int index) : IPv4InterfaceProperties
{
    public override int Index => index;

    public override bool IsDhcpEnabled => dhcp;

    public override bool IsAutomaticPrivateAddressingActive => false;

    public override bool IsAutomaticPrivateAddressingEnabled => false;

    public override bool IsForwardingEnabled => false;

    public override int Mtu => 1500;

    public override bool UsesWins => false;
}

internal sealed class FakeUnicast(IPAddress address, IPAddress? mask) : UnicastIPAddressInformation
{
    public override IPAddress Address => address;

    public override IPAddress IPv4Mask => mask ?? IPAddress.Any;

    public override bool IsDnsEligible => true;

    public override bool IsTransient => false;

    public override long AddressPreferredLifetime => 0;

    public override long AddressValidLifetime => 0;

    public override long DhcpLeaseLifetime => 0;

    public override DuplicateAddressDetectionState DuplicateAddressDetectionState => DuplicateAddressDetectionState.Preferred;

    public override PrefixOrigin PrefixOrigin => PrefixOrigin.Dhcp;

    public override SuffixOrigin SuffixOrigin => SuffixOrigin.OriginDhcp;
}

internal sealed class FakeGateway(IPAddress address) : GatewayIPAddressInformation
{
    public override IPAddress Address => address;
}

internal sealed class FakeAddressCollection(IReadOnlyList<IPAddress> items) : IPAddressCollection
{
    public override int Count => items.Count;

    public override IPAddress this[int index] => items[index];

    public override bool Contains(IPAddress address) => items.Contains(address);

    public override void CopyTo(IPAddress[] array, int offset) => items.ToArray().CopyTo(array, offset);

    public override IEnumerator<IPAddress> GetEnumerator() => items.GetEnumerator();
}

internal sealed class FakeGatewayCollection(IReadOnlyList<IPAddress> items) : GatewayIPAddressInformationCollection
{
    private readonly List<GatewayIPAddressInformation> _items = [.. items.Select(a => new FakeGateway(a))];

    public override int Count => _items.Count;

    public override GatewayIPAddressInformation this[int index] => _items[index];

    public override bool Contains(GatewayIPAddressInformation address) => _items.Contains(address);

    public override void CopyTo(GatewayIPAddressInformation[] array, int offset) => _items.CopyTo(array, offset);

    public override IEnumerator<GatewayIPAddressInformation> GetEnumerator() => _items.GetEnumerator();
}

internal sealed class FakeUnicastCollection(IReadOnlyList<(IPAddress Address, IPAddress? Mask)> items) : UnicastIPAddressInformationCollection
{
    private readonly List<UnicastIPAddressInformation> _items = [.. items.Select(i => new FakeUnicast(i.Address, i.Mask))];

    public override int Count => _items.Count;

    public override UnicastIPAddressInformation this[int index] => _items[index];

    public override bool Contains(UnicastIPAddressInformation address) => _items.Contains(address);

    public override void CopyTo(UnicastIPAddressInformation[] array, int offset) => _items.CopyTo(array, offset);

    public override IEnumerator<UnicastIPAddressInformation> GetEnumerator() => _items.GetEnumerator();
}
