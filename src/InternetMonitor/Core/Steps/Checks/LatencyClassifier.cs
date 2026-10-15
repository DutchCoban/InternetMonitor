using InternetMonitor.Configuration;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Turns a latency reading into a step state.
///
/// A connection that works but is slow is a real finding, and one a user will notice before any
/// check fails outright - so slow is a warning rather than silence. The thresholds are user
/// configurable because "slow" means something different on fibre and on a congested 4G link.
/// </summary>
public static class LatencyClassifier
{
    public static StepState Classify(double? latencyMs, CheckTargets targets)
    {
        if (latencyMs is not { } ms)
        {
            return StepState.Ok;
        }

        if (ms >= targets.LatencyErrorMs)
        {
            // Still reachable, so not Failed: the connection works, it is just bad enough that the
            // user is certainly noticing it.
            return StepState.Warning;
        }

        return ms >= targets.LatencyWarningMs ? StepState.Warning : StepState.Ok;
    }

    /// <summary>
    /// The detail line for a latency warning. When this computer was filling the line at the time,
    /// that is said outright - it turns "the internet is slow" into "your own downloads are
    /// queueing in the router", which is a different fix and not the provider's. On a metered
    /// connection - nearly always mobile data or a phone's hotspot - a higher round trip is simply
    /// what that kind of connection does, and saying so saves a pointless hunt for a fault. The
    /// same goes for a VPN carrying all traffic: the round trip is then to the VPN's server and on.
    /// </summary>
    public static (string Key, object[] Args) WarningDetail(StepContext context) => context switch
    {
        { Throughput: { Busy: true } busy } => ("step.latency.busy", [busy.DownMbps.ToString("F0"), busy.UpMbps.ToString("F0")]),
        { Vpn.FullTunnel: true } => ("step.latency.vpn", []),
        { Cost.Metered: true } => ("step.latency.metered", []),
        _ => ("step.latency.high", []),
    };

    /// <summary>Combines two states, keeping the worse one. Ok &lt; Warning &lt; Failed.</summary>
    public static StepState Worse(StepState a, StepState b)
    {
        if (a == StepState.Failed || b == StepState.Failed)
        {
            return StepState.Failed;
        }

        return a == StepState.Warning || b == StepState.Warning ? StepState.Warning : StepState.Ok;
    }
}
