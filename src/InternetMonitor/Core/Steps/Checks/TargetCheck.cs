using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 7: can I reach the thing I actually wanted?
///
/// Every configured endpoint is checked at once. A <see cref="TargetKind.Url"/> target gets a
/// full, per-stage-timed DNS -> TCP -> TLS -> HTTP request, so a failure says *where* it broke
/// rather than just that it did; a <see cref="TargetKind.TcpPort"/> target gets a bare DNS (if
/// needed) -> TCP connect, for the services - databases, mail servers, anything else that is not
/// itself speaking HTTP - a URL cannot describe.
///
/// Status policy, carried over from v1: any HTTP response at all counts as reachable. The point is
/// proving the whole stack works end to end, not that "/" happens to be a valid route - real
/// endpoints 404 on their root all the time. A 5xx is a warning: the stack works and the server
/// answered, but the service behind it is unhealthy, which is a different problem from an
/// unreachable one. The same applies to a configured <see cref="EndpointConfig.ExpectedStatusCode"/>
/// that the server did not actually send - a mismatch is a warning, not a failure, for the same
/// reason: the stack worked.
///
/// A TCP connect that is actively *refused* still counts as reachable, matching
/// <see cref="TcpOutcome.Answered"/>'s reasoning elsewhere in this app: a refusal proves something
/// is listening at that address, which is exactly what this check is trying to establish. Only a
/// timeout or an unreachable error means nothing is there.
///
/// When the built-in reachability check fails - or answers with something other than its 204 -
/// one extra plain-HTTP probe asks whether a login page is in the way (hotel, train, guest Wi-Fi).
/// If so, that is the finding: "log in first" is what the user needs to hear, not which TLS stage
/// a login page happened to break.
///
/// When step 3 found a global IPv6 address, one more request goes out over IPv6 alongside the
/// rest. IPv6 that is offered but broken is a classic hidden fault - browsers try it first and
/// sites load slowly or only sometimes - and it shows nowhere else, since every other check is
/// IPv4. It makes the step a warning, never a failure: IPv4 still works.
/// </summary>
public sealed class TargetCheck : IStepCheck
{
    private static readonly TimeSpan DnsSubBudget = TimeSpan.FromSeconds(1.5);

    public StepId Id => StepId.Target;

    public bool IsLocal => false;

    public async Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        LocalizationManager loc = LocalizationManager.Instance;
        IReadOnlyList<TargetEndpoint> targets = context.Targets.Targets;

        if (targets.Count == 0)
        {
            return new StepResult(
                Id,
                StepState.Ok,
                loc.Get("step.target.noneConfigured"),
                null,
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                [],
                new TargetFacts([]))
            {
                HeadlineKey = "step.target.noneConfigured",
            };
        }

        Task<HttpOutcome?> ipv6Check = CheckIpv6Async(context, cancellationToken);
        TargetEndpointResult[] results = await Task.WhenAll(
            targets.Select(t => CheckOneAsync(t, context.Targets.TargetTimeout, context.LocalAddress, context.DnsServers, cancellationToken))).ConfigureAwait(false);
        HttpOutcome? ipv6 = await ipv6Check.ConfigureAwait(false);

        // A failed AAAA lookup is "not tested", not "broken": without an IPv6 address for a name, a
        // browser does not try IPv6 for it either, so nothing is slowed down.
        bool? ipv6Reachable = ipv6 is null || ipv6.FailedStage == HttpStage.Dns ? null : ipv6.Succeeded;
        string? ipv6Error = ipv6 is { Succeeded: false } failedV6 ? failedV6.Error : null;

        CaptivePortalOutcome? portal = await ProbeForLoginPageAsync(targets, results, context, cancellationToken).ConfigureAwait(false);
        var facts = new TargetFacts(results, portal?.IsPortal == true, portal?.RedirectHost, ipv6Reachable, ipv6Error);

        var measurements = results.Select(r => new Measurement(r.Name, DescribeResult(r, loc))).ToList();
        if (ipv6 is not null)
        {
            measurements.Add(new Measurement(
                loc.Get("measure.ipv6Http"),
                ipv6.Succeeded
                    ? $"HTTP {ipv6.StatusCode} ({ipv6.Timings.Total.TotalMilliseconds:F0} ms)"
                    : loc.Format("measure.failedAt", loc.Get($"httpStage.{ipv6.FailedStage.ToString().ToLowerInvariant()}"))));
        }

        if (facts.CaptivePortal)
        {
            measurements.Add(new Measurement(loc.Get("measure.captivePortal"), facts.PortalHost ?? $"HTTP {portal!.StatusCode}"));

            return new StepResult(
                Id,
                StepState.Failed,
                loc.Get("step.target.captivePortal"),
                loc.Get("step.target.captivePortal.detail"),
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                measurements,
                facts)
            {
                HeadlineKey = "step.target.captivePortal",
                DetailKey = "step.target.captivePortal.detail",
            };
        }

        // Per-stage timings are worth showing for a single endpoint; with several they would
        // swamp the panel, and the per-endpoint detail view is the right place for them. Only a
        // Url target has stages to break down - a TcpPort target's one number is already in the
        // measurement row above.
        if (results.Length == 1 && results[0].Timings is { } timings)
        {
            AddTimingMeasurements(measurements, loc, timings);
        }

        if (facts.AllUnreachable)
        {
            TargetEndpointResult first = results[0];
            string stageKey = $"httpStage.{first.FailedStage.ToString().ToLowerInvariant()}";

            string headlineKey = results.Length == 1 ? "step.target.failed" : "step.target.allFailed";
            object[] headlineArgs = results.Length == 1 ? [first.Name] : [results.Length];
            object[] detailArgs = [loc.Get(stageKey), first.Error ?? "-"];

            return new StepResult(
                Id,
                StepState.Failed,
                loc.Format(headlineKey, headlineArgs),
                loc.Format("step.target.failed.detail", detailArgs),
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                measurements,
                facts)
            {
                HeadlineKey = headlineKey,
                HeadlineArgs = headlineArgs,
                DetailKey = "step.target.failed.detail",
                DetailArgs = [LocalizationManager.GetEnglish(stageKey), first.Error ?? "-"],
            };
        }

        int failed = results.Count(r => !r.Reachable);
        bool anyServerError = results.Any(r => r.Reachable && r.StatusCode >= 500);
        bool anyStatusMismatch = results.Any(r => r.StatusMismatch);
        double? slowest = results.Where(r => r.Reachable).Max(ElapsedMsOf);

        StepState state = LatencyClassifier.Classify(slowest, context.Targets);
        if (failed > 0 || anyServerError || anyStatusMismatch || facts.Ipv6Broken)
        {
            state = StepState.Warning;
        }

        (string headlineKey2, object[] headlineArgs2) = results.Length == 1
            ? HeadlineFor(results[0], slowest)
            : ("step.target.okMany", [results.Length - failed, results.Length]);

        (string? detailKey, object[] detailArgs2) = failed > 0
            ? ("step.target.someFailed.detail", [string.Join(", ", results.Where(r => !r.Reachable).Select(r => r.Name))])
            : anyStatusMismatch
                ? MismatchDetail(results.First(r => r.StatusMismatch))
                : anyServerError
                    ? ("step.target.serverError.detail", [])
                    : facts.Ipv6Broken
                        ? ("step.target.ipv6Broken.detail", [])
                        : state == StepState.Warning ? LatencyClassifier.WarningDetail(context) : (null, []);

        return new StepResult(
            Id,
            state,
            loc.Format(headlineKey2, headlineArgs2),
            detailKey is null ? null : loc.Format(detailKey, detailArgs2),
            slowest,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts)
        {
            HeadlineKey = headlineKey2,
            HeadlineArgs = headlineArgs2,
            DetailKey = detailKey,
            DetailArgs = detailArgs2,
        };
    }

    /// <summary>
    /// Runs the login-page probe, but only when the built-in reachability check did not get its
    /// expected answer - a healthy connection never pays for it.
    /// </summary>
    private static async Task<CaptivePortalOutcome?> ProbeForLoginPageAsync(
        IReadOnlyList<TargetEndpoint> targets,
        TargetEndpointResult[] results,
        StepContext context,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].IsReachabilityCheck && (!results[i].Reachable || results[i].StatusMismatch))
            {
                return await CaptivePortalProbe.ProbeAsync(
                    context.Targets.CaptivePortalProbeUrl,
                    context.Targets.CaptivePortalProbeBody,
                    context.Targets.CaptivePortalProbeTimeout,
                    cancellationToken,
                    context.LocalAddress,
                    context.DnsServers).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// The IPv6 request, only when step 3 found a global IPv6 address: the name is looked up (AAAA)
    /// over the adapter's IPv4 DNS servers, the connection leaves from the IPv6 address. Null when
    /// there is nothing to test.
    /// </summary>
    private static async Task<HttpOutcome?> CheckIpv6Async(StepContext context, CancellationToken cancellationToken)
    {
        if (context.GlobalIpv6 is not { } source)
        {
            return null;
        }

        return await TlsTimedRequest.RequestAsync(
            context.Targets.Ipv6CheckUrl,
            context.Targets.TargetTimeout,
            [],
            cancellationToken,
            context.LocalAddress,
            context.DnsServers,
            new IPv6Path(source)).ConfigureAwait(false);
    }

    private static double? ElapsedMsOf(TargetEndpointResult r) => r.Timings?.Total.TotalMilliseconds ?? r.ConnectMs;

    private static (string? Key, object[] Args) MismatchDetail(TargetEndpointResult r) =>
        ("step.target.statusMismatch.detail", [r.StatusCode!.Value, r.ExpectedStatusCode!.Value]);

    private static (string Key, object[] Args) HeadlineFor(TargetEndpointResult r, double? ms)
    {
        string msText = ms?.ToString("F0") ?? "?";
        return r.Kind switch
        {
            TargetKind.TcpPort => ("step.target.ok.tcp", [r.Name, r.Url, msText]),
            _ => ("step.target.ok", [r.Name, r.StatusCode ?? 0, msText]),
        };
    }

    private static string DescribeResult(TargetEndpointResult r, LocalizationManager loc)
    {
        if (!r.Reachable)
        {
            return loc.Format("measure.failedAt", loc.Get($"httpStage.{r.FailedStage.ToString().ToLowerInvariant()}"));
        }

        return r.Kind switch
        {
            TargetKind.TcpPort => $"TCP {r.Url} ({r.ConnectMs:F0} ms)",
            _ => $"HTTP {r.StatusCode} ({r.Timings?.Total.TotalMilliseconds:F0} ms)",
        };
    }

    /// <summary>
    /// <paramref name="source"/> and <paramref name="dnsServers"/> are the adapter steps 1-4
    /// described: names are resolved through that adapter's own DNS servers and connections leave
    /// from its address, so this step tests the same path as the ones before it rather than
    /// whichever adapter Windows' routing happens to prefer.
    /// </summary>
    private static Task<TargetEndpointResult> CheckOneAsync(
        TargetEndpoint target,
        TimeSpan timeout,
        IPAddress? source,
        IReadOnlyList<IPAddress> dnsServers,
        CancellationToken cancellationToken) => target.Kind switch
        {
            TargetKind.TcpPort => CheckTcpPortAsync(target, timeout, source, dnsServers, cancellationToken),
            _ => CheckUrlAsync(target, timeout, source, dnsServers, cancellationToken),
        };

    private static async Task<TargetEndpointResult> CheckUrlAsync(
        TargetEndpoint target,
        TimeSpan timeout,
        IPAddress? source,
        IReadOnlyList<IPAddress> dnsServers,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri))
        {
            // A malformed URL is a configuration mistake, not a network condition. Reporting it as
            // a failed endpoint puts it in front of the user instead of silently skipping it.
            return new TargetEndpointResult(target.Name, TargetKind.Url, target.Url, false, null, target.ExpectedStatusCode, HttpStage.None, "Invalid URL", null, null, null);
        }

        IReadOnlyList<RequestHeader> headers = target.Headers.Count == 0
            ? []
            : [.. target.Headers.Select(h => new RequestHeader(h.Name, h.Value))];
        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(uri, timeout, headers, cancellationToken, source, dnsServers).ConfigureAwait(false);

        return new TargetEndpointResult(
            target.Name,
            TargetKind.Url,
            target.Url,
            outcome.Succeeded,
            outcome.StatusCode,
            target.ExpectedStatusCode,
            outcome.FailedStage,
            outcome.Error,
            outcome.Timings,
            outcome.RemoteAddress,
            null,
            target.IsReachabilityCheck);
    }

    /// <summary>"host:port" is what every measurement and headline shows for a TcpPort target, so
    /// it is built once here rather than re-joined at each call site.</summary>
    private static string PortLabel(TargetEndpoint target) => $"{target.Host}:{target.Port}";

    /// <summary>
    /// A TcpPort target: resolve the host through the adapter's DNS servers unless it is an address,
    /// then one connect. A refused connection counts as reachable - see the class summary.
    /// </summary>
    private static async Task<TargetEndpointResult> CheckTcpPortAsync(
        TargetEndpoint target,
        TimeSpan timeout,
        IPAddress? source,
        IReadOnlyList<IPAddress> dnsServers,
        CancellationToken cancellationToken)
    {
        string label = PortLabel(target);

        if (!IPAddress.TryParse(target.Host, out IPAddress? address))
        {
            DnsOutcome dns = await DnsResolve.ResolveViaAsync(target.Host, dnsServers, source, DnsSubBudget, cancellationToken).ConfigureAwait(false);
            if (!dns.Success)
            {
                string error = dns.TimedOut ? "DNS lookup timed out" : $"DNS lookup failed ({dns.Error})";
                return new TargetEndpointResult(target.Name, TargetKind.TcpPort, label, false, null, null, HttpStage.Dns, error, null, null, null);
            }

            address = dns.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? dns.Addresses[0];
        }

        TcpOutcome outcome = await TcpConnect.TryConnectAsync(address, target.Port, timeout, cancellationToken, source).ConfigureAwait(false);

        return outcome.Answered
            ? new TargetEndpointResult(target.Name, TargetKind.TcpPort, label, true, null, null, HttpStage.None, null, null, address, outcome.ElapsedMs)
            : new TargetEndpointResult(target.Name, TargetKind.TcpPort, label, false, null, null, HttpStage.Tcp, outcome.Error?.ToString() ?? "Timed out", null, address, outcome.ElapsedMs);
    }

    /// <summary>
    /// The per-stage timings of one request as measurement rows; a stage that was never reached is
    /// left out, and a cached DNS answer says so instead of showing 0 ms.
    /// </summary>
    private static void AddTimingMeasurements(List<Measurement> measurements, LocalizationManager loc, HttpTimings timings)
    {
        void Add(string key, TimeSpan? value)
        {
            if (value is { } span)
            {
                measurements.Add(new Measurement(loc.Get(key), $"{span.TotalMilliseconds:F0} ms"));
            }
        }

        if (timings.DnsFromCache)
        {
            measurements.Add(new Measurement(loc.Get("measure.dnsTime"), loc.Get("measure.dnsFromCache")));
        }
        else
        {
            Add("measure.dnsTime", timings.Dns);
        }

        Add("measure.tcpTime", timings.Tcp);
        Add("measure.tlsTime", timings.Tls);
        Add("measure.httpTime", timings.Http);
        Add("measure.totalTime", timings.Total);
    }
}
