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
///
/// A connected Wi-Fi adapter also gets its radio side read (<see cref="WlanInfo"/>): signal, band,
/// channel and access point. "Connected" over a two-bar signal is a different situation from a
/// solid one, so a weak signal makes this step a warning - it is the most common cause of a
/// connection that works, but badly.
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
                new LinkFacts(PhysicalLinkState.Unknown, null, false, null))
            {
                HeadlineKey = "step.link.noAdapter",
            });
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

        WifiInfo? wifi = wireless && connected && Guid.TryParse(adapter.Id, out Guid interfaceId)
            ? WlanInfo.Query(interfaceId)
            : null;

        var facts = new LinkFacts(info.Link, managed, wireless, adapter.Name, wifi);
        var measurements = new List<Measurement>
        {
            new(loc.Get("measure.linkState"), loc.Get($"linkState.{info.Link.ToString().ToLowerInvariant()}")),
            new(loc.Get("measure.adapter"), adapter.Name),
        };

        if (info.SpeedBps is { } speed && speed > 0)
        {
            measurements.Add(new Measurement(loc.Get("measure.linkSpeed"), $"{speed / 1_000_000} Mbit/s"));
        }

        if (wifi is not null)
        {
            AddWifiMeasurements(measurements, wifi, loc);
        }

        (StepState state, string headlineKey, string? detailKey) = (connected, connecting, wireless) switch
        {
            (true, _, true) => (StepState.Ok, "step.link.wifiAssociated", null),
            (true, _, false) => (StepState.Ok, "step.link.connected", null),
            (false, true, _) => (StepState.Warning, "step.link.connecting", (string?)null),
            (false, false, true) => (StepState.Failed, "step.link.wifiNotAssociated", "step.link.wifiNotAssociated.detail"),
            _ => (StepState.Failed, "step.link.disconnected", "step.link.disconnected.detail"),
        };

        object[] headlineArgs = [];
        object[] detailArgs = [];
        if (state == StepState.Ok && wifi?.SignalPercent is { } signal)
        {
            (headlineKey, headlineArgs) = wifi.Band is { } band
                ? ("step.link.wifiSignal", [signal, band])
                : ("step.link.wifiSignalNoBand", new object[] { signal });

            if (facts.WeakSignal(context.Targets.WeakWifiSignalPercent))
            {
                state = StepState.Warning;
                detailKey = "step.link.weakSignal.detail";
                detailArgs = [signal];
            }
        }

        return Task.FromResult(new StepResult(
            Id,
            state,
            loc.Format(headlineKey, headlineArgs),
            detailKey is null ? null : loc.Format(detailKey, detailArgs),
            connected ? 1 : 0,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = headlineKey,
            HeadlineArgs = headlineArgs,
            DetailKey = detailKey,
            DetailArgs = detailArgs,
        });
    }

    private static void AddWifiMeasurements(List<Measurement> measurements, WifiInfo wifi, LocalizationManager loc)
    {
        if (wifi.Ssid is { } ssid)
        {
            measurements.Add(new Measurement(loc.Get("measure.wifiNetwork"), ssid));
        }

        if (wifi.Bssid is { } bssid)
        {
            measurements.Add(new Measurement(loc.Get("measure.wifiAccessPoint"), bssid));
        }

        if (wifi.SignalPercent is { } signal)
        {
            measurements.Add(new Measurement(
                loc.Get("measure.wifiSignal"),
                wifi.RssiDbm is { } rssi ? $"{signal}% ({rssi} dBm)" : $"{signal}%"));
        }

        if (wifi.Band is { } band)
        {
            measurements.Add(new Measurement(
                loc.Get("measure.wifiBand"),
                wifi.Channel is { } channel ? loc.Format("measure.wifiBand.channel", band, channel) : band));
        }

        if (wifi.Generation is { } generation)
        {
            measurements.Add(new Measurement(loc.Get("measure.wifiStandard"), generation));
        }

        if (wifi.RxMbps is { } rx && wifi.TxMbps is { } tx)
        {
            measurements.Add(new Measurement(loc.Get("measure.wifiRate"), $"{rx:F0} / {tx:F0} Mbit/s"));
        }

        // Windows withholds the network name and access point when location access is off; say
        // so, rather than leaving the user to wonder why those two rows are missing.
        if (wifi.AccessDenied)
        {
            measurements.Add(new Measurement(loc.Get("measure.wifiDetails"), loc.Get("measure.wifiDetails.denied")));
        }
    }
}
