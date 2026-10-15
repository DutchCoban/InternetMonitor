using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Steps;

/// <summary>
/// Runs the seven steps and reports what they are doing while they are doing it.
///
/// Execution shape, and why:
///
///  - Steps 1 to 3 touch only local OS state and together take under a millisecond. They run
///    sequentially, in order, and their results are published as they land - so the first three
///    rows of the status screen are filled in before the network has been touched at all. That
///    single fact is most of why v2 feels faster than v1, which revealed nothing until the whole
///    cycle had finished.
///  - Steps 4 to 7 run concurrently. A cycle therefore costs the slowest network step rather than
///    the sum of all four, while the logical order the user sees is untouched.
///
/// Publication order is where the subtlety is. Every step announces Running the moment it starts,
/// which is what the focus arrow and the spinners are drawn from. Successes publish immediately.
/// A *failure*, though, is held back until every lower-numbered step has resolved - because until
/// then it is not yet known whether this is a real fault or just the downstream consequence of
/// one. When the earlier steps land, a held failure is published either as Failed (nothing
/// upstream broke, so this is the fault) or as Skipped (something upstream broke, so this was
/// never going to work). Without that gate the screen would show four red rows and then quietly
/// turn three of them grey, which looks like a bug and reads like panic.
///
/// A step that *succeeds* while an earlier one failed keeps its success. That combination is
/// information, not a contradiction: pings failing while HTTPS works is how a filtered-ICMP
/// network is recognised, and discarding it would throw away the best diagnosis this tool makes.
/// </summary>
public sealed class StepPipeline : IDisposable
{
    /// <summary>
    /// Backstop for the whole cycle. Every check already has its own timeout, so reaching this
    /// means something is wedged below them - a hung DNS client, a socket that never completes.
    /// Better to abandon the cycle and run a clean one than to stall the monitor indefinitely.
    /// </summary>
    private static readonly TimeSpan CycleBudget = TimeSpan.FromSeconds(20);

    private static readonly StepId[] AllSteps =
        [StepId.Adapter, StepId.Link, StepId.IpAddress, StepId.Gateway, StepId.PublicIp, StepId.Dns, StepId.Target];

    private readonly IReadOnlyList<IStepCheck> _localChecks;
    private readonly IReadOnlyList<IStepCheck> _networkChecks;
    private readonly Func<CheckTargets> _targetsProvider;

    private readonly object _gate = new();
    private readonly Dictionary<StepId, StepResult> _current = [];
    private readonly Dictionary<StepId, StepResult> _heldFailures = [];

    private CancellationTokenSource? _cycleCts;
    private int _cycleInFlight;
    private bool _disposed;

    public StepPipeline(Func<CheckTargets> targetsProvider)
        : this(
            targetsProvider,
            [new AdapterCheck(), new LinkCheck(), new IpAddressCheck()],
            [new GatewayCheck(), new PublicIpCheck(), new DnsCheck(), new TargetCheck()])
    {
    }

    /// <summary>Injectable overload, used by the tests and by the fault simulator.</summary>
    public StepPipeline(
        Func<CheckTargets> targetsProvider,
        IReadOnlyList<IStepCheck> localChecks,
        IReadOnlyList<IStepCheck> networkChecks)
    {
        _targetsProvider = targetsProvider;
        _localChecks = [.. localChecks.OrderBy(c => (int)c.Id)];
        _networkChecks = [.. networkChecks.OrderBy(c => (int)c.Id)];
    }

    /// <summary>
    /// Raised on every state transition of every step, including Running. Raised on a background
    /// thread - subscribers that touch the UI must marshal.
    /// </summary>
    public event Action<StepResult>? StepChanged;

    /// <summary>Raised once per cycle, after every step has resolved.</summary>
    public event Action<StepSnapshot>? CycleCompleted;

    public StepSnapshot? LatestSnapshot { get; private set; }

    /// <summary>Reads the adapter's traffic once per cycle; see <see cref="ThroughputMeter"/>.</summary>
    public ThroughputMeter Throughput { get; } = new();

    /// <summary>
    /// Runs one cycle. If a cycle is already running, this returns immediately rather than
    /// queueing: the caller is a timer or a user clicking "check now", and neither wants two
    /// overlapping cycles fighting over the same context.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || Interlocked.CompareExchange(ref _cycleInFlight, 1, 0) != 0)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(CycleBudget);
        _cycleCts = cts;

        try
        {
            await RunCycleCoreAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Either shutdown or the cycle budget. Neither is worth reporting as a fault: the next
            // cycle starts clean, and a genuinely broken network already surfaces through the
            // individual steps' own timeouts.
        }
        finally
        {
            _cycleCts = null;
            cts.Dispose();
            Volatile.Write(ref _cycleInFlight, 0);
        }
    }

    private async Task RunCycleCoreAsync(CancellationToken cancellationToken)
    {
        var context = new StepContext(_targetsProvider());

        lock (_gate)
        {
            // _current is still cleared every cycle - DrainHeldFailures relies on it holding only
            // this cycle's own results when it checks whether a step's upstream has resolved yet.
            // What used to follow this, an explicit Pending publish for all seven steps, is gone:
            // both StepList and StepTable already start their own display at Pending before any
            // event ever arrives, so re-announcing it here on every single cycle - including the
            // thousands after the first, on a healthy connection that never changes - bought
            // nothing except an extra StepChanged event and a repaint for every step, every few
            // seconds, forever. That is what made the dashboard feel like it kept wiping itself
            // clean on each refresh: each step now goes straight from last cycle's real result to
            // Running to this cycle's real result, with nothing resetting in between.
            _current.Clear();
            _heldFailures.Clear();
        }

        StepId? localFailure = await RunLocalChecksAsync(context, cancellationToken).ConfigureAwait(false);

        if (localFailure is { } blockedBy)
        {
            // Nothing downstream of a dead adapter, a dead link or a missing address can tell the
            // user anything they do not already know, and running them would only add delay and
            // noise. This is the one place the pipeline short-circuits outright rather than
            // running speculatively.
            SkipRemaining(blockedBy);
        }
        else
        {
            // Read just before the network checks: the rate then covers the interval in which the
            // user's own traffic was flowing, not the checks' own few packets.
            context.Throughput = context.Adapter is { } adapter ? Throughput.Measure(adapter) : null;
            await RunNetworkChecksAsync(context, cancellationToken).ConfigureAwait(false);
        }

        DrainHeldFailures(force: true);
        CompleteCycle(context.Throughput);
    }

    /// <summary>
    /// Runs steps 1 to 3 one after the other, publishing each as it finishes. They only read local
    /// state, and each depends on the one before, so the first failure stops here; the returned step
    /// lets the caller mark everything after it as skipped.
    /// </summary>
    private async Task<StepId?> RunLocalChecksAsync(StepContext context, CancellationToken cancellationToken)
    {
        foreach (IStepCheck check in _localChecks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publish(StepResult.Running(check.Id));

            StepResult result = await RunGuardedAsync(check, context, cancellationToken).ConfigureAwait(false);
            Publish(result);

            if (result.State.IsFailure())
            {
                return check.Id;
            }
        }

        return null;
    }

    private async Task RunNetworkChecksAsync(StepContext context, CancellationToken cancellationToken)
    {
        await Task.WhenAll(_networkChecks.Select(async check =>
        {
            Publish(StepResult.Running(check.Id));
            StepResult result = await RunGuardedAsync(check, context, cancellationToken).ConfigureAwait(false);

            if (result.State.IsFailure())
            {
                HoldFailure(result);
            }
            else
            {
                Publish(result);
            }

            // A step resolving can unblock a failure that was waiting on it, whichever order they
            // happened to finish in.
            DrainHeldFailures(force: false);
        })).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs a check and turns an unexpected exception into a failed result rather than letting it
    /// take down the cycle. A check throwing is a bug, but one broken check should cost one broken
    /// row, not the whole diagnosis.
    /// </summary>
    private static async Task<StepResult> RunGuardedAsync(IStepCheck check, StepContext context, CancellationToken cancellationToken)
    {
        if (FaultSimulation.ShouldFail(check.Id))
        {
            // Short-circuits before the real check runs, so a simulated adapter failure genuinely
            // costs nothing and the downstream skip logic sees exactly what it would see for a
            // real one.
            return new StepResult(
                check.Id,
                StepState.Failed,
                LocalizationManager.Instance.Get("step.simulated"),
                LocalizationManager.Instance.Get("step.simulated.detail"),
                null,
                TimeSpan.Zero,
                DateTimeOffset.UtcNow,
                [],
                null)
            {
                HeadlineKey = "step.simulated",
                DetailKey = "step.simulated.detail",
            };
        }

        try
        {
            await FaultSimulation.ApplyDelayAsync(check, cancellationToken).ConfigureAwait(false);
            return await check.RunAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ex.Message has no HeadlineKey/DetailKey equivalent - it is whatever the framework or
            // a library produced, not one of this app's own translated strings - so only the
            // headline (a fixed, translated sentence) gets one; the detail falls back to the live
            // (untranslated either way) exception text wherever it ends up frozen.
            return new StepResult(
                check.Id,
                StepState.Failed,
                LocalizationManager.Instance.Get("step.error.unexpected"),
                ex.Message,
                null,
                TimeSpan.Zero,
                DateTimeOffset.UtcNow,
                [],
                null)
            {
                HeadlineKey = "step.error.unexpected",
            };
        }
    }

    private void HoldFailure(StepResult result)
    {
        lock (_gate)
        {
            _heldFailures[result.Step] = result;
        }
    }

    /// <summary>
    /// Publishes any held failure whose upstream steps have all resolved - as Failed when nothing
    /// upstream broke, as Skipped when something did. With <paramref name="force"/> the cycle is
    /// over, so anything still held is resolved against whatever is known.
    /// </summary>
    private void DrainHeldFailures(bool force)
    {
        var toPublish = new List<StepResult>();

        lock (_gate)
        {
            foreach (StepId step in _heldFailures.Keys.OrderBy(s => (int)s).ToList())
            {
                bool upstreamResolved = AllSteps
                    .Where(s => (int)s < (int)step)
                    .All(s => _current.TryGetValue(s, out StepResult? r) && r.State.IsResolved());

                if (!upstreamResolved && !force)
                {
                    continue;
                }

                StepResult held = _heldFailures[step];
                _heldFailures.Remove(step);

                StepId? blocker = AllSteps
                    .Where(s => (int)s < (int)step)
                    .Where(s => _current.TryGetValue(s, out StepResult? r) && r.State.IsFailure())
                    .Cast<StepId?>()
                    .FirstOrDefault();

                toPublish.Add(blocker is { } b ? AsSkipped(held, b) : held);
            }
        }

        foreach (StepResult result in toPublish)
        {
            Publish(result);
        }
    }

    private void SkipRemaining(StepId blockedBy)
    {
        foreach (StepId step in AllSteps.Where(s => (int)s > (int)blockedBy))
        {
            Publish(AsSkipped(StepResult.Pending(step), blockedBy));
        }
    }

    private static StepResult AsSkipped(StepResult result, StepId blockedBy)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        string blockerNameKey = $"step.{blockedBy.Key()}.name";
        bool resultFailed = result.State.IsFailure();

        // The skip detail reuses the blocking result's own headline verbatim (see below), so its
        // English reconstruction reuses that result's HeadlineKey/Args too, rather than needing a
        // key of its own - EnglishDetail() resolving "step.adapter.disabled" is exactly the same
        // operation as EnglishHeadline() would have done on the original result.
        return result.AsSkipped(
            loc.Format("step.skipped", loc.Get(blockerNameKey)),
            resultFailed ? result.Headline : null,
            headlineKey: "step.skipped",
            headlineArgs: [LocalizationManager.GetEnglish(blockerNameKey)],
            detailKey: resultFailed ? result.HeadlineKey : null,
            detailArgs: resultFailed ? result.HeadlineArgs : null);
    }

    private void Publish(StepResult result)
    {
        lock (_gate)
        {
            if (_current.TryGetValue(result.Step, out StepResult? existing) && existing.State == result.State
                && existing.Headline == result.Headline)
            {
                return;
            }

            _current[result.Step] = result;
        }

        StepChanged?.Invoke(result);
    }

    private void CompleteCycle(ThroughputSample? throughput)
    {
        StepSnapshot snapshot;
        lock (_gate)
        {
            snapshot = new StepSnapshot(_current.Values.ToList(), DateTimeOffset.UtcNow) { Throughput = throughput };
        }

        LatestSnapshot = snapshot;
        CycleCompleted?.Invoke(snapshot);
    }

    public void Dispose()
    {
        _disposed = true;
        _cycleCts?.Cancel();
    }
}
