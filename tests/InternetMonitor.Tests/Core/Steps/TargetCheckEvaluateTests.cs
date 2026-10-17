using System.Net;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>
/// Step 7's verdict over what its probes found, fed directly: which situation wins when several
/// things are true at once, what the headline and detail say, and what the facts carry.
/// </summary>
[Collection("Localization")]
public sealed class TargetCheckEvaluateTests
{
    private static readonly TimeSpan Elapsed = TimeSpan.FromMilliseconds(80);

    private static HttpTimings Timings(int totalMs) => new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(totalMs - 35), TimeSpan.FromMilliseconds(totalMs));

    private static TargetEndpointResult Url(string name, bool reachable, int? status = 204, int totalMs = 120, int? expected = null, HttpStage failedAt = HttpStage.None, bool reachability = false) =>
        new(name, TargetKind.Url, $"https://{name}/", reachable, reachable ? status : null, expected, failedAt, reachable ? null : "no route", reachable ? Timings(totalMs) : null, IPAddress.Loopback, null, reachability);

    private static StepContext Context(int warningMs = 300, int errorMs = 1000) =>
        new(new CheckTargets { LatencyWarningMs = warningMs, LatencyErrorMs = errorMs });

    [Fact]
    public void One_reachable_url_is_ok_with_its_status_and_round_trip()
    {
        StepResult result = TargetCheck.Evaluate(Context(), [Url("site", true)], null, null, Elapsed);

        Assert.Equal(StepState.Ok, result.State);
        Assert.Equal("step.target.ok", result.HeadlineKey);
        Assert.Equal(120, result.ChartValue);
        Assert.Contains(result.Measurements, m => m.Label == "site" && m.Value.StartsWith("HTTP 204", StringComparison.Ordinal));
        Assert.Contains(result.Measurements, m => m.Value == "20 ms");
    }

    [Fact]
    public void A_captive_portal_wins_over_everything_else()
    {
        var portal = new CaptivePortalOutcome(true, true, 302, "login.hotel.example", null);

        StepResult result = TargetCheck.Evaluate(Context(), [Url("site", false, failedAt: HttpStage.Tls, reachability: true)], null, portal, Elapsed);

        Assert.Equal(StepState.Failed, result.State);
        Assert.Equal("step.target.captivePortal", result.HeadlineKey);
        var facts = (TargetFacts)result.Facts!;
        Assert.True(facts.CaptivePortal);
        Assert.Equal("login.hotel.example", facts.PortalHost);
        Assert.Contains(result.Measurements, m => m.Value == "login.hotel.example");
    }

    [Fact]
    public void Everything_down_is_a_failure_naming_the_stage_of_the_first()
    {
        StepResult result = TargetCheck.Evaluate(Context(), [Url("site", false, failedAt: HttpStage.Tcp)], null, null, Elapsed);

        Assert.Equal(StepState.Failed, result.State);
        Assert.Equal("step.target.failed", result.HeadlineKey);
        Assert.Equal("step.target.failed.detail", result.DetailKey);
        Assert.Equal("TCP connection", result.DetailArgs![0]);
        Assert.True(((TargetFacts)result.Facts!).AllUnreachable);

        StepResult two = TargetCheck.Evaluate(Context(), [Url("a", false, failedAt: HttpStage.Dns), Url("b", false, failedAt: HttpStage.Dns)], null, null, Elapsed);
        Assert.Equal("step.target.allFailed", two.HeadlineKey);
        Assert.Equal(2, two.HeadlineArgs![0]);
    }

    [Fact]
    public void One_of_two_down_is_a_warning_that_names_the_one()
    {
        StepResult result = TargetCheck.Evaluate(Context(), [Url("a", true), Url("b", false, failedAt: HttpStage.Http)], null, null, Elapsed);

        Assert.Equal(StepState.Warning, result.State);
        Assert.Equal("step.target.okMany", result.HeadlineKey);
        Assert.Equal([1, 2], result.HeadlineArgs!.Cast<int>());
        Assert.Equal("step.target.someFailed.detail", result.DetailKey);
        Assert.Equal("b", result.DetailArgs![0]);
    }

    [Fact]
    public void An_unexpected_status_and_a_server_error_are_warnings_with_their_own_detail()
    {
        StepResult mismatch = TargetCheck.Evaluate(Context(), [Url("api", true, status: 200, expected: 204)], null, null, Elapsed);
        Assert.Equal(StepState.Warning, mismatch.State);
        Assert.Equal("step.target.statusMismatch.detail", mismatch.DetailKey);
        Assert.Equal([200, 204], mismatch.DetailArgs!.Cast<int>());

        StepResult serverError = TargetCheck.Evaluate(Context(), [Url("api", true, status: 503)], null, null, Elapsed);
        Assert.Equal(StepState.Warning, serverError.State);
        Assert.Equal("step.target.serverError.detail", serverError.DetailKey);
    }

    [Fact]
    public void Broken_ipv6_next_to_working_ipv4_is_a_warning_and_a_dns_failure_on_ipv6_is_not_counted()
    {
        var v6Failed = new HttpOutcome(false, null, HttpStage.Tcp, "unreachable", null, 80, null, Timings(50));
        StepResult broken = TargetCheck.Evaluate(Context(), [Url("reach", true, reachability: true)], v6Failed, null, Elapsed);
        Assert.Equal(StepState.Warning, broken.State);
        Assert.Equal("step.target.ipv6Broken.detail", broken.DetailKey);
        Assert.True(((TargetFacts)broken.Facts!).Ipv6Broken);
        Assert.Contains(broken.Measurements, m => m.Label == "IPv6 (HTTP)");

        var v6NoDns = new HttpOutcome(false, null, HttpStage.Dns, "no AAAA", null, 80, null, Timings(5));
        StepResult undecided = TargetCheck.Evaluate(Context(), [Url("reach", true, reachability: true)], v6NoDns, null, Elapsed);
        Assert.Equal(StepState.Ok, undecided.State);
        Assert.Null(((TargetFacts)undecided.Facts!).Ipv6Reachable);
    }

    [Fact]
    public void A_slow_but_working_target_is_a_latency_warning_measured_by_its_slowest()
    {
        StepResult result = TargetCheck.Evaluate(Context(warningMs: 300), [Url("a", true, totalMs: 120), Url("b", true, totalMs: 900)], null, null, Elapsed);

        Assert.Equal(StepState.Warning, result.State);
        Assert.Equal(900, result.ChartValue);
        Assert.NotNull(result.DetailKey);
    }

    [Fact]
    public async Task Without_any_target_the_step_is_simply_ok()
    {
        var context = new StepContext(new CheckTargets { Targets = [] });

        StepResult result = await new TargetCheck().RunAsync(context, CancellationToken.None);

        Assert.Equal(StepState.Ok, result.State);
        Assert.Equal("step.target.noneConfigured", result.HeadlineKey);
    }
}
