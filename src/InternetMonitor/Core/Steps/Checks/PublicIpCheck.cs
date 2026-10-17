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

        // IPv6 is pinged alongside, never instead: the step's verdict stays the IPv4 one, the same
        // family as the steps before it. Only tried when step 3 found a global IPv6 address.
        Task<PingOutcome?> ipv6 = PingIpv6Async(context, cancellationToken);
        IReadOnlyList<PublicIpEndpointResult> endpoints = await PingUntilFirstAnswerAsync(context, cancellationToken).ConfigureAwait(false);
        PingOutcome? ipv6Outcome = await ipv6.ConfigureAwait(false);

        double? best = endpoints.Where(e => e.Reachable).Select(e => e.LatencyMs).Min();
        var facts = new PublicIpFacts(endpoints, best, ipv6Outcome?.Success, ipv6Outcome?.RoundtripMs);

        var measurements = endpoints
            .Select(e => new Measurement(
                e.Address.ToString(),
                e.Reachable ? $"{e.LatencyMs:F0} ms" : e.Status.ToString()))
            .ToList();

        if (ipv6Outcome is { } v6)
        {
            measurements.Add(new Measurement(
                loc.Get("measure.ipv6Ping"),
                v6.Success ? $"{v6.RoundtripMs:F0} ms" : loc.Get("measure.ipv6.noAnswer")));
        }

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
        (string? detailKey, object[] detailArgs) = state == StepState.Warning ? LatencyClassifier.WarningDetail(context) : (null, []);

        return new StepResult(
            Id,
            state,
            loc.Format("step.publicIp.ok", bestText),
            detailKey is null ? null : loc.Format(detailKey, detailArgs),
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

    /// <summary>
    /// The IPv6 targets, all at once; the first answer wins, as for IPv4. Null when the adapter has
    /// no global IPv6 address, so there is nothing to test.
    /// </summary>
    private static async Task<PingOutcome?> PingIpv6Async(StepContext context, CancellationToken cancellationToken)
    {
        if (context.GlobalIpv6 is null || context.Targets.PublicIpv6Targets.Count == 0)
        {
            return null;
        }

        PingOutcome[] outcomes = await Task.WhenAll(context.Targets.PublicIpv6Targets
            .Select(target => IcmpPing.SendAsync(target, context.Targets.PublicIpTimeout, cancellationToken))).ConfigureAwait(false);

        return outcomes.Where(o => o.Success).OrderBy(o => o.RoundtripMs).FirstOrDefault() is { Success: true } best
            ? best
            : outcomes[0];
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
        Func<CancellationToken, Task<(IPAddress Address, PingOutcome Outcome)>> Ping(IPAddress target) =>
            async token => (target, await IcmpPing.SendAsync(target, context.Targets.PublicIpTimeout, token, context.LocalAddress).ConfigureAwait(false));

        RaceOutcome<(IPAddress Address, PingOutcome Outcome)> race = await Race
            .FirstAsync([.. context.Targets.PublicIpTargets.Select(Ping)], r => r.Outcome.Success, cancellationToken)
            .ConfigureAwait(false);

        return [.. race.Finished.Select(r => new PublicIpEndpointResult(r.Address, r.Outcome.Success, r.Outcome.RoundtripMs, r.Outcome.Status))];
    }
}
