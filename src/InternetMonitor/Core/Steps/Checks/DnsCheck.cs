using System.Diagnostics;
using System.Net;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 6: can a name be turned into an address?
///
/// Asks the DNS servers the adapter from steps 1-4 actually has - obtained via DHCP or set by
/// hand - directly, from that adapter's own address. Not the system resolver: with several
/// adapters up, Windows may answer from a different adapter's DNS server, and then this step would
/// be describing another path than the steps before it.
///
/// On failure it asks three public resolvers the same question, over the same adapter. If they
/// answer, the network's own DNS server is the problem; if they don't either, it is DNS as a
/// whole. The cross-check only runs on failure, so the healthy path costs exactly one lookup.
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
        IReadOnlyList<IPAddress> servers = context.DnsServers;

        var measurements = new List<Measurement>
        {
            new(loc.Get("measure.hostname"), hostname),
            new(loc.Get("measure.dnsServers"), servers.Count > 0 ? string.Join(", ", servers) : "-"),
        };

        (IPAddress? answeredBy, DnsQueryResult result) = await DnsUdpClient
            .QueryFirstAnswerAsync(servers, hostname, DnsRecordType.A, context.Targets.DnsTimeout, cancellationToken, context.LocalAddress)
            .ConfigureAwait(false);

        if (answeredBy is not null)
        {
            measurements.Add(new Measurement(loc.Get("measure.dnsAnsweredBy"), answeredBy.ToString()));
            measurements.Add(new Measurement(loc.Get("measure.resolved"), string.Join(", ", result.Addresses.Take(3))));
            measurements.Add(new Measurement(loc.Get("measure.latency"), $"{result.ElapsedMs:F0} ms"));

            var okFacts = new DnsFacts(hostname, result.Addresses, result.ElapsedMs, servers, null);
            StepState state = LatencyClassifier.Classify(result.ElapsedMs, context.Targets);
            string elapsedText = result.ElapsedMs.ToString("F0");

            return new StepResult(
                Id,
                state,
                loc.Format("step.dns.ok", hostname, elapsedText),
                state == StepState.Warning ? loc.Get("step.dns.slow") : null,
                result.ElapsedMs,
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

        (IPAddress? publicAnsweredBy, _) = await DnsUdpClient
            .QueryFirstAnswerAsync(context.Targets.PublicResolvers, hostname, DnsRecordType.A, context.Targets.PublicResolverTimeout, cancellationToken, context.LocalAddress)
            .ConfigureAwait(false);
        bool publicResolversWorked = publicAnsweredBy is not null;

        measurements.Add(new Measurement(
            loc.Get("measure.publicResolvers"),
            publicResolversWorked ? $"{loc.Get("measure.publicResolvers.ok")} ({publicAnsweredBy})" : loc.Get("measure.publicResolvers.failed")));

        var facts = new DnsFacts(hostname, [], result.ElapsedMs, servers, publicResolversWorked);

        (string headlineKey, object[] headlineArgs, string detailKey) =
            servers.Count == 0 ? ("step.dns.noServers", Array.Empty<object>(), "step.dns.noServers.detail")
            : publicResolversWorked ? ("step.dns.ownResolverFailed", Array.Empty<object>(), "step.dns.ownResolverFailed.detail")
            : ("step.dns.failed", new object[] { hostname }, "step.dns.failed.detail");

        return new StepResult(
            Id,
            StepState.Failed,
            loc.Format(headlineKey, headlineArgs),
            loc.Get(detailKey),
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
}
