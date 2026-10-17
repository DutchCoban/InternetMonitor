using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Net;
using InternetMonitor.Tests.Net;
using Xunit;
using Xunit.Abstractions;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>
/// Step 7 run for real against 127.0.0.1: a URL, a TCP port and a ping target in one cycle, a
/// refused port that still counts as an answer, and a URL that is not one.
///
/// The URL assertions step aside on a machine whose security client intercepts loopback HTTP
/// (see <see cref="LoopbackHttpTests"/>); the TCP and ping targets are checked either way.
/// </summary>
[Collection("Localization")]
public sealed class TargetCheckLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_url_a_tcp_port_and_a_ping_against_loopback_are_all_reachable()
    {
        using var server = new LoopbackServer("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");
        var targets = new CheckTargets
        {
            Targets =
            [
                new TargetEndpoint("site", server.Url().ToString(), ExpectedStatusCode: 204),
                new TargetEndpoint("port", string.Empty, TargetKind.TcpPort, Host: "127.0.0.1", Port: server.Port),
                new TargetEndpoint("ping", string.Empty, TargetKind.Ping, Host: "127.0.0.1"),
            ],
        };

        StepResult result = await new TargetCheck().RunAsync(new StepContext(targets), CancellationToken.None);

        var facts = (TargetFacts)result.Facts!;
        TargetEndpointResult site = facts.Endpoints.Single(e => e.Name == "site");
        TargetEndpointResult port = facts.Endpoints.Single(e => e.Name == "port");
        TargetEndpointResult ping = facts.Endpoints.Single(e => e.Name == "ping");

        Assert.True(port.Reachable, port.Error);
        Assert.Equal($"127.0.0.1:{server.Port}", port.Url);
        Assert.NotNull(port.ConnectMs);
        Assert.Contains(result.Measurements, m => m.Label == "port" && m.Value.StartsWith("TCP 127.0.0.1:", StringComparison.Ordinal));

        Assert.True(ping.Reachable, ping.Error);
        Assert.NotNull(ping.ConnectMs);
        Assert.Contains(result.Measurements, m => m.Label == "ping" && m.Value.EndsWith(" ms", StringComparison.Ordinal));

        bool intercepted = site.StatusCode is { } status && status != 204
            || !site.Reachable && site.FailedStage is HttpStage.Tcp or HttpStage.Http;
        if (intercepted)
        {
            output.WriteLine($"Skipped the URL assertions: loopback HTTP is intercepted on this machine (stage {site.FailedStage}, status {site.StatusCode?.ToString() ?? "-"}, error '{site.Error}').");
            return;
        }

        Assert.True(site.Reachable, site.Error);
        Assert.Equal(204, site.StatusCode);
        Assert.False(site.StatusMismatch);
        Assert.Equal(StepState.Ok, result.State);
        Assert.Equal("step.target.okMany", result.HeadlineKey);
        Assert.False(facts.CaptivePortal);
        Assert.Null(facts.Ipv6Reachable);
    }

    [Fact]
    public async Task A_refused_port_counts_as_an_answer_and_a_malformed_url_as_a_failed_target()
    {
        int closed = LoopbackServer.ClosedPort();
        var targets = new CheckTargets
        {
            Targets =
            [
                new TargetEndpoint("closed", string.Empty, TargetKind.TcpPort, Host: "127.0.0.1", Port: closed),
                new TargetEndpoint("bad", "not a url"),
            ],
        };

        StepResult result = await new TargetCheck().RunAsync(new StepContext(targets), CancellationToken.None);

        var facts = (TargetFacts)result.Facts!;
        Assert.True(facts.Endpoints.Single(e => e.Name == "closed").Reachable);
        TargetEndpointResult bad = facts.Endpoints.Single(e => e.Name == "bad");
        Assert.False(bad.Reachable);
        Assert.Equal("Invalid URL", bad.Error);

        Assert.Equal(StepState.Warning, result.State);
        Assert.Equal("step.target.someFailed.detail", result.DetailKey);
        Assert.Equal("bad", result.DetailArgs![0]);
    }
}
