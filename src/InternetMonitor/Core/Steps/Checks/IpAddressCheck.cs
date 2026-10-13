using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 3: do I have a usable address?
///
/// The distinction that matters is an APIPA address - 169.254.x.x, which Windows assigns itself
/// when DHCP got no answer. The adapter looks configured and traffic looks like it should work,
/// but nothing can leave the subnet. Reporting that as "no IP address" would be wrong and
/// reporting it as a working address would be worse, so it gets its own wording and its own fact
/// for the analyser.
///
/// This step also publishes the gateway and DNS servers into the context, since it has the
/// interface properties open anyway and steps 4 and 6 would otherwise re-read them.
/// </summary>
public sealed class IpAddressCheck : IStepCheck
{
    public StepId Id => StepId.IpAddress;

    public bool IsLocal => true;

    public Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        LocalizationManager loc = LocalizationManager.Instance;
        IPInterfaceProperties? properties = context.IpProperties;

        if (properties is null)
        {
            return Task.FromResult(new StepResult(
                Id,
                StepState.Failed,
                loc.Get("step.ip.none"),
                loc.Get("step.ip.none.detail"),
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                [],
                new IpAddressFacts(null, null, false, false, [], null))
            {
                HeadlineKey = "step.ip.none",
                DetailKey = "step.ip.none.detail",
            });
        }

        UnicastIPAddressInformation? unicast = properties.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                && !InterfaceSelector.IsUnspecified(a.Address));

        IPAddress? address = unicast?.Address;
        bool isApipa = address is not null && IsApipa(address);

        IPAddress? gateway = properties.GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !InterfaceSelector.IsUnspecified(a));

        IReadOnlyList<IPAddress> dnsServers =
        [
            .. properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork),
        ];

        context.LocalAddress = address;
        context.Gateway = gateway;
        context.DnsServers = dnsServers;

        bool dhcpEnabled = TryGetDhcpEnabled(properties);

        var facts = new IpAddressFacts(
            address,
            unicast?.IPv4Mask,
            isApipa,
            dhcpEnabled,
            dnsServers,
            gateway);

        var measurements = new List<Measurement>();
        if (address is not null)
        {
            measurements.Add(new Measurement(loc.Get("measure.address"), address.ToString()));
        }

        if (unicast?.IPv4Mask is { } mask && !InterfaceSelector.IsUnspecified(mask))
        {
            measurements.Add(new Measurement(loc.Get("measure.subnet"), mask.ToString()));
        }

        if (gateway is not null)
        {
            measurements.Add(new Measurement(loc.Get("measure.gateway"), gateway.ToString()));
        }

        if (dnsServers.Count > 0)
        {
            measurements.Add(new Measurement(loc.Get("measure.dnsServers"), string.Join(", ", dnsServers)));
        }

        measurements.Add(new Measurement(
            loc.Get("measure.dhcp"),
            loc.Get(dhcpEnabled ? "measure.dhcp.on" : "measure.dhcp.off")));

        (StepState state, string headlineKey, string? detailKey) = address switch
        {
            null => (StepState.Failed, "step.ip.none", "step.ip.none.detail"),
            _ when isApipa => (StepState.Failed, "step.ip.apipa", "step.ip.apipa.detail"),
            _ => (StepState.Ok, "step.ip.ok", (string?)null),
        };

        object[] headlineArgs = address is null ? [] : [address];

        return Task.FromResult(new StepResult(
            Id,
            state,
            address is null ? loc.Get(headlineKey) : loc.Format(headlineKey, address),
            detailKey is null ? null : loc.Get(detailKey),
            null,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = headlineKey,
            HeadlineArgs = headlineArgs,
            DetailKey = detailKey,
        });
    }

    /// <summary>
    /// 169.254.0.0/16 - the address Windows gives itself when DHCP got no answer. Seeing one is
    /// almost always a DHCP or cabling problem rather than an addressing problem.
    /// </summary>
    private static bool IsApipa(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool TryGetDhcpEnabled(IPInterfaceProperties properties)
    {
        try
        {
            return properties.GetIPv4Properties().IsDhcpEnabled;
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }
}
