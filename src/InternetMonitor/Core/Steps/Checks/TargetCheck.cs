using System.Diagnostics;
using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Steps.Checks;

/// <summary>
/// Step 7: can I reach the thing I actually wanted?
///
/// Every configured endpoint is checked at once with a full, per-stage-timed DNS -> TCP -> TLS ->
/// HTTP request, so a failure here says *where* it broke rather than just that it did.
///
/// Status policy, carried over from v1: any HTTP response at all counts as reachable. The point is
/// proving the whole stack works end to end, not that "/" happens to be a valid route - real
/// endpoints 404 on their root all the time. A 5xx is a warning: the stack works and the server
/// answered, but the service behind it is unhealthy, which is a different problem from an
/// unreachable one.
/// </summary>
public sealed class TargetCheck : IStepCheck
{
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

        var measurements = results
            .Select(r => new Measurement(
                r.Name,
                r.Reachable
                    ? $"HTTP {r.StatusCode} ({r.Timings?.Total.TotalMilliseconds:F0} ms)"
                    : loc.Format("measure.failedAt", loc.Get($"httpStage.{r.FailedStage.ToString().ToLowerInvariant()}"))))
            .ToList();

        // Per-stage timings are worth showing for a single endpoint; with several they would
        // swamp the panel, and the per-endpoint detail view is the right place for them.
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
        double? slowest = results.Where(r => r.Reachable).Max(r => r.Timings?.Total.TotalMilliseconds);

        StepState state = LatencyClassifier.Classify(slowest, context.Targets);
        if (failed > 0 || anyServerError)
        {
            state = StepState.Warning;
        }

        string headline = results.Length == 1
            ? loc.Format("step.target.ok", results[0].Name, results[0].StatusCode ?? 0, slowest?.ToString("F0") ?? "?")
            : loc.Format("step.target.okMany", results.Length - failed, results.Length);

        string? detail = failed > 0
            ? loc.Format("step.target.someFailed.detail", string.Join(", ", results.Where(r => !r.Reachable).Select(r => r.Name)))
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

    private static async Task<TargetEndpointResult> CheckOneAsync(
        TargetEndpoint target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out Uri? uri))
        {
            // A malformed URL is a configuration mistake, not a network condition. Reporting it as
            // a failed endpoint puts it in front of the user instead of silently skipping it.
            return new TargetEndpointResult(target.Name, target.Url, false, null, HttpStage.None, "Invalid URL", null, null);
        }

        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(uri, timeout, cancellationToken).ConfigureAwait(false);

        return new TargetEndpointResult(
            target.Name,
            target.Url,
            outcome.Succeeded,
            outcome.StatusCode,
            outcome.FailedStage,
            outcome.Error,
            outcome.Timings,
            outcome.RemoteAddress);
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
