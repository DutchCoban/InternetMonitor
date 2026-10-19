using System.Net;
using System.Net.Sockets;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.Tools.Traceroute;

/// <summary>
/// Where the traceroute that an opening incident records should go, so that it shows the path
/// the fault is on rather than a path that happens to be known.
/// </summary>
public static class IncidentTracerouteTarget
{
    /// <summary>
    /// For a DNS incident: the adapter's own DNS server, the address the unanswered queries went
    /// to - a trace to the HTTP reachability host says nothing about the way to 1.1.1.1. For the
    /// rest: the reachability host's address from the last cycle when it is known (during a DNS
    /// incident the name itself may not resolve), else the first public ping address. Null when
    /// nothing is known at all.
    /// </summary>
    public static string? For(RootCause cause, StepSnapshot? snapshot, IReadOnlyList<IPAddress> publicTargets)
    {
        if (cause == RootCause.Dns
            && snapshot?.FactsFor<DnsFacts>(StepId.Dns)?.ConfiguredServers.FirstOrDefault(s => s.AddressFamily == AddressFamily.InterNetwork) is { } server)
        {
            return server.ToString();
        }

        return snapshot?.FactsFor<TargetFacts>(StepId.Target)?.Endpoints.FirstOrDefault(e => e.IsReachabilityCheck)?.RemoteAddress?.ToString()
            ?? publicTargets.FirstOrDefault()?.ToString();
    }
}
