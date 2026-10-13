using System.Diagnostics;
using System.Net;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 6: can a name be turned into an address?
///
/// On success this is just a timed system lookup. On failure it does one extra thing that changes
/// the answer completely: it asks three public resolvers the same question directly over UDP. If
/// they answer where the system resolver could not, the problem is not "DNS is broken" but "the
/// DNS server you are configured to use is broken" - a different fault, with a different fix, that
/// the user can act on.
///
/// The cross-check only runs on failure, so the healthy path costs exactly one lookup.
/// </summary>
public sealed class DnsCheck : IStepCheck
{
    public StepId Id => StepId.Dns;

    public bool IsLocal => false;

    public async Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        LocalizationManager loc = LocalizationManager.Instance;
        string hostname = context.Targets.DnsProbeHostname;

        DnsOutcome outcome = await DnsResolve
            .ResolveAsync(hostname, context.Targets.DnsTimeout, cancellationToken)
            .ConfigureAwait(false);

        var measurements = new List<Measurement>
        {
            new(loc.Get("measure.hostname"), hostname),
        };

        if (context.DnsServers.Count > 0)
        {
            measurements.Add(new Measurement(loc.Get("measure.dnsServers"), string.Join(", ", context.DnsServers)));
        }

        if (outcome.Success)
        {
            measurements.Add(new Measurement(loc.Get("measure.resolved"), string.Join(", ", outcome.Addresses.Take(3))));
            measurements.Add(new Measurement(loc.Get("measure.latency"), $"{outcome.ElapsedMs:F0} ms"));

            var okFacts = new DnsFacts(hostname, outcome.Addresses, outcome.ElapsedMs, context.DnsServers, null);
            StepState state = LatencyClassifier.Classify(outcome.ElapsedMs, context.Targets);
            string elapsedText = outcome.ElapsedMs.ToString("F0");

            return new StepResult(
                Id,
                state,
                loc.Format("step.dns.ok", hostname, elapsedText),
                state == StepState.Warning ? loc.Get("step.dns.slow") : null,
                outcome.ElapsedMs,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                measurements,
                okFacts)
            {
                HeadlineKey = "step.dns.ok",
                HeadlineArgs = [hostname, elapsedText],
                DetailKey = state == StepState.Warning ? "step.dns.slow" : null,
            };
        }

        bool publicResolversWorked = await AnyPublicResolverAnswersAsync(context, hostname, cancellationToken)
            .ConfigureAwait(false);

        measurements.Add(new Measurement(
            loc.Get("measure.publicResolvers"),
            loc.Get(publicResolversWorked ? "measure.publicResolvers.ok" : "measure.publicResolvers.failed")));

        var facts = new DnsFacts(hostname, [], outcome.ElapsedMs, context.DnsServers, publicResolversWorked);

        string headlineKey = publicResolversWorked ? "step.dns.ownResolverFailed" : "step.dns.failed";
        string detailKey = publicResolversWorked ? "step.dns.ownResolverFailed.detail" : "step.dns.failed.detail";
        object[] headlineArgs = publicResolversWorked ? [] : [hostname];

        return new StepResult(
            Id,
            StepState.Failed,
            publicResolversWorked
                ? loc.Get("step.dns.ownResolverFailed")
                : loc.Format("step.dns.failed", hostname),
            publicResolversWorked
                ? loc.Get("step.dns.ownResolverFailed.detail")
                : loc.Get("step.dns.failed.detail"),
            null,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = headlineKey,
            HeadlineArgs = headlineArgs,
            DetailKey = detailKey,
        };
    }

    /// <summary>
    /// Queries the public resolvers in parallel and returns as soon as any of them answers.
    /// Running them at once rather than in sequence keeps the failure path from costing three
    /// timeouts on a connection that is genuinely down, which is the case where this runs most.
    /// </summary>
    private static async Task<bool> AnyPublicResolverAnswersAsync(
        StepContext context,
        string hostname,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> resolvers = context.Targets.PublicResolvers;
        if (resolvers.Count == 0)
        {
            return false;
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        List<Task<DnsQueryResult>> pending = resolvers
            .Select(resolver => DnsUdpClient.QueryAsync(
                resolver, hostname, DnsRecordType.A, context.Targets.PublicResolverTimeout, race.Token))
            .ToList();

        try
        {
            while (pending.Count > 0)
            {
                Task<DnsQueryResult> finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);

                DnsQueryResult result = await finished.ConfigureAwait(false);
                if (result.Success && result.Addresses.Count > 0)
                {
                    return true;
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
        }

        return false;
    }
}
