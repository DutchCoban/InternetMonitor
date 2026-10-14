using System.Diagnostics;
using System.Net;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 5: can I reach anything on the public internet at all?
///
/// All configured addresses are pinged at once, and the first answer settles it: one address
/// replying is proof enough, so the step does not sit out the timeout of another that happens to
/// be silent. Only total silence is a failure - any single anycast address can be having a bad
/// day, or be blocked on its own.
///
/// Note what this step does *not* conclude. ICMP being blocked is extremely common, so "nothing
/// answered" here is evidence, not a verdict: the root-cause analyser compares it against step 7
/// and reports a filtered-ICMP network rather than a dead connection when HTTPS still works. That
/// comparison is the single most valuable piece of reasoning v1 had and it is preserved exactly.
/// </summary>
public sealed class PublicIpCheck : IStepCheck
{
    public StepId Id => StepId.PublicIp;

    public bool IsLocal => false;

    public async Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        LocalizationManager loc = LocalizationManager.Instance;

        IReadOnlyList<PublicIpEndpointResult> endpoints = await PingUntilFirstAnswerAsync(context, cancellationToken).ConfigureAwait(false);

        double? best = endpoints.Where(e => e.Reachable).Select(e => e.LatencyMs).Min();
        var facts = new PublicIpFacts(endpoints, best);

        var measurements = endpoints
            .Select(e => new Measurement(
                e.Address.ToString(),
                e.Reachable ? $"{e.LatencyMs:F0} ms" : e.Status.ToString()))
            .ToList();

        if (facts.AllUnreachable)
        {
            return new StepResult(
                Id,
                StepState.Failed,
                loc.Get("step.publicIp.none"),
                loc.Get("step.publicIp.none.detail"),
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                measurements,
                facts)
            {
                HeadlineKey = "step.publicIp.none",
                DetailKey = "step.publicIp.none.detail",
            };
        }

        StepState state = LatencyClassifier.Classify(best, context.Targets);
        string bestText = best?.ToString("F0") ?? "?";
        string? detailKey = state == StepState.Warning ? "step.latency.high" : null;

        return new StepResult(
            Id,
            state,
            loc.Format("step.publicIp.ok", bestText),
            detailKey is null ? null : loc.Get(detailKey),
            best,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = "step.publicIp.ok",
            HeadlineArgs = [bestText],
            DetailKey = detailKey,
        };
    }

    /// <summary>
    /// Pings every target at once and returns as soon as one answers, cancelling the rest. The
    /// result lists the targets that had finished by then - the answering one, plus any that
    /// already failed. When none answers, every target's failure is in the list.
    /// </summary>
    private static async Task<IReadOnlyList<PublicIpEndpointResult>> PingUntilFirstAnswerAsync(
        StepContext context,
        CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<(IPAddress Address, PingOutcome Outcome)>> pending = context.Targets.PublicIpTargets
            .Select(async target => (target, await IcmpPing
                .SendAsync(target, context.Targets.PublicIpTimeout, race.Token, context.LocalAddress)
                .ConfigureAwait(false)))
            .ToList();

        var finished = new List<PublicIpEndpointResult>();
        try
        {
            while (pending.Count > 0)
            {
                Task<(IPAddress Address, PingOutcome Outcome)> done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);

                (IPAddress address, PingOutcome outcome) = await done.ConfigureAwait(false);
                finished.Add(new PublicIpEndpointResult(address, outcome.Success, outcome.RoundtripMs, outcome.Status));
                if (outcome.Success)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            race.Cancel();

            // The losers end in a cancellation once the race is over; observe them so it never
            // surfaces as an unobserved task exception.
            foreach (Task task in pending)
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            }
        }

        return finished;
    }
}
