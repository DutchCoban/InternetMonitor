using System.Diagnostics;
using System.Net.NetworkInformation;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 1: is there a network adapter, and is it switched on?
///
/// This is only about the hardware existing and being enabled. Whether anything is plugged into it
/// is step 2, and whether it has an address is step 3. v1 collapsed all three into one probe,
/// which is why it could only ever say "network adapter not active" for three quite different
/// problems with three quite different fixes.
///
/// It also resolves the adapter the rest of the cycle will describe and puts it in the context,
/// so every later step is talking about the same piece of hardware.
/// </summary>
public sealed class AdapterCheck : IStepCheck
{
    public StepId Id => StepId.Adapter;

    public bool IsLocal => true;

    public Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        LocalizationManager loc = LocalizationManager.Instance;

        // One enumeration for the whole cycle: the candidates, the routed adapter and - in step 3 -
        // the VPN detection all read this list rather than asking Windows again.
        IReadOnlyList<NetworkInterface> all = InterfaceSelector.GetAll();
        context.AllAdapters = all;
        IReadOnlyList<NetworkInterface> candidates = InterfaceSelector.Candidates(all);
        NetworkInterface? adapter = InterfaceSelector.GetPrimary(all);

        if (adapter is null)
        {
            return Task.FromResult(new StepResult(
                Id,
                StepState.Failed,
                loc.Get("step.adapter.none"),
                loc.Get("step.adapter.none.detail"),
                0,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                [],
                new AdapterFacts(false, false, null, null, null, null, null, false, false, candidates.Count))
            {
                HeadlineKey = "step.adapter.none",
                DetailKey = "step.adapter.none.detail",
            });
        }

        context.Adapter = adapter;
        context.IpProperties = TryGetIpProperties(adapter);

        InterfaceLinkInfo link = IfTable.Query(adapter);
        bool wireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        string? mac = FormatMac(adapter);
        long? speedMbps = adapter.Speed > 0 ? adapter.Speed / 1_000_000 : null;

        var facts = new AdapterFacts(
            AnyAdapterPresent: true,
            Enabled: link.Admin != AdminStatus.Disabled,
            Name: adapter.Name,
            Description: adapter.Description,
            Type: adapter.NetworkInterfaceType,
            MacAddress: mac,
            SpeedMbps: speedMbps,
            IsWireless: wireless,
            IsLikelyVirtual: InterfaceSelector.IsLikelyVirtual(adapter),
            CandidateCount: candidates.Count);

        var measurements = new List<Measurement>
        {
            new(loc.Get("measure.adapter"), adapter.Name),
            new(loc.Get("measure.description"), adapter.Description),
            new(loc.Get("measure.kind"), loc.Get(wireless ? "measure.kind.wifi" : "measure.kind.ethernet")),
        };

        if (mac is not null)
        {
            measurements.Add(new Measurement(loc.Get("measure.mac"), mac));
        }

        if (speedMbps is not null)
        {
            measurements.Add(new Measurement(loc.Get("measure.linkSpeed"), $"{speedMbps} Mbit/s"));
        }

        // Only an explicit Disabled counts as off. Unknown means the interface table could not be
        // read, in which case assuming the adapter is fine and letting step 2 judge the link is
        // better than inventing a failure out of a missing measurement.
        bool disabled = link.Admin == AdminStatus.Disabled;

        return Task.FromResult(new StepResult(
            Id,
            disabled ? StepState.Failed : StepState.Ok,
            disabled
                ? loc.Format("step.adapter.disabled", adapter.Name)
                : loc.Format("step.adapter.ok", adapter.Name),
            disabled ? loc.Get("step.adapter.disabled.detail") : null,
            disabled ? 0 : 1,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = disabled ? "step.adapter.disabled" : "step.adapter.ok",
            HeadlineArgs = [adapter.Name],
            DetailKey = disabled ? "step.adapter.disabled.detail" : null,
        });
    }

    private static IPInterfaceProperties? TryGetIpProperties(NetworkInterface adapter)
    {
        try
        {
            return adapter.GetIPProperties();
        }
        catch (NetworkInformationException)
        {
            // A disabled adapter can have no IP stack bound at all - a legitimate state, and
            // exactly the one this step is about to report.
            return null;
        }
    }

    private static string? FormatMac(NetworkInterface adapter)
    {
        byte[] bytes = adapter.GetPhysicalAddress().GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join(":", bytes.Select(b => b.ToString("X2")));
    }
}
