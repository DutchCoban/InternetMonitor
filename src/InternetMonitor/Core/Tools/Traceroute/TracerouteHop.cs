using System.Net;
using System.Net.NetworkInformation;

namespace InternetMonitor.Core.Tools.Traceroute;

/// <summary>One probe to one hop. The status is kept, not just whether it answered.</summary>
public readonly record struct HopProbe(IPStatus Status, double? LatencyMs, IPAddress? Address)
{
    public bool Answered => Address is not null && Status is IPStatus.TtlExpired or IPStatus.Success;

    public bool TimedOut => Status is IPStatus.TimedOut or IPStatus.Unknown;
}

/// <summary>Who an address belongs to, as far as public registries will say.</summary>
public sealed record IpOwner(string? AsNumber, string? AsName, string? NetworkName, string? CountryCode, string? Prefix)
{
    public bool HasAnything => AsNumber is not null || AsName is not null || NetworkName is not null;

    /// <summary>
    /// "AS15169  GOOGLE   US" - whatever is known about the hop's owner, the network name left out
    /// when it only repeats the AS name.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>(3);
        if (AsNumber is { Length: > 0 })
        {
            parts.Add(AsName is { Length: > 0 } ? $"AS{AsNumber}  {AsName}" : $"AS{AsNumber}");
        }
        else if (AsName is { Length: > 0 })
        {
            parts.Add(AsName);
        }

        if (NetworkName is { Length: > 0 } && NetworkName != AsName)
        {
            parts.Add(NetworkName);
        }

        if (CountryCode is { Length: > 0 })
        {
            parts.Add(CountryCode);
        }

        return string.Join("   ", parts);
    }
}

/// <summary>
/// One hop in a route: everything measured for it, plus whatever could be learned about it
/// afterwards.
///
/// Several probes per hop rather than one, which is what makes the difference between "this
/// router is rate-limiting its replies" and "packets are being lost here" observable at all. v1
/// sent a single probe per TTL and discarded the <see cref="IPStatus"/>, so a silent hop, a
/// rate-limited hop and a genuinely unreachable network were all rendered as the same asterisk.
/// </summary>
public sealed record TracerouteHop(int Ttl, IReadOnlyList<HopProbe> Probes)
{
    /// <summary>The distinct addresses that answered. More than one means the route is load-balanced across parallel paths.</summary>
    public IReadOnlyList<IPAddress> Responders { get; } =
    [
        .. Probes
            .Where(p => p.Address is not null)
            .Select(p => p.Address!)
            .Distinct()
            .ToList(),
    ];

    public bool Silent => Responders.Count == 0;

    public bool ReachedDestination => Probes.Any(p => p.Status == IPStatus.Success);

    public int AnsweredCount => Probes.Count(p => p.Answered);

    public double LossFraction => Probes.Count == 0 ? 1 : 1 - ((double)AnsweredCount / Probes.Count);

    public double? MinMs => Latencies.Count == 0 ? null : Latencies.Min();

    public double? AvgMs => Latencies.Count == 0 ? null : Latencies.Average();

    public double? MaxMs => Latencies.Count == 0 ? null : Latencies.Max();

    /// <summary>The most specific non-success status seen, used to explain an unreachable hop.</summary>
    public IPStatus? ProblemStatus => Probes
        .Select(p => p.Status)
        .FirstOrDefault(s => s is not (IPStatus.Success or IPStatus.TtlExpired or IPStatus.TimedOut or IPStatus.Unknown))
        is var status && status != default ? status : null;

    /// <summary>Reverse DNS for the first responder, filled in after the hop is reported.</summary>
    public string? Hostname { get; set; }

    public IpOwner? Owner { get; set; }

    /// <summary>The plain-language note explaining this hop, if it needs one.</summary>
    public string? Annotation { get; set; }

    private List<double> Latencies { get; } =
        [.. Probes.Where(p => p.LatencyMs is not null).Select(p => p.LatencyMs!.Value)];
}
