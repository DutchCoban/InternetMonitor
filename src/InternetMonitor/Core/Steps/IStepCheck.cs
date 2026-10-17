using System.Net;
using System.Net.NetworkInformation;
using InternetMonitor.Configuration;

namespace InternetMonitor.Core.Steps;

/// <summary>
/// Per-cycle shared state. The local steps fill it in as they run and the network steps read it,
/// so the adapter is resolved once per cycle instead of four times - and, more importantly, so
/// every step is describing the same adapter. In v1 each probe independently asked which interface
/// was active, which on a machine with a VPN or a Hyper-V switch could have two probes reasoning
/// about two different pieces of hardware in the same cycle.
/// </summary>
public sealed class StepContext(CheckTargets targets)
{
    public CheckTargets Targets { get; } = targets;

    public NetworkInterface? Adapter { get; set; }

    /// <summary>Every adapter Windows listed at the start of this cycle - enumerated once, by step 1, for whoever needs the list.</summary>
    public IReadOnlyList<NetworkInterface>? AllAdapters { get; set; }

    public IPInterfaceProperties? IpProperties { get; set; }

    public IPAddress? LocalAddress { get; set; }

    public IPAddress? Gateway { get; set; }

    public IReadOnlyList<IPAddress> DnsServers { get; set; } = [];

    /// <summary>How busy this computer's connection was since the previous cycle; null when unknown.</summary>
    public ThroughputSample? Throughput { get; set; }

    /// <summary>Whether the connection is metered or mobile, as step 2 found it; null when unknown.</summary>
    public Net.ConnectionCostInfo? Cost { get; set; }

    /// <summary>An active VPN, as step 3 found it; null when there is none.</summary>
    public Net.VpnInfo? Vpn { get; set; }

    /// <summary>The adapter's global IPv6 address, as step 3 found it; null on an IPv4-only network.</summary>
    public IPAddress? GlobalIpv6 { get; set; }
}

/// <summary>
/// One step in the pipeline.
///
/// A check reports only what it observed and never decides that "the internet is down" - that
/// interpretation belongs to the root-cause analyser, which can see all seven results at once.
/// This separation is inherited from v1 and is what makes it possible to say "ICMP is filtered"
/// rather than "the internet is down" when pings fail but HTTPS works.
/// </summary>
public interface IStepCheck
{
    StepId Id { get; }

    /// <summary>
    /// True for checks that only read local OS state and do no network I/O. They take microseconds
    /// and run first, sequentially, so the first three rows of the status screen are already
    /// filled in by the time the network checks have even opened a socket.
    /// </summary>
    bool IsLocal { get; }

    Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken);
}
