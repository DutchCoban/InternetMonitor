using System.Diagnostics;
using System.Net;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 6: can a name be turned into an address?
///
/// Works down a fixed chain, the way a person troubleshooting by hand would, and stops at the
/// first link that resolves the name - all of it sent from the adapter steps 1-4 described:
///
///  1. The DNS servers that adapter actually has, obtained via DHCP or set by hand. Not the system
///     resolver: with several adapters up, Windows may answer through a different adapter's
///     server, and then this step would describe another path than the steps before it.
///  2. Public resolvers, one at a time in a fixed order (9.9.9.9, then 1.1.1.1, then 8.8.8.8),
///     skipping any the adapter already uses - asking the same server again would only test the
///     same thing twice, and its answer would be read as "a different server works". The
///     first one that answers proves the fault is the adapter's DNS server, not DNS as such. Each
///     one that stays silent is also tried on port 443 at the same moment: reachable there but
///     silent on DNS means something is blocking DNS traffic (port 53) rather than the server
///     being gone.
///  3. Last resort: up to two DNS servers the user entered in Settings, if any.
///
/// A healthy adapter costs exactly one lookup; the rest of the chain only runs on failure.
/// </summary>
public sealed class DnsCheck : IStepCheck
{
    /// <summary>Every public resolver in the chain also serves HTTPS, so 443 is a fair "is the server there at all" probe.</summary>
    private const int ReachabilityProbePort = 443;

    public StepId Id => StepId.Dns;

    public bool IsLocal => false;

    private enum AttemptOutcome
    {
        Resolved,
        AnsweredWithError,
        NoAnswerButReachable,
        NoAnswer,
    }

    private sealed record Attempt(IPAddress Server, AttemptOutcome Outcome);

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

        // The adapter's own DNS failed: walk the public resolvers in order, stopping at the first
        // that resolves the name.
        IReadOnlyList<IPAddress> publicResolvers = [.. context.Targets.PublicResolvers.Where(r => !servers.Contains(r))];
        List<Attempt> publicAttempts = await WalkAsync(publicResolvers, hostname, context, probeReachability: true, cancellationToken)
            .ConfigureAwait(false);
        Attempt? publicWinner = publicAttempts.FirstOrDefault(a => a.Outcome == AttemptOutcome.Resolved);
        bool portBlocked = publicWinner is null && publicAttempts.Any(a => a.Outcome == AttemptOutcome.NoAnswerButReachable);

        // Only when every public resolver failed: the user's own last-resort servers, if any.
        IReadOnlyList<IPAddress> custom = [.. context.Targets.CustomDnsServers.Where(c => !servers.Contains(c) && !publicResolvers.Contains(c))];
        List<Attempt> customAttempts = publicWinner is null && custom.Count > 0
            ? await WalkAsync(custom, hostname, context, probeReachability: false, cancellationToken).ConfigureAwait(false)
            : [];
        Attempt? customWinner = customAttempts.FirstOrDefault(a => a.Outcome == AttemptOutcome.Resolved);
        bool? customWorked = customAttempts.Count == 0 ? null : customWinner is not null;

        measurements.AddRange(publicAttempts.Select(a => new Measurement(a.Server.ToString(), Describe(a, loc))));
        measurements.AddRange(customAttempts.Select(a => new Measurement(
            $"{a.Server} ({loc.Get("measure.customDns")})", Describe(a, loc))));

        string serverList = servers.Count > 0 ? string.Join(", ", servers) : "-";
        string publicList = publicResolvers.Count > 0 ? string.Join(", ", publicResolvers) : "-";

        // Nothing answered plain DNS. Before calling that a failure, ask Windows itself: a setup
        // that only resolves through encrypted DNS (DNS over HTTPS) blocks port 53 yet browses
        // fine, and that is a warning about what this step can see, not an outage.
        if (publicWinner is null && customWinner is null)
        {
            DnsOutcome system = await DnsResolve.ResolveAsync(hostname, context.Targets.DnsTimeout, cancellationToken).ConfigureAwait(false);
            if (system.Success)
            {
                measurements.Add(new Measurement(loc.Get("measure.dnsSystemResolver"), string.Join(", ", system.Addresses.Take(3))));
                object[] systemArgs = [serverList, publicList, hostname];
                return new StepResult(
                    Id,
                    StepState.Warning,
                    loc.Get("step.dns.systemOnly"),
                    loc.Format("step.dns.systemOnly.detail", systemArgs),
                    system.ElapsedMs,
                    watch.Elapsed,
                    DateTimeOffset.UtcNow,
                    measurements,
                    new DnsFacts(hostname, system.Addresses, system.ElapsedMs, servers, false, portBlocked, customWorked, SystemResolverWorked: true))
                {
                    HeadlineKey = "step.dns.systemOnly",
                    DetailKey = "step.dns.systemOnly.detail",
                    DetailArgs = systemArgs,
                };
            }
        }

        var facts = new DnsFacts(hostname, [], result.ElapsedMs, servers, publicWinner is not null, portBlocked, customWorked);

        (string headlineKey, object[] headlineArgs, string detailKey, object[] detailArgs) =
            publicWinner is not null && servers.Count == 0
                ? ("step.dns.noServers", Array.Empty<object>(), "step.dns.noServers.detail", new object[] { publicWinner.Server })
            : publicWinner is not null
                ? ("step.dns.ownResolverFailed", Array.Empty<object>(), "step.dns.ownResolverFailed.detail", new object[] { serverList, hostname, publicWinner.Server })
            : customWinner is not null
                ? ("step.dns.customOnly", Array.Empty<object>(), "step.dns.customOnly.detail", new object[] { serverList, publicList, customWinner.Server })
            : portBlocked
                ? ("step.dns.portBlocked", Array.Empty<object>(), "step.dns.portBlocked.detail", new object[] { serverList, publicList })
            : customAttempts.Count > 0
                ? ("step.dns.failed", new object[] { hostname }, "step.dns.failed.detailCustom", new object[] { serverList, publicList, string.Join(", ", custom) })
                : ("step.dns.failed", new object[] { hostname }, "step.dns.failed.detail", new object[] { serverList, publicList });

        return new StepResult(
            Id,
            StepState.Failed,
            loc.Format(headlineKey, headlineArgs),
            loc.Format(detailKey, detailArgs),
            null,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = headlineKey,
            HeadlineArgs = headlineArgs,
            DetailKey = detailKey,
            DetailArgs = detailArgs,
        };
    }

    /// <summary>Asks each server in turn, stopping at the first that resolves the name.</summary>
    private static async Task<List<Attempt>> WalkAsync(
        IReadOnlyList<IPAddress> servers,
        string hostname,
        StepContext context,
        bool probeReachability,
        CancellationToken cancellationToken)
    {
        var attempts = new List<Attempt>();
        foreach (IPAddress server in servers)
        {
            Attempt attempt = await TryServerAsync(server, hostname, context, probeReachability, cancellationToken).ConfigureAwait(false);
            attempts.Add(attempt);
            if (attempt.Outcome == AttemptOutcome.Resolved)
            {
                break;
            }
        }

        return attempts;
    }

    /// <summary>
    /// One DNS query, with - when asked - a TCP probe on <see cref="ReachabilityProbePort"/>
    /// started at the same moment, so finding out "silent on DNS, but is it there at all?" costs
    /// no extra time on top of the query's own timeout.
    /// </summary>
    private static async Task<Attempt> TryServerAsync(
        IPAddress server,
        string hostname,
        StepContext context,
        bool probeReachability,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = context.Targets.PublicResolverTimeout;
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<TcpOutcome>? probe = probeReachability
            ? TcpConnect.TryConnectAsync(server, ReachabilityProbePort, timeout, probeCts.Token, context.LocalAddress)
            : null;

        DnsQueryResult result = await DnsUdpClient
            .QueryAsync(server, hostname, DnsRecordType.A, timeout, cancellationToken, context.LocalAddress)
            .ConfigureAwait(false);

        AttemptOutcome outcome;
        if (result.Success && result.Addresses.Count > 0)
        {
            outcome = AttemptOutcome.Resolved;
        }
        else if (result.Answered)
        {
            outcome = AttemptOutcome.AnsweredWithError;
        }
        else
        {
            outcome = probe is not null && (await probe.ConfigureAwait(false)).Answered
                ? AttemptOutcome.NoAnswerButReachable
                : AttemptOutcome.NoAnswer;
        }

        if (probe is not null && !probe.IsCompleted)
        {
            probeCts.Cancel();
            try
            {
                await probe.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The probe was only needed if the query went unanswered.
            }
        }

        return new Attempt(server, outcome);
    }

    private static string Describe(Attempt attempt, LocalizationManager loc) => attempt.Outcome switch
    {
        AttemptOutcome.Resolved => loc.Get("measure.dnsTry.resolved"),
        AttemptOutcome.AnsweredWithError => loc.Get("measure.dnsTry.error"),
        AttemptOutcome.NoAnswerButReachable => loc.Get("measure.dnsTry.blocked"),
        _ => loc.Get("measure.dnsTry.noAnswer"),
    };
}
