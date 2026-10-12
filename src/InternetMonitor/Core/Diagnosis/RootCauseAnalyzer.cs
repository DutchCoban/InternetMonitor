using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.Diagnosis;

public sealed record RootCauseResult(
    RootCause Cause,
    Severity Severity,
    IReadOnlyList<StepId> AffectedSteps)
{
    public bool IsProblem => Severity is Severity.Error;
}

/// <summary>How bad the overall situation is, independent of which step reported it.</summary>
public enum Severity
{
    Healthy,
    Degraded,
    Error,
}

/// <summary>
/// A pure function from a snapshot to the single most likely cause.
///
/// It walks the steps in dependency order and stops at the first real failure, rather than
/// reporting every failed step as its own problem - an unplugged cable makes six checks fail, and
/// saying so six times is noise, not diagnosis.
///
/// The one place it does more than "first failure wins" is the comparison between a failed probe
/// and a working one further along. ICMP being blocked is extremely common on corporate and hotel
/// networks, so pings failing while HTTPS succeeds does not mean the internet is down - it means
/// the probe is being filtered. v1 made exactly this comparison for step 5 and it is the single
/// most valuable piece of reasoning it did; v2 generalises it to the gateway as well, since plenty
/// of routers are configured not to answer pings either.
///
/// No network I/O and no state: given a snapshot, the answer is always the same, which is what
/// makes it straightforward to test against synthetic snapshots.
/// </summary>
public static class RootCauseAnalyzer
{
    public static RootCauseResult Analyze(StepSnapshot snapshot)
    {
        if (Failed(snapshot, StepId.Adapter))
        {
            return new RootCauseResult(RootCause.Adapter, Severity.Error, [StepId.Adapter]);
        }

        if (Failed(snapshot, StepId.Link))
        {
            return new RootCauseResult(RootCause.Link, Severity.Error, [StepId.Link]);
        }

        if (Failed(snapshot, StepId.IpAddress))
        {
            return new RootCauseResult(RootCause.IpConfiguration, Severity.Error, [StepId.IpAddress]);
        }

        // "Something beyond the local network answered" is the test for whether a failed probe is
        // a real outage or a filtered one. Checked once and reused for both the gateway and the
        // public-internet comparison below.
        bool trafficFlows = Succeeded(snapshot, StepId.Target) || Succeeded(snapshot, StepId.Dns);

        if (Failed(snapshot, StepId.Gateway))
        {
            return trafficFlows
                ? new RootCauseResult(RootCause.FirewallSuspected, Severity.Degraded, [StepId.Gateway])
                : new RootCauseResult(RootCause.Gateway, Severity.Error, [StepId.Gateway]);
        }

        if (Failed(snapshot, StepId.PublicIp))
        {
            return trafficFlows
                ? new RootCauseResult(RootCause.FirewallSuspected, Severity.Degraded, [StepId.PublicIp])
                : new RootCauseResult(RootCause.Internet, Severity.Error, [StepId.PublicIp, StepId.Target]);
        }

        if (Failed(snapshot, StepId.Dns))
        {
            return new RootCauseResult(RootCause.Dns, Severity.Error, [StepId.Dns]);
        }

        if (Failed(snapshot, StepId.Target))
        {
            return new RootCauseResult(RootCause.Target, Severity.Error, [StepId.Target]);
        }

        // Nothing failed. A warning still means the user is experiencing something - high latency,
        // a 5xx from a monitored endpoint - so it is reported rather than rounded up to healthy.
        List<StepId> warnings =
        [
            .. snapshot.All
                .Where(r => r.State == StepState.Warning)
                .OrderBy(r => (int)r.Step)
                .Select(r => r.Step),
        ];

        return warnings.Count > 0
            ? new RootCauseResult(RootCause.Healthy, Severity.Degraded, warnings)
            : new RootCauseResult(RootCause.Healthy, Severity.Healthy, []);
    }

    private static bool Failed(StepSnapshot snapshot, StepId step) => snapshot.StateOf(step) == StepState.Failed;

    private static bool Succeeded(StepSnapshot snapshot, StepId step) =>
        snapshot.StateOf(step) is StepState.Ok or StepState.Warning;
}
