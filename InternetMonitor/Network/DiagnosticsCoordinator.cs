using Microsoft.Win32;
using InternetMonitor.Configuration;
using InternetMonitor.Network.Diagnosis;
using InternetMonitor.Network.Diagnostics;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network;

public sealed class ProbeCompletedEventArgs : EventArgs
{
    public required string ProbeId { get; init; }
    public required IProbeResult Result { get; init; }
}

/// <summary>
/// Owns every diagnostic probe (the fixed core set plus the user's configured application
/// endpoints), runs them together on a shared poll cycle, builds a <see cref="ProbeSnapshot"/>,
/// and hands it to <see cref="DiagnosisEngine"/>. This is the data source for both the simple
/// status popup and the professional Diagnostics screen - it has no UI dependency and raises
/// events on thread-pool threads; consumers marshal to their own UI thread.
/// </summary>
public sealed class DiagnosticsCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>Fixed probe id for the continuous ping-latency graph, independent of the main poll cycle.</summary>
    public const string PingProbeId = "ping-target";
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    // NTP pools expect clients to poll on the order of minutes to hours, not seconds - querying
    // pool.ntp.org every 15s (the main cycle's cadence) is excessive and can look like abuse.
    // Checked at startup, once an hour otherwise, and immediately on an OS-level clock change
    // (see RunTimeSyncLoopAsync) rather than on the main cycle.
    private static readonly TimeSpan TimeSyncInterval = TimeSpan.FromHours(1);

    // Deliberately NOT www.ripe.net here even though DnsResolutionProbe uses it: ripe.net is
    // served via Akamai's CDN, and live testing showed 1-3+ second, highly variable response
    // times depending on which edge node DNS round-robin happens to select (confirmed: curl and
    // this app resolved to different Akamai edge IPs on different calls). That's real latency,
    // not a bug, but it makes ripe.net a poor choice for "is HTTPS reachable at all".
    //
    // European-first, no US service involved: Volla (Germany) runs the same AOSP-style
    // /generate_204 captive-portal check used elsewhere in this app - verified live to return a
    // fast (<300ms), validly-certified HTTP 204 over HTTPS.
    private const string GeneralHttpsUrl = "https://connectivitycheck.volla.tech/generate_204";

    private readonly NetworkInterfaceProbe _networkProbe = new();
    private readonly IpAddressProbe _ipProbe = new();
    private readonly GatewayProbe _gatewayProbe = new();
    private readonly InternetProbe _internetProbe = new();
    private readonly DnsResolutionProbe _dnsProbe = new();
    private readonly TimeSyncProbe _timeSyncProbe = new();
    private readonly HttpsEndpointProbe _generalHttpsProbe = new("https", "Network", GeneralHttpsUrl, TimeSpan.FromSeconds(5));

    // Volatile + always read into a local once per cycle (see RunCycleAsync): UpdateEndpoints can
    // reassign this from the UI thread at any time, including mid-cycle. Reading the field twice
    // (once to build tasks, once to zip names back onto results) would risk pairing endpoint
    // names from a new list against IProbeResults from an old list's tasks.
    private volatile List<(string Name, ApplicationEndpointProbe Probe)> _applicationProbes = [];
    private PingLatencyProbe _pingProbe = new(PingProbeId, "9.9.9.9", PingTimeout);
    private readonly object _lifecycleLock = new();
    private readonly IncidentTracker _incidentTracker;
    private readonly DiagnosticLogger _diagnosticLogger;
    private readonly ProbeHistory _history = new();

    // Placeholder so the very first cycle (which can run before the startup TimeSync check
    // completes) always has a value for ProbeSnapshot.TimeSync. ProbeStatus.Ok rather than
    // Unknown/Warning is deliberate: those statuses mean something specific to DiagnosisEngine
    // ("time server unreachable" / "significant drift") and would misreport a problem that
    // doesn't exist yet - this is replaced within moments by the real result regardless.
    private TimeSyncProbeResult _latestTimeSyncResult;

    public DiagnosticsCoordinator(IncidentTracker incidentTracker, DiagnosticLogger diagnosticLogger)
    {
        _incidentTracker = incidentTracker;
        _diagnosticLogger = diagnosticLogger;
        _latestTimeSyncResult = new TimeSyncProbeResult(
            _timeSyncProbe.Id, ProbeStatus.Ok, string.Empty, TimeSpan.Zero, DateTimeOffset.UtcNow,
            TimeSyncProbe.DefaultServer, null, true);
    }

    private PeriodicTimer? _timer;
    private PeriodicTimer? _pingTimer;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource _timeSyncKickCts = new();
    private Task? _loopTask;
    private Task? _pingLoopTask;
    private Task? _timeSyncLoopTask;
    private bool _cycleInFlight;

    /// <summary>Raised once, right before a cycle's probes are kicked off - lets a UI reset any per-cycle state (e.g. StatusPopupForm's ordered-reveal tracking) before ProbeCompleted starts firing for the new cycle.</summary>
    public event EventHandler? CycleStarted;
    public event EventHandler<ProbeCompletedEventArgs>? ProbeCompleted;
    public event EventHandler<ProbeSnapshot>? SnapshotUpdated;
    public event EventHandler<DiagnosisResult>? DiagnosisUpdated;

    public ProbeSnapshot? LatestSnapshot { get; private set; }
    public DiagnosisResult? LatestDiagnosis { get; private set; }
    public DateTimeOffset? LastCheckUtc { get; private set; }
    public DateTimeOffset? NextCheckUtc { get; private set; }
    public bool IsChecking { get; private set; }
    public IProbeResult? LatestPingResult { get; private set; }

    public IReadOnlyList<(string Id, string Name)> ApplicationEndpointDescriptors =>
        _applicationProbes.Select(ap => (ap.Probe.Id, ap.Name)).ToList();

    public IReadOnlyList<(DateTimeOffset Timestamp, double Value)> GetHistory(string probeId) => _history.Get(probeId);

    public void UpdateEndpoints(IEnumerable<EndpointConfig> endpoints)
    {
        _applicationProbes = endpoints
            .Where(e => e.Enabled)
            .Select(e => (e.Name, new ApplicationEndpointProbe(e)))
            .ToList();
    }

    /// <summary>Repoints the continuous ping graph at a new target and discards its old history - mixing latencies to two different hosts in one graph would be misleading.</summary>
    public void UpdatePingTarget(string address)
    {
        _pingProbe = new PingLatencyProbe(PingProbeId, address, PingTimeout);
        _history.Clear(PingProbeId);
    }

    public void ClearPingHistory() => _history.Clear(PingProbeId);

    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_loopTask is not null)
            {
                return;
            }

            _loopCts = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCts.Token);
            _pingLoopTask = RunPingLoopAsync(_loopCts.Token);
            _timeSyncLoopTask = RunTimeSyncLoopAsync(_loopCts.Token);
        }
    }

    /// <summary>Forces an immediate full cycle outside the normal poll schedule (e.g. "Check now" or opening the status window).</summary>
    public Task RunNowAsync(CancellationToken cancellationToken = default) => RunCycleAsync(cancellationToken);

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        _timer = new PeriodicTimer(PollInterval);

        // Run one cycle immediately on startup rather than waiting a full interval.
        await RunCycleAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunCycleAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>
    /// Independent fast-cadence loop for the continuous ping-latency graph. Runs on its own
    /// 1-second timer rather than the shared 15-second <see cref="PollInterval"/> - it is a
    /// monitoring/visualization addition only, not part of <see cref="ProbeSnapshot"/> or
    /// <see cref="DiagnosisEngine"/>.
    /// </summary>
    private async Task RunPingLoopAsync(CancellationToken cancellationToken)
    {
        _pingTimer = new PeriodicTimer(PingInterval);

        LatestPingResult = await RunTypedAsync<IProbeResult>(_pingProbe, cancellationToken).ConfigureAwait(false);

        try
        {
            while (await _pingTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                LatestPingResult = await RunTypedAsync<IProbeResult>(_pingProbe, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>
    /// Independent slow-cadence loop for TimeSync (see the rationale on <see cref="TimeSyncInterval"/>):
    /// checks once immediately (startup), then again either after an hour or as soon as
    /// <see cref="OnSystemTimeChanged"/> wakes it early, whichever comes first - a plain
    /// cancellable delay rather than PeriodicTimer, since PeriodicTimer only supports one
    /// in-flight wait at a time and can't be woken early by an unrelated event.
    /// </summary>
    private async Task RunTimeSyncLoopAsync(CancellationToken cancellationToken)
    {
        _latestTimeSyncResult = await RunTypedAsync<TimeSyncProbeResult>(_timeSyncProbe, cancellationToken).ConfigureAwait(false);

        SystemEvents.TimeChanged += OnSystemTimeChanged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeSyncKickCts.Token);
                try
                {
                    await Task.Delay(TimeSyncInterval, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Woken early by a system time change, not shutdown - fall through and re-check.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _latestTimeSyncResult = await RunTypedAsync<TimeSyncProbeResult>(_timeSyncProbe, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
        finally
        {
            SystemEvents.TimeChanged -= OnSystemTimeChanged;
        }
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e)
    {
        CancellationTokenSource old = Interlocked.Exchange(ref _timeSyncKickCts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (_cycleInFlight)
        {
            return;
        }

        _cycleInFlight = true;
        IsChecking = true;
        CycleStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            Task<NetworkInterfaceProbeResult> networkTask = RunTypedAsync<NetworkInterfaceProbeResult>(_networkProbe, cancellationToken);
            Task<IpAddressProbeResult> ipTask = RunTypedAsync<IpAddressProbeResult>(_ipProbe, cancellationToken);
            Task<GatewayProbeResult> gatewayTask = RunTypedAsync<GatewayProbeResult>(_gatewayProbe, cancellationToken);
            Task<InternetProbeResult> internetTask = RunTypedAsync<InternetProbeResult>(_internetProbe, cancellationToken);
            Task<DnsResolutionProbeResult> dnsTask = RunTypedAsync<DnsResolutionProbeResult>(_dnsProbe, cancellationToken);
            Task<HttpsEndpointProbeResult> httpsTask = RunTypedAsync<HttpsEndpointProbeResult>(_generalHttpsProbe, cancellationToken);

            // Captured once into a local: _applicationProbes can be reassigned by UpdateEndpoints
            // from the UI thread at any time, so both uses below must agree on the same list.
            List<(string Name, ApplicationEndpointProbe Probe)> applicationProbes = _applicationProbes;
            List<Task<IProbeResult>> appResultTasks = applicationProbes
                .Select(ap => RunTypedAsync<IProbeResult>(ap.Probe, cancellationToken))
                .ToList();

            await Task.WhenAll(
                new Task[] { networkTask, ipTask, gatewayTask, internetTask, dnsTask, httpsTask }
                    .Concat(appResultTasks)).ConfigureAwait(false);

            var applicationResults = applicationProbes
                .Zip(appResultTasks, (ap, task) => (ap.Name, Result: task.Result))
                .ToList();

            var snapshot = new ProbeSnapshot(
                networkTask.Result, ipTask.Result, gatewayTask.Result, internetTask.Result,
                dnsTask.Result, httpsTask.Result, _latestTimeSyncResult, applicationResults);

            LatestSnapshot = snapshot;
            LastCheckUtc = DateTimeOffset.UtcNow;
            NextCheckUtc = LastCheckUtc + PollInterval;

            DiagnosisResult diagnosis = DiagnosisEngine.Diagnose(snapshot);
            LatestDiagnosis = diagnosis;

            Incident? beforeIncident = _incidentTracker.CurrentIncident;
            _incidentTracker.Observe(diagnosis, snapshot);
            Incident? afterIncident = _incidentTracker.CurrentIncident;

            if (beforeIncident is null && afterIncident is not null)
            {
                _diagnosticLogger.LogIncidentOpened(afterIncident);
            }
            else if (beforeIncident is not null && afterIncident is null)
            {
                _diagnosticLogger.LogIncidentResolved(beforeIncident);
            }

            _diagnosticLogger.LogCycle(snapshot, diagnosis, afterIncident?.Id);

            SnapshotUpdated?.Invoke(this, snapshot);
            DiagnosisUpdated?.Invoke(this, diagnosis);
        }
        finally
        {
            IsChecking = false;
            _cycleInFlight = false;
        }
    }

    private async Task<TResult> RunTypedAsync<TResult>(IProbe probe, CancellationToken cancellationToken)
        where TResult : IProbeResult
    {
        IProbeResult result = await probe.RunAsync(cancellationToken).ConfigureAwait(false);
        if (result.ChartValue is { } value)
        {
            _history.Record(probe.Id, result.TimestampUtc, value);
        }

        ProbeCompleted?.Invoke(this, new ProbeCompletedEventArgs { ProbeId = probe.Id, Result = result });
        return (TResult)result;
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        if (_pingLoopTask is not null)
        {
            try
            {
                await _pingLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        if (_timeSyncLoopTask is not null)
        {
            try
            {
                await _timeSyncLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _timer?.Dispose();
        _pingTimer?.Dispose();
        _loopCts?.Dispose();
        _timeSyncKickCts.Dispose();
    }
}
