using System.Net;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Tools.SpeedTest;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Monitors;

/// <summary>One condition's current reading, for the Diagnostics page summary card.</summary>
public sealed record NetworkHealthConditionStatus(bool Enabled, int CountInWindow, int Threshold, int WindowMinutes, bool IncidentOpen);

/// <summary>Everything the Diagnostics page's network-health card shows, as of the last poll.</summary>
public sealed record NetworkHealthStatus(
    bool MonitoringEnabled,
    NetworkHealthConditionStatus TcpRetransmissions,
    NetworkHealthConditionStatus PacketLoss,
    NetworkHealthConditionStatus TtlExceeded,
    NetworkHealthConditionStatus RogueDhcp,
    DhcpProbeStatus LastRogueDhcpProbeStatus,
    IReadOnlyList<IPAddress> RogueDhcpServers,
    DateTimeOffset? LastUpdatedUtc);

/// <summary>
/// Watches for packet-level trouble that the step pipeline never looks at: TCP retransmissions, ICMP
/// TTL-exceeded replies, ping-sweep packet loss, and a DHCP server other than the known gateway
/// answering a discovery broadcast. Independent of the pipeline, in the same spirit as
/// <see cref="KumaPusher"/> - no dependency on <see cref="Steps.StepPipeline"/>'s cycle, because
/// none of these conditions are tied to its cadence.
///
/// Also independent of <see cref="IncidentTracker"/>: that class allows exactly one active incident
/// at a time, debounced against one pipeline snapshot - not a fit for four conditions that open and
/// close on their own schedules. Incidents here go straight to <see cref="IncidentStore"/>, with one
/// open-or-null slot tracked per condition.
///
/// Two independent loops, both following <see cref="KumaPusher"/>'s stop/restart-on-change
/// approach: a fixed-interval counter loop samples <see cref="TcpStats"/>/<see cref="IcmpStats"/>
/// and runs a ping sweep, and a slower DHCP loop (its own interval, taken from the rogue-DHCP
/// threshold's window) broadcasts one discovery per tick.
/// </summary>
public sealed class NetworkHealthMonitor : IAsyncDisposable
{
    private const int CounterPollIntervalSeconds = 30;

    private static readonly TimeSpan DhcpListenWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(1000);
    private static readonly NetworkHealthConditionStatus DisabledCondition = new(false, 0, 0, 5, false);

    private readonly IncidentStore _incidents;
    private readonly Func<IReadOnlyList<IPAddress>> _pingTargets;

    /// <summary>
    /// Reads the pipeline's current gateway address. A delegate rather than a direct
    /// <see cref="Steps.StepPipeline"/> reference: this class only ever needs one field out of a
    /// snapshot that may not exist yet (e.g. before the first cycle completes), and a delegate lets
    /// the composition root decide where that comes from without this class depending on the
    /// pipeline's shape.
    /// </summary>
    private readonly Func<IPAddress?> _gatewayProvider;

    private readonly ConditionTracker _tcpRetransmissions = new("tcpRetransmissions", AggregationMode.Sum);
    private readonly ConditionTracker _packetLoss = new("packetLoss", AggregationMode.Average);
    private readonly ConditionTracker _ttlExceeded = new("ttlExceeded", AggregationMode.Sum);
    private readonly ConditionTracker _rogueDhcp = new("rogueDhcp", AggregationMode.Sum);

    private long? _lastTcpRetransmissions;
    private long? _lastTtlExceeded;
    private DhcpProbeStatus _lastDhcpProbeStatus = DhcpProbeStatus.Ok;
    private IReadOnlyList<IPAddress> _rogueDhcpServers = [];
    private DateTimeOffset? _lastUpdatedUtc;
    private bool _enabled;

    private CancellationTokenSource? _counterLoopCts;
    private Task? _counterLoopTask;
    private CancellationTokenSource? _dhcpLoopCts;
    private Task? _dhcpLoopTask;

    public NetworkHealthMonitor(IncidentStore incidents, Func<IReadOnlyList<IPAddress>> pingTargets, Func<IPAddress?> gatewayProvider)
    {
        _incidents = incidents;
        _pingTargets = pingTargets;
        _gatewayProvider = gatewayProvider;

        // Pick up what the previous run left open, so it is closed here - by the condition it
        // belongs to - rather than lingering as "active" for good.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (ConditionTracker tracker in new[] { _tcpRetransmissions, _packetLoss, _ttlExceeded, _rogueDhcp })
        {
            if (incidents.ActiveFor(tracker.Key) is { } open)
            {
                tracker.ActiveIncident = open;
                tracker.ResumedAt = now;
            }
        }

        PublishStatus();
    }

    public event Action<Incident>? IncidentOpened;

    public event Action<Incident>? IncidentClosed;

    /// <summary>Raised after every poll tick of either loop, for the Diagnostics page summary card.</summary>
    public event Action<NetworkHealthStatus>? StatusChanged;

    public NetworkHealthStatus CurrentStatus { get; private set; } =
        new(false, DisabledCondition, DisabledCondition, DisabledCondition, DisabledCondition, DhcpProbeStatus.Ok, [], null);

    /// <summary>
    /// Applies settings and starts/stops/restarts the loops as needed. Idempotent, matching
    /// <see cref="KumaPusher.Reconfigure"/> - Settings saves on every change, not just on an
    /// "Apply" button.
    /// </summary>
    public void Reconfigure(
        bool enabled,
        NetworkHealthThreshold tcpRetransmissions,
        NetworkHealthThreshold packetLoss,
        NetworkHealthThreshold ttlExceeded,
        NetworkHealthThreshold rogueDhcp)
    {
        _enabled = enabled;

        ApplyThreshold(_tcpRetransmissions, tcpRetransmissions);
        ApplyThreshold(_packetLoss, packetLoss);
        ApplyThreshold(_ttlExceeded, ttlExceeded);

        int previousDhcpWindowMinutes = _rogueDhcp.Threshold.WindowMinutes;
        ApplyThreshold(_rogueDhcp, rogueDhcp);

        if (!enabled)
        {
            // Nothing will measure these any more, so nothing would ever close them.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (ConditionTracker tracker in new[] { _tcpRetransmissions, _packetLoss, _ttlExceeded, _rogueDhcp })
            {
                CloseIncidentIfOpen(tracker, now);
            }

            StopCounterLoop();
            StopDhcpLoop();
            PublishStatus();
            return;
        }

        StartCounterLoop();

        var dhcpInterval = TimeSpan.FromMinutes(Math.Max(1, rogueDhcp.WindowMinutes));
        if (_dhcpLoopTask is null)
        {
            StartDhcpLoop(dhcpInterval);
        }
        else if (rogueDhcp.WindowMinutes != previousDhcpWindowMinutes)
        {
            StopDhcpLoop();
            StartDhcpLoop(dhcpInterval);
        }

        PublishStatus();
    }

    private void ApplyThreshold(ConditionTracker tracker, NetworkHealthThreshold threshold)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        tracker.Threshold = threshold;

        DateTimeOffset cutoff = now - TimeSpan.FromMinutes(Math.Max(1, threshold.WindowMinutes));
        tracker.Samples.RemoveAll(s => s.At < cutoff);

        if (!threshold.Enabled)
        {
            tracker.Samples.Clear();
            CloseIncidentIfOpen(tracker, now);
        }
    }

    private void StartCounterLoop()
    {
        if (_counterLoopTask is not null)
        {
            return;
        }

        _counterLoopCts = new CancellationTokenSource();
        _counterLoopTask = RunCounterLoopAsync(_counterLoopCts.Token);
    }

    private void StopCounterLoop()
    {
        _counterLoopCts?.Cancel();
        _counterLoopCts?.Dispose();
        _counterLoopCts = null;
        _counterLoopTask = null;
    }

    private void StartDhcpLoop(TimeSpan interval)
    {
        if (_dhcpLoopTask is not null)
        {
            return;
        }

        _dhcpLoopCts = new CancellationTokenSource();
        _dhcpLoopTask = RunDhcpLoopAsync(interval, _dhcpLoopCts.Token);
    }

    private void StopDhcpLoop()
    {
        _dhcpLoopCts?.Cancel();
        _dhcpLoopCts?.Dispose();
        _dhcpLoopCts = null;
        _dhcpLoopTask = null;
    }

    private async Task RunCounterLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(CounterPollIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollCountersOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/reconfigure path.
        }
    }

    /// <summary>
    /// One tick of the counter loop. The Windows counters only ever grow, so each sample is the
    /// difference since the previous tick (clamped at 0, for a counter reset); the first tick only
    /// sets the baseline. Packet loss is a ping to every configured address, as a percentage.
    /// </summary>
    private async Task PollCountersOnceAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_tcpRetransmissions.Threshold.Enabled && TcpStats.RetransmittedSegments() is { } retransmitted)
        {
            if (_lastTcpRetransmissions is { } previous)
            {
                RecordSample(_tcpRetransmissions, now, (int)Math.Clamp(retransmitted - previous, 0, int.MaxValue));
            }

            _lastTcpRetransmissions = retransmitted;
        }

        if (_ttlExceeded.Threshold.Enabled && IcmpStats.TimeExceededReceived() is { } ttlExceeded)
        {
            if (_lastTtlExceeded is { } previous)
            {
                RecordSample(_ttlExceeded, now, (int)Math.Clamp(ttlExceeded - previous, 0, int.MaxValue));
            }

            _lastTtlExceeded = ttlExceeded;
        }

        IReadOnlyList<IPAddress> pingTargets = _pingTargets();
        if (_packetLoss.Threshold.Enabled && pingTargets.Count > 0)
        {
            IReadOnlyList<(IPAddress Address, PingOutcome Outcome)> results = await IcmpPing
                .SendAllAsync(pingTargets, PingTimeout, cancellationToken)
                .ConfigureAwait(false);

            int lostPercent = (int)Math.Round(100.0 * results.Count(r => !r.Outcome.Success) / results.Count);
            RecordSample(_packetLoss, now, lostPercent);
        }

        EvaluateCondition(_tcpRetransmissions, now);
        EvaluateCondition(_ttlExceeded, now);
        EvaluateCondition(_packetLoss, now);
        PublishStatus();
    }

    private async Task RunDhcpLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        // Probe immediately, then wait - a freshly enabled condition shows a reading right away
        // rather than after a full interval, matching KumaPusher.RunLoopAsync's reasoning.
        await PollDhcpOnceAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await PollDhcpOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown/reconfigure path.
        }
    }

    private async Task PollDhcpOnceAsync(CancellationToken cancellationToken)
    {
        DhcpProbeResult result = await DhcpDiscoverProbe.DiscoverAsync(DhcpListenWindow, cancellationToken).ConfigureAwait(false);
        _lastDhcpProbeStatus = result.Status;

        if (result.Status != DhcpProbeStatus.Ok || !_rogueDhcp.Threshold.Enabled)
        {
            _rogueDhcpServers = [];
            PublishStatus();
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IPAddress? gateway = _gatewayProvider();

        // No known gateway yet (e.g. the pipeline has not completed its first cycle) - never call
        // a server rogue without something known-good to compare it against.
        IReadOnlyList<IPAddress> rogueServers = gateway is null
            ? []
            : [.. result.Offers.Select(o => o.Server).Where(s => !s.Equals(gateway)).Distinct()];
        _rogueDhcpServers = rogueServers;

        _rogueDhcp.Samples.Clear();
        _rogueDhcp.Samples.Add((now, rogueServers.Count));
        EvaluateCondition(_rogueDhcp, now);
        PublishStatus();
    }

    private static void RecordSample(ConditionTracker tracker, DateTimeOffset at, int value)
    {
        if (!tracker.Threshold.Enabled)
        {
            return;
        }

        tracker.Samples.Add((at, value));

        DateTimeOffset cutoff = at - TimeSpan.FromMinutes(Math.Max(1, tracker.Threshold.WindowMinutes));
        tracker.Samples.RemoveAll(s => s.At < cutoff);
    }

    /// <summary>
    /// Opens an incident when a condition crosses its threshold within its window, and closes it
    /// once it no longer does. Counts are summed over the window; packet loss, already a rate, is averaged.
    /// </summary>
    private void EvaluateCondition(ConditionTracker tracker, DateTimeOffset now)
    {
        if (!tracker.Threshold.Enabled)
        {
            tracker.CountInWindow = 0;
            return;
        }

        int countInWindow = tracker.Mode == AggregationMode.Sum
            ? tracker.Samples.Sum(s => s.Count)
            : tracker.Samples.Count > 0 ? (int)Math.Round(tracker.Samples.Average(s => s.Count)) : 0;

        tracker.CountInWindow = countInWindow;
        bool breaching = countInWindow > tracker.Threshold.MaxOccurrences;

        if (breaching && tracker.ActiveIncident is null)
        {
            Incident incident = BuildIncident(tracker, countInWindow, now);
            _incidents.Add(incident);
            tracker.ActiveIncident = incident;
            IncidentOpened?.Invoke(incident);
        }
        else if (!breaching)
        {
            // An incident resumed after a restart is judged on a full window of fresh samples:
            // right after start-up the window is empty, which says nothing about the condition.
            if (tracker.ResumedAt is { } resumed && now - resumed < TimeSpan.FromMinutes(Math.Max(1, tracker.Threshold.WindowMinutes)))
            {
                return;
            }

            CloseIncidentIfOpen(tracker, now);
        }
    }

    private void CloseIncidentIfOpen(ConditionTracker tracker, DateTimeOffset now)
    {
        if (tracker.ActiveIncident is not { } active)
        {
            return;
        }

        active.Status = IncidentStatus.Resolved;
        active.EndUtc = now;
        _incidents.Update(active);
        tracker.ActiveIncident = null;
        tracker.ResumedAt = null;
        IncidentClosed?.Invoke(active);
    }

    /// <summary>
    /// A network-health incident, worded from the <c>networkHealth.{key}.*</c> texts with the count
    /// and window. When a speed test ran within the window, the cause and advice say so.
    /// </summary>
    private Incident BuildIncident(ConditionTracker tracker, int countInWindow, DateTimeOffset now)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        string prefix = $"networkHealth.{tracker.Key}";
        bool speedTest = SpeedTestActivity.During(now - TimeSpan.FromMinutes(Math.Max(1, tracker.Threshold.WindowMinutes)), now);
        (string causeKey, string todoKey) = ExplanationKeys(tracker.Key, speedTest);

        return new Incident
        {
            Id = _incidents.NextId(now),
            Cause = RootCause.NetworkHealth,
            ConditionKey = tracker.Key,
            StartUtc = now,
            Status = IncidentStatus.Active,
            Headline = loc.Format($"{prefix}.headline", countInWindow, tracker.Threshold.WindowMinutes),
            Explanation = new Explanation(
                loc.Get($"{prefix}.what"),
                loc.Format(causeKey, countInWindow, tracker.Threshold.WindowMinutes),
                loc.Get(todoKey)),
        };
    }

    /// <summary>
    /// The cause and advice texts for a condition. Retransmissions and packet loss have a variant
    /// for a window in which a speed test ran: filling the line on purpose makes both go up, which
    /// is then expected rather than a fault. Public for the tests.
    /// </summary>
    public static (string CauseKey, string TodoKey) ExplanationKeys(string conditionKey, bool speedTestDuring)
    {
        string prefix = $"networkHealth.{conditionKey}";
        return speedTestDuring && conditionKey is "tcpRetransmissions" or "packetLoss"
            ? ($"{prefix}.speedTest.cause", $"{prefix}.speedTest.todo")
            : ($"{prefix}.cause", $"{prefix}.todo");
    }

    /// <summary>Snapshots every condition for the Diagnostics page and raises <see cref="StatusChanged"/>.</summary>
    private void PublishStatus()
    {
        _lastUpdatedUtc = DateTimeOffset.UtcNow;

        var status = new NetworkHealthStatus(
            _enabled,
            ToStatus(_tcpRetransmissions),
            ToStatus(_packetLoss),
            ToStatus(_ttlExceeded),
            ToStatus(_rogueDhcp),
            _lastDhcpProbeStatus,
            _rogueDhcpServers,
            _lastUpdatedUtc);

        CurrentStatus = status;
        StatusChanged?.Invoke(status);
    }

    private static NetworkHealthConditionStatus ToStatus(ConditionTracker tracker) => new(
        tracker.Threshold.Enabled,
        tracker.CountInWindow,
        tracker.Threshold.MaxOccurrences,
        tracker.Threshold.WindowMinutes,
        tracker.ActiveIncident is not null);

    public async ValueTask DisposeAsync()
    {
        _counterLoopCts?.Cancel();
        _dhcpLoopCts?.Cancel();

        foreach (Task? task in new[] { _counterLoopTask, _dhcpLoopTask })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _counterLoopCts?.Dispose();
        _dhcpLoopCts?.Dispose();
    }

    private enum AggregationMode
    {
        /// <summary>Samples are counts that add up over the window - retransmissions, TTL errors, rogue-server sightings.</summary>
        Sum,

        /// <summary>Samples are already a rate (a percentage) - averaging them, not summing, is what makes the window meaningful.</summary>
        Average,
    }

    private sealed class ConditionTracker(string key, AggregationMode mode)
    {
        public string Key { get; } = key;

        public AggregationMode Mode { get; } = mode;

        public NetworkHealthThreshold Threshold { get; set; } = new(false, 0, 5);

        public List<(DateTimeOffset At, int Count)> Samples { get; } = [];

        public int CountInWindow { get; set; }

        public Incident? ActiveIncident { get; set; }

        /// <summary>When an incident left open by the previous run was picked up; null otherwise.</summary>
        public DateTimeOffset? ResumedAt { get; set; }
    }
}
