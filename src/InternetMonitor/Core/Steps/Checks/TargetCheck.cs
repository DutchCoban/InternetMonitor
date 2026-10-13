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
                new TargetFacts([]));
        }

        TargetEndpointResult[] results = await Task.WhenAll(
            targets.Select(t => CheckOneAsync(t, context.Targets.TargetTimeout, cancellationToken))).ConfigureAwait(false);

        var facts = new TargetFacts(results);

        var measurements = results.Select(r => new Measurement(r.Name, DescribeResult(r, loc))).ToList();

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
            return new StepResult(
                Id,
                StepState.Failed,
                results.Length == 1
                    ? loc.Format("step.target.failed", first.Name)
                    : loc.Format("step.target.allFailed", results.Length),
                loc.Format("step.target.failed.detail", loc.Get($"httpStage.{first.FailedStage.ToString().ToLowerInvariant()}"), first.Error ?? "-"),
                null,
                watch.Elapsed,
                DateTimeOffset.UtcNow,
                measurements,
                facts);
        }

        int failed = results.Count(r => !r.Reachable);
        bool anyServerError = results.Any(r => r.Reachable && r.StatusCode >= 500);
        bool anyStatusMismatch = results.Any(r => r.StatusMismatch);
        double? slowest = results.Where(r => r.Reachable).Max(ElapsedMsOf);

        StepState state = LatencyClassifier.Classify(slowest, context.Targets);
        if (failed > 0 || anyServerError || anyStatusMismatch)
        {
            state = StepState.Warning;
        }

        string headline = results.Length == 1
            ? HeadlineFor(results[0], slowest, loc)
            : loc.Format("step.target.okMany", results.Length - failed, results.Length);

        string? detail = failed > 0
            ? loc.Format("step.target.someFailed.detail", string.Join(", ", results.Where(r => !r.Reachable).Select(r => r.Name)))
            : anyStatusMismatch
                ? FormatMismatch(results.First(r => r.StatusMismatch), loc)
                : anyServerError
                    ? loc.Get("step.target.serverError.detail")
                    : state == StepState.Warning ? loc.Get("step.latency.high") : null;

        return new StepResult(
            Id,
            state,
            headline,
            detail,
            slowest,
            watch.Elapsed,
            DateTimeOffset.UtcNow,
            measurements,
            facts);
    }

    private static double? ElapsedMsOf(TargetEndpointResult r) => r.Timings?.Total.TotalMilliseconds ?? r.ConnectMs;

    private static string FormatMismatch(TargetEndpointResult r, LocalizationManager loc) =>
        loc.Format("step.target.statusMismatch.detail", r.StatusCode!.Value, r.ExpectedStatusCode!.Value);

    private static string HeadlineFor(TargetEndpointResult r, double? ms, LocalizationManager loc) => r.Kind switch
    {
        TargetKind.TcpPort => loc.Format("step.target.ok.tcp", r.Name, r.Url, ms?.ToString("F0") ?? "?"),
        _ => loc.Format("step.target.ok", r.Name, r.StatusCode ?? 0, ms?.ToString("F0") ?? "?"),
    };

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

    private static Task<TargetEndpointResult> CheckOneAsync(
        TargetEndpoint target,
        TimeSpan timeout,
        CancellationToken cancellationToken) => target.Kind switch
        {
            TargetKind.TcpPort => CheckTcpPortAsync(target, timeout, cancellationToken),
            _ => CheckUrlAsync(target, timeout, cancellationToken),
        };

    private static async Task<TargetEndpointResult> CheckUrlAsync(
        TargetEndpoint target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri))
        {
            // A malformed URL is a configuration mistake, not a network condition. Reporting it as
            // a failed endpoint puts it in front of the user instead of silently skipping it.
            return new TargetEndpointResult(target.Name, TargetKind.Url, target.Url, false, null, target.ExpectedStatusCode, HttpStage.None, "Invalid URL", null, null, null);
        }

        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(uri, timeout, cancellationToken).ConfigureAwait(false);

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
            null);
    }

    /// <summary>"host:port" is what every measurement and headline shows for a TcpPort target, so
    /// it is built once here rather than re-joined at each call site.</summary>
    private static string PortLabel(TargetEndpoint target) => $"{target.Host}:{target.Port}";

    private static async Task<TargetEndpointResult> CheckTcpPortAsync(
        TargetEndpoint target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string label = PortLabel(target);

        if (!IPAddress.TryParse(target.Host, out IPAddress? address))
        {
            DnsOutcome dns = await DnsResolve.ResolveAsync(target.Host, DnsSubBudget, cancellationToken).ConfigureAwait(false);
            if (!dns.Success)
            {
                string error = dns.TimedOut ? "DNS lookup timed out" : $"DNS lookup failed ({dns.Error})";
                return new TargetEndpointResult(target.Name, TargetKind.TcpPort, label, false, null, null, HttpStage.Dns, error, null, null, null);
            }

            address = dns.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? dns.Addresses[0];
        }

        TcpOutcome outcome = await TcpConnect.TryConnectAsync(address, target.Port, timeout, cancellationToken).ConfigureAwait(false);

        return outcome.Answered
            ? new TargetEndpointResult(target.Name, TargetKind.TcpPort, label, true, null, null, HttpStage.None, null, null, address, outcome.ElapsedMs)
            : new TargetEndpointResult(target.Name, TargetKind.TcpPort, label, false, null, null, HttpStage.Tcp, outcome.Error?.ToString() ?? "Timed out", null, address, outcome.ElapsedMs);
    }

    private static void AddTimingMeasurements(List<Measurement> measurements, LocalizationManager loc, HttpTimings timings)
    {
        void Add(string key, TimeSpan? value)
        {
            if (value is { } span)
            {
                measurements.Add(new Measurement(loc.Get(key), $"{span.TotalMilliseconds:F0} ms"));
            }
        }

        Add("measure.dnsTime", timings.Dns);
        Add("measure.tcpTime", timings.Tcp);
        Add("measure.tlsTime", timings.Tls);
        Add("measure.httpTime", timings.Http);
        Add("measure.totalTime", timings.Total);
    }
}
