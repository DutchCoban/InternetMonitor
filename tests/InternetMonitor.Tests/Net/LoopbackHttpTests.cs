using System.Net;
using InternetMonitor.Net;
using Xunit;
using Xunit.Abstractions;

namespace InternetMonitor.Tests.Net;

/// <summary>
/// The staged HTTP request and the TCP helpers against a listener on 127.0.0.1: which stage fails
/// says what is wrong, and a header never smuggles a second line into the request.
///
/// Some machines run a security client that intercepts TCP connections, loopback included, and
/// answers for the server (a 502, or a reset) now and then. A test that sees an answer its own
/// server did not send reports that and stops, rather than failing over something outside the
/// code under test; on a machine without such a client every test runs to the end.
/// </summary>
public sealed class LoopbackHttpTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_204_from_the_server_is_a_success_with_every_stage_but_tls_timed()
    {
        using var server = new LoopbackServer("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n");

        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(server.Url(), Timeout, [], CancellationToken.None);
        if (Intercepted(outcome, server))
        {
            return;
        }

        Assert.True(outcome.Succeeded, Describe(outcome, server));
        Assert.Equal(204, outcome.StatusCode);
        Assert.Equal(HttpStage.None, outcome.FailedStage);
        Assert.Equal(IPAddress.Loopback, outcome.RemoteAddress);
        Assert.Equal(server.Port, outcome.Port);
        Assert.NotNull(outcome.Timings.Tcp);
        Assert.NotNull(outcome.Timings.Http);
        Assert.Null(outcome.Timings.Tls);
        Assert.Null(outcome.TlsProtocol);
        Assert.StartsWith("GET /probe HTTP/1.1", server.LastRequest);
    }

    [Fact]
    public async Task A_closed_port_fails_at_the_tcp_stage()
    {
        int port = LoopbackServer.ClosedPort();

        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(new Uri($"http://127.0.0.1:{port}/"), Timeout, [], CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(HttpStage.Tcp, outcome.FailedStage);
        Assert.Null(outcome.StatusCode);
    }

    [Fact]
    public async Task Garbage_from_the_server_fails_at_the_http_stage()
    {
        using var server = new LoopbackServer("this is not http\r\n\r\n");

        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(server.Url(), Timeout, [], CancellationToken.None);
        if (Intercepted(outcome, server))
        {
            return;
        }

        Assert.False(outcome.Succeeded, Describe(outcome, server));
        Assert.Equal(HttpStage.Http, outcome.FailedStage);
    }

    [Fact]
    public async Task Https_against_a_plain_server_fails_at_the_tls_stage()
    {
        using var server = new LoopbackServer("HTTP/1.1 200 OK\r\n\r\n", readRequest: false);

        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(server.Url(scheme: "https"), Timeout, [], CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(HttpStage.Tls, outcome.FailedStage);
        Assert.NotNull(outcome.Timings.Tcp);
    }

    [Fact]
    public async Task Custom_headers_reach_the_server_on_one_line_each_whatever_they_contain()
    {
        using var server = new LoopbackServer("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        RequestHeader[] headers = [new("X-Token", "abc"), new("X-Evil", "one\r\nX-Injected: yes")];

        HttpOutcome outcome = await TlsTimedRequest.RequestAsync(server.Url(), Timeout, headers, CancellationToken.None);
        if (Intercepted(outcome, server))
        {
            return;
        }

        Assert.True(outcome.Succeeded, Describe(outcome, server));
        Assert.Contains("X-Token: abc", server.LastRequest);
        Assert.DoesNotContain("\r\nX-Injected:", server.LastRequest);
    }

    [Fact]
    public async Task Tcp_connect_tells_an_open_port_from_a_refused_one_and_both_count_as_an_answer()
    {
        using var server = new LoopbackServer("HTTP/1.1 200 OK\r\n\r\n", readRequest: false);
        int closed = LoopbackServer.ClosedPort();

        TcpOutcome open = await TcpConnect.TryConnectAsync(IPAddress.Loopback, server.Port, Timeout, CancellationToken.None);
        TcpOutcome refused = await TcpConnect.TryConnectAsync(IPAddress.Loopback, closed, Timeout, CancellationToken.None);

        Assert.True(open.Connected);
        Assert.True(open.Answered);
        Assert.NotNull(open.ElapsedMs);
        Assert.False(refused.Connected);
        Assert.True(refused.Refused);
        Assert.True(refused.Answered);

        (int Port, TcpOutcome Outcome)? first = await TcpConnect.FirstAnsweringPortAsync(IPAddress.Loopback, [server.Port], Timeout, CancellationToken.None);
        Assert.Equal(server.Port, first!.Value.Port);
        Assert.True(first.Value.Outcome.Connected);

        IReadOnlyList<(IPAddress Address, (int Port, TcpOutcome Outcome)? Result)> sweep = await TcpConnect.SweepAsync([IPAddress.Loopback], [closed, server.Port], Timeout, CancellationToken.None);
        Assert.Single(sweep);
        Assert.NotNull(sweep[0].Result);
    }

    /// <summary>
    /// True when the answer cannot have come from our server: a status it never sends, or a
    /// connection torn down although the server was there. Says so in the test output.
    /// </summary>
    private bool Intercepted(HttpOutcome outcome, LoopbackServer server)
    {
        bool foreignStatus = outcome.StatusCode is { } status && !server.Reply.Contains($" {status} ", StringComparison.Ordinal);
        bool tornDown = !outcome.Succeeded
            && outcome.FailedStage is HttpStage.Http or HttpStage.Tcp
            && (string.IsNullOrEmpty(outcome.Error) || outcome.Error.Contains("forcibly", StringComparison.OrdinalIgnoreCase) || outcome.Error.Contains("reset", StringComparison.OrdinalIgnoreCase));
        if (!foreignStatus && !tornDown)
        {
            return false;
        }

        output.WriteLine($"Skipped: loopback HTTP is intercepted on this machine ({Describe(outcome, server)}).");
        return true;
    }

    private static string Describe(HttpOutcome outcome, LoopbackServer server) =>
        $"stage {outcome.FailedStage}, status {outcome.StatusCode?.ToString() ?? "-"}, error '{outcome.Error}', server port {server.Port}, request seen: {server.LastRequest.Length > 0}";
}
