using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 4: can I reach my own router?
///
/// ICMP and a TCP connect to a few common router ports are raced against each other, all started
/// at once, first answer wins. This is carried straight over from v1 (Network/GatewayReachability)
/// and it matters: plenty of routers do not answer pings, and plenty of networks filter ICMP, so a
/// ping-only check reports a perfectly healthy gateway as unreachable. Racing rather than
/// sequencing means the common case (ICMP works) stays as fast as a single ping instead of waiting
/// through a chain of timeouts.
///
/// Deliberately no retry. A settings change - even just switching DNS server - can genuinely make
/// the gateway unreachable at the OS level for a second or two, and that is real, momentary state
/// worth showing rather than a measurement error to smooth away.
/// </summary>
public sealed class GatewayCheck : IStepCheck
{
    public StepId Id => StepId.Gateway;

    public bool IsLocal => false;

    public async Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        LocalizationManager loc = LocalizationManager.Instance;
        IPAddress? gateway = context.Gateway;

        if (gateway is null)
        {
            return new StepResult(
                Id,
                StepState.Failed,
                loc.Get("step.gateway.none"),
                loc.Get("step.gateway.none.detail"),
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                [],
                new GatewayFacts(null, false, null, null, null))
            {
                HeadlineKey = "step.gateway.none",
                DetailKey = "step.gateway.none.detail",
            };
        }

        TimeSpan timeout = context.Targets.GatewayTimeout;
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task<PingOutcome> icmp = IcmpPing.SendAsync(gateway, timeout, race.Token, context.LocalAddress);
        Task<(int Port, TcpOutcome Outcome)?> tcp = TcpConnect.FirstAnsweringPortAsync(
            gateway, context.Targets.GatewayProbePorts, timeout, race.Token, context.LocalAddress);

        PingOutcome ping;
        (int Port, TcpOutcome Outcome)? tcpAnswer;
        // Both are awaited, each within the same timeout, so ICMP's outcome is known even when a
        // TCP port answered: ICMP is preferred as the answer (see "via" below), and its status is
        // shown either way - "the router ignores ping" is worth seeing.
        try
        {
            await Task.WhenAll(icmp, tcp).ConfigureAwait(false);
            ping = await icmp.ConfigureAwait(false);
            tcpAnswer = await tcp.ConfigureAwait(false);
        }
        finally
        {
            race.Cancel();
        }

        bool reachable = ping.Success || tcpAnswer is not null;
        string? via = ping.Success
            ? "ICMP"
            : tcpAnswer is { } answer ? $"TCP {answer.Port}" : null;
        double? latency = ping.Success ? ping.RoundtripMs : tcpAnswer?.Outcome.ElapsedMs;

        var facts = new GatewayFacts(gateway, reachable, via, latency, ping.Status);

        var measurements = new List<Measurement>
        {
            new(loc.Get("measure.gateway"), gateway.ToString()),
            new(loc.Get("measure.icmp"), ping.Status.ToString()),
        };

        if (via is not null)
        {
            measurements.Add(new Measurement(loc.Get("measure.answeredVia"), via));
        }

        if (latency is not null)
        {
            measurements.Add(new Measurement(loc.Get("measure.latency"), $"{latency:F0} ms"));
        }

        if (!reachable)
        {
            return new StepResult(
                Id,
                StepState.Failed,
                loc.Format("step.gateway.unreachable", gateway),
                loc.Get("step.gateway.unreachable.detail"),
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                measurements,
                facts)
            {
                HeadlineKey = "step.gateway.unreachable",
                HeadlineArgs = [gateway],
                DetailKey = "step.gateway.unreachable.detail",
            };
        }

        StepState state = LatencyClassifier.Classify(latency, context.Targets);
        string latencyText = latency?.ToString("F0") ?? "?";
        (string? detailKey, object[] detailArgs) = state == StepState.Warning ? LatencyClassifier.WarningDetail(context) : (null, []);
        return new StepResult(
            Id,
            state,
            loc.Format("step.gateway.ok", gateway, latencyText),
            detailKey is null ? null : loc.Format(detailKey, detailArgs),
            latency,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = "step.gateway.ok",
            HeadlineArgs = [gateway, latencyText],
            DetailKey = detailKey,
            DetailArgs = detailArgs,
        };
    }
}
