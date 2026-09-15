using System.Net.NetworkInformation;
using Microsoft.Win32;
using InternetMonitor.Configuration;
using InternetMonitor.Localization;
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

    /// <summary>Probe/history id for a given ping target's continuous ping-latency graph, independent of the main poll cycle.</summary>
    public static string PingProbeId(string targetId) => $"ping-target:{targetId}";

    public const string SpeedTestDownloadProbeId = "speedtest-download";
    public const string SpeedTestUploadProbeId = "speedtest-upload";

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
    private HttpsEndpointProbe _generalHttpsProbe = new("https", "Network", GeneralHttpsUrl, TimeSpan.FromSeconds(5));
    private readonly WifiSignalProbe _wifiProbe = new();

    // Reused across every automatic run (not recreated per run) since it owns a real HttpClient -
    // matches SpeedTestForm's own instance-per-form-lifetime reuse of a SpeedTestClient.
    private readonly SpeedTestClient _autoSpeedTestClient = new();

    // Volatile + always read into a local once per cycle (see RunCycleAsync): UpdateEndpoints can
    // reassign this from the UI thread at any time, including mid-cycle. Reading the field twice
    // (once to build tasks, once to zip names back onto results) would risk pairing endpoint
    // names from a new list against IProbeResults from an old list's tasks.
    private volatile List<(EndpointConfig Config, ApplicationEndpointProbe Probe)> _applicationProbes = [];

    // Same volatile-plus-captured-local discipline as _applicationProbes: UpdatePingTargets can
    // reassign this from the UI thread at any time, including mid-tick.
    private volatile List<(PingTargetConfig Config, PingLatencyProbe Probe)> _pingTargets = [];
    private volatile bool _timeSyncCheckEnabled = true;
    private volatile bool _speedTestAutoRunEnabled;
    private volatile int _speedTestIntervalHours = 4;
    private int _latencyWarningMs = 300;
    private int _latencyErrorMs = 1000;
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
    private CancellationTokenSource _speedTestKickCts = new();
    private Task? _loopTask;
    private Task? _pingLoopTask;
    private Task? _timeSyncLoopTask;
    private Task? _speedTestLoopTask;

    // 0 = idle, 1 = running. Interlocked, not a plain bool: RunNowAsync can be invoked
    // concurrently with the scheduled loop's own call to RunCycleAsync (from
    // TrayApplicationContext on every connectivity state change, or from an open
    // StatusPopupForm's "Check now"), so a plain check-then-set bool is a real race that would
    // let two cycles overlap and pile up during a slow/flapping incident.
    private int _cycleInFlight;

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
    public IReadOnlyList<(PingTargetConfig Config, IProbeResult Result)> LatestPingResults { get; private set; } = [];

    /// <summary>Null whenever the active adapter isn't wireless - see <see cref="RunCycleAsync"/>. Never run at all on a wired connection, so this never shows a stale reading from a Wi-Fi adapter the machine no longer uses.</summary>
    public IProbeResult? LatestWifiResult { get; private set; }

    /// <summary>
    /// Null until the first speed test - manual (the Speed Test screen's Run button, via
    /// <see cref="RecordSpeedTestResult"/>) or automatic (<see cref="RunAutoSpeedTestAsync"/>) -
    /// completes; either path updates this the same way. The Diagnostics screen only shows the
    /// two rows once this is non-null.
    /// </summary>
    public (IProbeResult Download, IProbeResult Upload)? LatestSpeedTestResults { get; private set; }

    public IReadOnlyList<(string Id, string Name)> ApplicationEndpointDescriptors =>
        _applicationProbes.Select(ap => (ap.Probe.Id, ap.Config.Name)).ToList();

    public IReadOnlyList<(DateTimeOffset Timestamp, double Value)> GetHistory(string probeId) => _history.Get(probeId);

    public void UpdateEndpoints(IEnumerable<EndpointConfig> endpoints)
    {
        _applicationProbes = endpoints
            .Where(e => e.Enabled)
            .Select(e => (e, new ApplicationEndpointProbe(e, _latencyWarningMs, _latencyErrorMs)))
            .ToList();
    }

    /// <summary>
    /// (Re)builds the set of continuous-ping probes from the user's configured target list
    /// (disabled entries are dropped). A target whose address hasn't changed since the previous
    /// call keeps its existing probe instance (and therefore its in-memory state); a genuinely new
    /// target, or one whose address changed, gets a fresh probe and has its old history discarded
    /// - mixing latencies to two different hosts under one history key would be misleading.
    /// </summary>
    public void UpdatePingTargets(IEnumerable<PingTargetConfig> targets)
    {
        Dictionary<string, PingLatencyProbe> previousById = _pingTargets.ToDictionary(p => p.Config.Id, p => p.Probe);

        var updated = new List<(PingTargetConfig Config, PingLatencyProbe Probe)>();
        foreach (PingTargetConfig target in targets.Where(t => t.Enabled))
        {
            string probeId = PingProbeId(target.Id);
            if (previousById.TryGetValue(target.Id, out PingLatencyProbe? existingProbe) && existingProbe.TargetAddress == target.Address)
            {
                updated.Add((target, existingProbe));
            }
            else
            {
                _history.Clear(probeId);
                updated.Add((target, new PingLatencyProbe(probeId, target.Address, PingTimeout, _latencyWarningMs, _latencyErrorMs)));
            }
        }

        _pingTargets = updated;
    }

    /// <summary>Live-reconfigures every latency-aware probe's Warning/Error thresholds (see <see cref="LatencyClassifier"/>) without waiting for the next poll cycle or a fresh <see cref="UpdateEndpoints"/> call.</summary>
    public void UpdateLatencyThresholds(int warningMs, int errorMs)
    {
        _latencyWarningMs = warningMs;
        _latencyErrorMs = errorMs;
        _gatewayProbe.WarningThresholdMs = warningMs;
        _gatewayProbe.ErrorThresholdMs = errorMs;
        _pingTargets = _pingTargets
            .Select(p => (p.Config, new PingLatencyProbe(PingProbeId(p.Config.Id), p.Probe.TargetAddress, PingTimeout, warningMs, errorMs)))
            .ToList();
        _generalHttpsProbe = new HttpsEndpointProbe("https", "Network", GeneralHttpsUrl, TimeSpan.FromSeconds(5), warningMs, errorMs);
        _applicationProbes = _applicationProbes
            .Select(ap => (ap.Config, new ApplicationEndpointProbe(ap.Config, warningMs, errorMs)))
            .ToList();
    }

    public void ClearPingHistory(string targetId) => _history.Clear(PingProbeId(targetId));

    /// <summary>Live-toggles the time-sync check and wakes the loop immediately (reusing the same early-wake mechanism <see cref="OnSystemTimeChanged"/> uses) so the change is reflected right away rather than waiting up to an hour.</summary>
    public void UpdateTimeSyncCheckEnabled(bool enabled)
    {
        _timeSyncCheckEnabled = enabled;
        WakeTimeSyncLoop();
    }

    /// <summary>Live-reconfigures how long persisted probe history is retained (see <see cref="ProbeHistory.SetRetention"/>).</summary>
    public void UpdateHistoryRetention(TimeSpan window) => _history.SetRetention(window);

    /// <summary>
    /// Live-toggles automatic speed testing and/or its interval, waking the loop immediately (same
    /// early-wake idiom as <see cref="WakeTimeSyncLoop"/>) so a change is reflected right away
    /// rather than waiting out however long was left on the previous interval.
    /// </summary>
    public void UpdateSpeedTestAutoRun(bool enabled, int intervalHours)
    {
        _speedTestAutoRunEnabled = enabled;
        _speedTestIntervalHours = intervalHours;
        WakeSpeedTestLoop();
    }

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
            _speedTestLoopTask = RunAutoSpeedTestLoopAsync(_loopCts.Token);
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

        await RunAllPingTargetsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (await _pingTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await RunAllPingTargetsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>Runs every currently-configured, enabled ping target concurrently - same captured-local discipline as the main cycle's application-endpoint probes, since <see cref="_pingTargets"/> can be reassigned mid-tick by <see cref="UpdatePingTargets"/>.</summary>
    private async Task RunAllPingTargetsAsync(CancellationToken cancellationToken)
    {
        List<(PingTargetConfig Config, PingLatencyProbe Probe)> targets = _pingTargets;
        if (targets.Count == 0)
        {
            LatestPingResults = [];
            return;
        }

        List<Task<IProbeResult>> tasks = targets
            .Select(t => RunTypedAsync<IProbeResult>(t.Probe, cancellationToken))
            .ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        LatestPingResults = targets.Zip(tasks, (t, task) => (t.Config, task.Result)).ToList();
    }

    /// <summary>
    /// Independent slow-cadence loop for TimeSync (see the rationale on <see cref="TimeSyncInterval"/>):
    /// checks once immediately (startup), then again either after an hour or as soon as
    /// <see cref="WakeTimeSyncLoop"/> wakes it early (a system clock change, or a live
    /// Settings toggle of <see cref="UpdateTimeSyncCheckEnabled"/>), whichever comes first - a
    /// plain cancellable delay rather than PeriodicTimer, since PeriodicTimer only supports one
    /// in-flight wait at a time and can't be woken early by an unrelated event.
    /// </summary>
    private async Task RunTimeSyncLoopAsync(CancellationToken cancellationToken)
    {
        _latestTimeSyncResult = await GetTimeSyncResultAsync(cancellationToken).ConfigureAwait(false);

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
                    // Woken early by a system time change or a live enabled/disabled toggle, not
                    // shutdown - fall through and re-check.
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _latestTimeSyncResult = await GetTimeSyncResultAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Runs the real NTP probe when enabled; otherwise returns a neutral "disabled" reading
    /// without ever touching the probe, its history, or ProbeCompleted - toggling this off
    /// shouldn't record a meaningless flat datapoint into the time-sync history chart. Uses
    /// ProbeStatus.Ok deliberately (same reasoning as the startup placeholder in the constructor)
    /// so DiagnosisEngine's Unknown/Warning TimeSync branches never fire while disabled.
    /// </summary>
    private async Task<TimeSyncProbeResult> GetTimeSyncResultAsync(CancellationToken cancellationToken)
    {
        if (!_timeSyncCheckEnabled)
        {
            return new TimeSyncProbeResult(
                _timeSyncProbe.Id, ProbeStatus.Ok, LocalizationManager.Instance.Get("diag.timeSync.disabled"), TimeSpan.Zero, DateTimeOffset.UtcNow,
                TimeSyncProbe.DefaultServer, null, true);
        }

        return await RunTypedAsync<TimeSyncProbeResult>(_timeSyncProbe, cancellationToken).ConfigureAwait(false);
    }

    private void OnSystemTimeChanged(object? sender, EventArgs e) => WakeTimeSyncLoop();

    private void WakeTimeSyncLoop()
    {
        CancellationTokenSource old = Interlocked.Exchange(ref _timeSyncKickCts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    /// <summary>
    /// Independent loop for automatic speed testing - same plain-Task.Delay-plus-kick-CTS shape as
    /// <see cref="RunTimeSyncLoopAsync"/>, for the same reason (needs to be woken early by a live
    /// Settings change, which PeriodicTimer can't do). Waits the full configured interval - or
    /// indefinitely while disabled, so a disabled auto-run doesn't wake up every few hours to do
    /// nothing - before each run, and deliberately does NOT run once immediately on startup or on
    /// enabling: the manual Run button already covers "test right now", so enabling auto-run only
    /// ever schedules a *future* run, never an implicit immediate one.
    /// </summary>
    private async Task RunAutoSpeedTestLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = _speedTestAutoRunEnabled ? TimeSpan.FromHours(_speedTestIntervalHours) : Timeout.InfiniteTimeSpan;
                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _speedTestKickCts.Token);
                try
                {
                    await Task.Delay(delay, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Woken early by a live Settings change, not shutdown - re-loop to pick up the
                    // new enabled/interval state (and possibly go back to sleep immediately).
                    continue;
                }

                if (_speedTestAutoRunEnabled)
                {
                    try
                    {
                        await RunAutoSpeedTestAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException)
                    {
                        // Best-effort background job - a failed automatic run shouldn't stop future
                        // ones; just skip this cycle and try again next interval.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path.
        }
    }

    /// <summary>
    /// Runs one headless speed test (no IProgress - nothing is watching) via the automatic loop's
    /// own client, then records it the same way a manual run does - see
    /// <see cref="RecordSpeedTestResult"/>.
    /// </summary>
    private async Task RunAutoSpeedTestAsync(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SpeedTestResult result = await _autoSpeedTestClient.RunAsync(null, cancellationToken).ConfigureAwait(false);
        sw.Stop();
        RecordSpeedTestResult(result, sw.Elapsed);
    }

    /// <summary>
    /// Records a completed speed test's download/upload throughput as two independently
    /// chartable rows, using the exact same ProbeHistory.Record + ProbeCompleted mechanism every
    /// other probe result goes through - SpeedTestClient itself isn't an IProbe, so this can't go
    /// through RunTypedAsync like the rest of the app; it does the same two steps by hand instead
    /// (same as RunCycleAsync already does for the per-address "internet:{address}" rows). Public
    /// and called from two places: the automatic loop (<see cref="RunAutoSpeedTestAsync"/>) and
    /// SpeedTestForm's "Run" button - a manual run makes its result visible on the Diagnostics
    /// screen too, not just automatic ones, so you don't have to wait out a whole interval just to
    /// see whether this is working.
    /// </summary>
    public void RecordSpeedTestResult(SpeedTestResult result, TimeSpan duration)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var downloadResult = new SpeedTestProbeResult(
            SpeedTestDownloadProbeId, ProbeStatus.Ok, $"{result.DownloadMbps:F1} Mbps", duration, now, result.DownloadMbps);
        var uploadResult = new SpeedTestProbeResult(
            SpeedTestUploadProbeId, ProbeStatus.Ok, $"{result.UploadMbps:F1} Mbps", duration, now, result.UploadMbps);

        _history.Record(SpeedTestDownloadProbeId, now, result.DownloadMbps);
        _history.Record(SpeedTestUploadProbeId, now, result.UploadMbps);

        LatestSpeedTestResults = (downloadResult, uploadResult);
        ProbeCompleted?.Invoke(this, new ProbeCompletedEventArgs { ProbeId = SpeedTestDownloadProbeId, Result = downloadResult });
        ProbeCompleted?.Invoke(this, new ProbeCompletedEventArgs { ProbeId = SpeedTestUploadProbeId, Result = uploadResult });
        _diagnosticLogger.LogSpeedTestCompleted(result, duration);
    }

    private void WakeSpeedTestLoop()
    {
        CancellationTokenSource old = Interlocked.Exchange(ref _speedTestKickCts, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
        {
            return;
        }

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
            List<(EndpointConfig Config, ApplicationEndpointProbe Probe)> applicationProbes = _applicationProbes;
            List<Task<IProbeResult>> appResultTasks = applicationProbes
                .Select(ap => RunTypedAsync<IProbeResult>(ap.Probe, cancellationToken))
                .ToList();

            await Task.WhenAll(
                new Task[] { networkTask, ipTask, gatewayTask, internetTask, dnsTask, httpsTask }
                    .Concat(appResultTasks)).ConfigureAwait(false);

            var applicationResults = applicationProbes
                .Zip(appResultTasks, (ap, task) => (Name: ap.Config.Name, Result: task.Result))
                .ToList();

            // InternetProbeResult only records its aggregate reachable-count under the "internet"
            // probe id (see PingLatencyProbe/HttpsEndpointProbe for the per-target equivalents) -
            // recorded here too, per address, so each of the three public-IP rows shown in
            // DiagnosticsForm has its own real history to chart when double-clicked.
            foreach (PingEndpointResult ep in internetTask.Result.Endpoints)
            {
                if (ep.Reachable && ep.LatencyMs is { } latency)
                {
                    _history.Record($"internet:{ep.Address}", internetTask.Result.TimestampUtc, latency);
                }
            }

            // Only queried when the active adapter is actually wireless - a WLAN query on a wired
            // connection would just report "no connection" every cycle for no benefit, and this
            // keeps LatestWifiResult correctly null (not stale) the moment the adapter changes.
            LatestWifiResult = networkTask.Result.InterfaceType == NetworkInterfaceType.Wireless80211
                ? await RunTypedAsync<IProbeResult>(_wifiProbe, cancellationToken).ConfigureAwait(false)
                : null;

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
            Volatile.Write(ref _cycleInFlight, 0);
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

        if (_speedTestLoopTask is not null)
        {
            try
            {
                await _speedTestLoopTask.ConfigureAwait(false);
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
        _speedTestKickCts.Dispose();
        _autoSpeedTestClient.Dispose();

        // Last: by this point every one of this coordinator's own loops has already been
        // awaited to completion above, so no further Record() call can race the final flush.
        await _history.DisposeAsync().ConfigureAwait(false);
    }
}
