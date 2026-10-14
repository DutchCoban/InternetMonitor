using System.Diagnostics;
using System.Net;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 5: can I reach anything on the public internet at all?
///
/// Three well-known resolvers are pinged at once. One of them being silent means nothing - any
/// single anycast address can be having a bad day, or be blocked on its own - so a partial answer
/// is still a pass, with the detail saying which ones answered. Only total silence is treated as
/// a failure.
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
        IReadOnlyList<IPAddress> targets = context.Targets.PublicIpTargets;

        IReadOnlyList<(IPAddress Address, PingOutcome Outcome)> results =
            await IcmpPing.SendAllAsync(targets, context.Targets.PublicIpTimeout, cancellationToken, context.LocalAddress).ConfigureAwait(false);

        var endpoints = results
            .Select(r => new PublicIpEndpointResult(r.Address, r.Outcome.Success, r.Outcome.RoundtripMs, r.Outcome.Status))
            .ToList();

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
        bool partial = facts.ReachableCount < endpoints.Count;
        string bestText = best?.ToString("F0") ?? "?";

        string? detailKey = partial ? "step.publicIp.partial.detail" : state == StepState.Warning ? "step.latency.high" : null;
        object[] detailArgs = partial ? [facts.ReachableCount, endpoints.Count] : [];

        return new StepResult(
            Id,
            state,
            loc.Format("step.publicIp.ok", bestText),
            partial
                ? loc.Format("step.publicIp.partial.detail", facts.ReachableCount, endpoints.Count)
                : state == StepState.Warning ? loc.Get("step.latency.high") : null,
            best,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = "step.publicIp.ok",
            HeadlineArgs = [bestText],
            DetailKey = detailKey,
            DetailArgs = detailArgs,
        };
    }
}
