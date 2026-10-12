using System.Diagnostics;
using System.Net.NetworkInformation;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 2: is anything actually plugged in?
///
/// The step v1 did not have. .NET reports a disabled adapter and an adapter with its cable out
/// identically, as <see cref="OperationalStatus.Down"/>, so the only way to ask this question is
/// the interface table's operational status - see <see cref="IfTable"/>.
///
/// For a wireless adapter the same state means "not associated with an access point", which is the
/// honest wireless equivalent of an unplugged cable, and is worded that way.
/// </summary>
public sealed class LinkCheck : IStepCheck
{
    public StepId Id => StepId.Link;

    public bool IsLocal => true;

    public Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        LocalizationManager loc = LocalizationManager.Instance;
        NetworkInterface? adapter = context.Adapter;

        if (adapter is null)
        {
            return Task.FromResult(new StepResult(
                Id,
                StepState.Failed,
                loc.Get("step.link.noAdapter"),
                null,
                0,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                [],
                new LinkFacts(PhysicalLinkState.Unknown, null, false, null)));
        }

        InterfaceLinkInfo info = IfTable.Query(adapter);
        bool wireless = adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        OperationalStatus managed = adapter.OperationalStatus;

        // When the interface table could not be read, fall back to the managed status. That is
        // precisely v1's behaviour, so an unreadable table degrades this step to what v1 could do
        // rather than breaking it.
        bool connected = info.Link == PhysicalLinkState.Unknown
            ? managed == OperationalStatus.Up
            : info.HasCarrier;

        bool connecting = info.Link == PhysicalLinkState.Connecting;

        var facts = new LinkFacts(info.Link, managed, wireless, adapter.Name);
        var measurements = new List<Measurement>
        {
            new(loc.Get("measure.linkState"), loc.Get($"linkState.{info.Link.ToString().ToLowerInvariant()}")),
            new(loc.Get("measure.adapter"), adapter.Name),
        };

        if (info.SpeedBps is { } speed && speed > 0)
        {
            measurements.Add(new Measurement(loc.Get("measure.linkSpeed"), $"{speed / 1_000_000} Mbit/s"));
        }

        (StepState state, string headline, string? detail) = (connected, connecting, wireless) switch
        {
            (true, _, true) => (StepState.Ok, loc.Get("step.link.wifiAssociated"), null),
            (true, _, false) => (StepState.Ok, loc.Get("step.link.connected"), null),
            (false, true, _) => (StepState.Warning, loc.Get("step.link.connecting"), (string?)null),
            (false, false, true) => (StepState.Failed, loc.Get("step.link.wifiNotAssociated"), loc.Get("step.link.wifiNotAssociated.detail")),
            _ => (StepState.Failed, loc.Get("step.link.disconnected"), loc.Get("step.link.disconnected.detail")),
        };

        return Task.FromResult(new StepResult(
            Id,
            state,
            headline,
            detail,
            connected ? 1 : 0,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts));
    }
}
