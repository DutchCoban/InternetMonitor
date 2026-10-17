using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

/// <summary>
/// The network-health monitor driven by substituted readings: a counter that jumps opens an
/// incident and calm readings close it, lost pings count as a percentage, a DHCP server that is
/// not the gateway is rogue, an incident left open by a previous run is resumed and closed here,
/// and switching the monitor off closes everything.
/// </summary>
[Collection("Localization")]
public sealed class NetworkHealthMonitorTests
{
    private static readonly NetworkHealthThreshold Off = new(false, 0, 5);
    private static readonly IPAddress Gateway = IPAddress.Parse("192.168.1.1");

    /// <summary>The readings the monitor gets, changed by the test between polls.</summary>
    private sealed class Readings
    {
        public long? TcpRetransmissions { get; set; } = 1000;

        public long? TtlExceeded { get; set; } = 50;

        public HashSet<IPAddress> Unanswered { get; } = [];

        public DhcpProbeResult Dhcp { get; set; } = new(DhcpProbeStatus.Ok, []);

        public NetworkHealthProbes Probes => new(
            () => TcpRetransmissions,
            () => TtlExceeded,
            (targets, _, _) => Task.FromResult<IReadOnlyList<(IPAddress, PingOutcome)>>([.. targets.Select(t => (t, Unanswered.Contains(t)
                ? new PingOutcome(IPStatus.TimedOut, null, null)
                : new PingOutcome(IPStatus.Success, 12, t)))]),
            (_, _) => Task.FromResult(Dhcp));
    }

    private static IncidentStore FreshStore()
    {
        var store = new IncidentStore();
        store.Clear();
        return store;
    }

    private static NetworkHealthMonitor Monitor(IncidentStore store, Readings readings, IPAddress? gateway = null, params IPAddress[] pingTargets) =>
        new(store, () => pingTargets, () => gateway, readings.Probes);

    [Fact]
    public async Task A_jump_in_retransmissions_opens_an_incident_and_calm_readings_close_it()
    {
        IncidentStore store = FreshStore();
        var readings = new Readings();
        await using NetworkHealthMonitor monitor = Monitor(store, readings);
        var opened = new List<Incident>();
        var closed = new List<Incident>();
        monitor.IncidentOpened += opened.Add;
        monitor.IncidentClosed += closed.Add;
        monitor.Reconfigure(true, new NetworkHealthThreshold(true, 5, 5), Off, Off, Off);

        await monitor.PollCountersOnceAsync(CancellationToken.None); // baseline only
        Assert.Empty(opened);
        Assert.Equal(0, monitor.CurrentStatus.TcpRetransmissions.CountInWindow);

        readings.TcpRetransmissions = 1012;
        await Task.Delay(2);
        await monitor.PollCountersOnceAsync(CancellationToken.None);

        Incident incident = Assert.Single(opened);
        Assert.Equal(RootCause.NetworkHealth, incident.Cause);
        Assert.Equal("tcpRetransmissions", incident.ConditionKey);
        Assert.StartsWith("INC-", incident.Id, StringComparison.Ordinal);
        Assert.NotEqual(string.Empty, incident.Headline);
        Assert.True(monitor.CurrentStatus.TcpRetransmissions.IncidentOpen);
        Assert.Equal(12, monitor.CurrentStatus.TcpRetransmissions.CountInWindow);
        Assert.Same(incident, store.ActiveFor("tcpRetransmissions"));

        await Task.Delay(2);
        await monitor.PollCountersOnceAsync(CancellationToken.None);
        Assert.Empty(closed);
        await Task.Delay(2);
        await monitor.PollCountersOnceAsync(CancellationToken.None);

        Assert.Same(incident, Assert.Single(closed));
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.NotNull(incident.EndUtc);
        Assert.False(monitor.CurrentStatus.TcpRetransmissions.IncidentOpen);
        Assert.Null(store.ActiveFor("tcpRetransmissions"));
    }

    [Fact]
    public async Task Lost_pings_are_a_percentage_averaged_over_the_window()
    {
        IncidentStore store = FreshStore();
        var readings = new Readings();
        IPAddress a = IPAddress.Parse("1.1.1.1");
        IPAddress b = IPAddress.Parse("9.9.9.9");
        await using NetworkHealthMonitor monitor = Monitor(store, readings, null, a, b);
        var opened = new List<Incident>();
        monitor.IncidentOpened += opened.Add;
        monitor.Reconfigure(true, Off, new NetworkHealthThreshold(true, 20, 5), Off, Off);

        await monitor.PollCountersOnceAsync(CancellationToken.None);
        Assert.Equal(0, monitor.CurrentStatus.PacketLoss.CountInWindow);
        Assert.Empty(opened);

        readings.Unanswered.Add(a);
        await Task.Delay(2);
        await monitor.PollCountersOnceAsync(CancellationToken.None);

        Assert.Equal(25, monitor.CurrentStatus.PacketLoss.CountInWindow);
        Incident incident = Assert.Single(opened);
        Assert.Equal("packetLoss", incident.ConditionKey);
    }

    [Fact]
    public async Task A_counter_that_cannot_be_read_records_nothing()
    {
        IncidentStore store = FreshStore();
        var readings = new Readings { TtlExceeded = null };
        await using NetworkHealthMonitor monitor = Monitor(store, readings);
        monitor.Reconfigure(true, Off, Off, new NetworkHealthThreshold(true, 0, 5), Off);

        await monitor.PollCountersOnceAsync(CancellationToken.None);
        readings.TtlExceeded = 60;
        await monitor.PollCountersOnceAsync(CancellationToken.None);
        readings.TtlExceeded = 61;
        await Task.Delay(2);
        await monitor.PollCountersOnceAsync(CancellationToken.None);

        Assert.Equal(1, monitor.CurrentStatus.TtlExceeded.CountInWindow);
        Assert.True(monitor.CurrentStatus.TtlExceeded.IncidentOpen);
    }

    [Fact]
    public async Task A_dhcp_server_other_than_the_gateway_is_rogue_and_without_a_gateway_nothing_is()
    {
        IncidentStore store = FreshStore();
        IPAddress rogue = IPAddress.Parse("192.168.1.66");
        var readings = new Readings
        {
            Dhcp = new DhcpProbeResult(DhcpProbeStatus.Ok, [new DhcpOfferInfo(Gateway, IPAddress.Parse("192.168.1.50")), new DhcpOfferInfo(rogue, IPAddress.Parse("192.168.1.51"))]),
        };

        await using (NetworkHealthMonitor monitor = Monitor(store, readings, Gateway))
        {
            var opened = new TaskCompletionSource<Incident>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.IncidentOpened += i => opened.TrySetResult(i);
            monitor.Reconfigure(true, Off, Off, Off, new NetworkHealthThreshold(true, 0, 5));

            Incident incident = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("rogueDhcp", incident.ConditionKey);
            Assert.Equal([rogue], monitor.CurrentStatus.RogueDhcpServers);
            Assert.Equal(DhcpProbeStatus.Ok, monitor.CurrentStatus.LastRogueDhcpProbeStatus);
            Assert.True(monitor.CurrentStatus.RogueDhcp.IncidentOpen);
        }

        await using NetworkHealthMonitor blind = Monitor(FreshStore(), readings, null);
        var polled = new TaskCompletionSource<NetworkHealthStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        blind.StatusChanged += s =>
        {
            if (s.LastRogueDhcpProbeStatus == DhcpProbeStatus.Ok && s.RogueDhcp.Enabled)
            {
                polled.TrySetResult(s);
            }
        };
        blind.Reconfigure(true, Off, Off, Off, new NetworkHealthThreshold(true, 0, 5));

        NetworkHealthStatus status = await polled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(status.RogueDhcpServers);
        Assert.False(status.RogueDhcp.IncidentOpen);
    }

    [Fact]
    public async Task An_unavailable_dhcp_probe_is_reported_and_counts_no_rogue()
    {
        var readings = new Readings { Dhcp = new DhcpProbeResult(DhcpProbeStatus.Unavailable, []) };
        await using NetworkHealthMonitor monitor = Monitor(FreshStore(), readings, Gateway);

        await monitor.PollDhcpOnceAsync(CancellationToken.None);

        Assert.Equal(DhcpProbeStatus.Unavailable, monitor.CurrentStatus.LastRogueDhcpProbeStatus);
        Assert.Empty(monitor.CurrentStatus.RogueDhcpServers);
    }

    [Fact]
    public async Task An_incident_left_open_by_a_previous_run_is_resumed_and_closed_when_the_monitor_is_switched_off()
    {
        IncidentStore store = FreshStore();
        var leftOpen = new Incident
        {
            Id = store.NextId(DateTimeOffset.UtcNow),
            Cause = RootCause.NetworkHealth,
            ConditionKey = "packetLoss",
            StartUtc = DateTimeOffset.UtcNow.AddMinutes(-10),
            Headline = "left open",
        };
        store.Add(leftOpen);

        await using NetworkHealthMonitor monitor = Monitor(store, new Readings());
        Assert.True(monitor.CurrentStatus.PacketLoss.IncidentOpen);

        var closed = new List<Incident>();
        monitor.IncidentClosed += closed.Add;
        monitor.Reconfigure(false, Off, Off, Off, Off);

        Assert.Same(leftOpen, Assert.Single(closed));
        Assert.Equal(IncidentStatus.Resolved, leftOpen.Status);
        Assert.False(monitor.CurrentStatus.MonitoringEnabled);
        Assert.False(monitor.CurrentStatus.PacketLoss.IncidentOpen);
        Assert.Null(store.ActiveFor("packetLoss"));
    }
}
