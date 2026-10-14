using System.Net;
using System.Net.Sockets;
using System.Text;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Net;

public sealed class CaptivePortalTests
{
    private const string Expected = "Microsoft Connect Test";

    [Fact]
    public void Untouched_test_page_is_not_a_portal()
    {
        CaptivePortalOutcome o = CaptivePortalProbe.Classify(
            "HTTP/1.1 200 OK\r\nContent-Length: 22\r\nContent-Type: text/plain\r\n\r\nMicrosoft Connect Test", Expected);

        Assert.True(o.Answered);
        Assert.False(o.IsPortal);
    }

    [Fact]
    public void Redirect_is_a_portal_and_reports_where_it_points()
    {
        CaptivePortalOutcome o = CaptivePortalProbe.Classify(
            "HTTP/1.1 302 Found\r\nLocation: https://login.hotel-wifi.example/portal?mac=aa\r\nContent-Length: 0\r\n\r\n", Expected);

        Assert.True(o.IsPortal);
        Assert.Equal(302, o.StatusCode);
        Assert.Equal("login.hotel-wifi.example", o.RedirectHost);
    }

    [Fact]
    public void Status_511_is_a_portal()
    {
        Assert.True(CaptivePortalProbe.Classify("HTTP/1.1 511 Network Authentication Required\r\n\r\n<html></html>", Expected).IsPortal);
    }

    [Fact]
    public void A_200_with_someone_elses_page_is_a_portal()
    {
        Assert.True(CaptivePortalProbe.Classify("HTTP/1.1 200 OK\r\n\r\n<html><body>Welcome to Train Wi-Fi</body></html>", Expected).IsPortal);
    }

    [Theory]
    [InlineData("HTTP/1.1 403 Forbidden\r\n\r\nBlocked by policy")]
    [InlineData("HTTP/1.1 502 Bad Gateway\r\n\r\n")]
    [InlineData("garbage")]
    public void Anything_else_is_no_evidence(string response)
    {
        Assert.False(CaptivePortalProbe.Classify(response, Expected).IsPortal);
    }

    [Fact]
    public async Task ProbeAsync_recognises_a_redirecting_server()
    {
        // HTTP-inspecting security software (Microsoft Defender's Network Inspection Service, on
        // the machine this was written on) intermittently resets or stalls even loopback HTTP - a
        // 200-run measurement saw a quarter of connections cut while the server side completed
        // normally. A cut connection is "no evidence", never a false portal, so what matters is
        // that an exchange that does get through is classified right; a few attempts get one.
        CaptivePortalOutcome? outcome = null;
        for (int attempt = 0; attempt < 5 && outcome is not { Answered: true }; attempt++)
        {
            outcome = await ProbeRedirectingServerOnceAsync();
        }

        Assert.True(outcome!.IsPortal, outcome.Error);
        Assert.Equal("portal.example", outcome.RedirectHost);
    }

    private static async Task<CaptivePortalOutcome> ProbeRedirectingServerOnceAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task serve = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            NetworkStream stream = client.GetStream();
            var request = new StringBuilder();
            var buffer = new byte[1024];
            while (!request.ToString().Contains("\r\n\r\n"))
            {
                int read = await stream.ReadAsync(buffer);
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://portal.example/login\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply);
            client.Client.Shutdown(SocketShutdown.Send);

            // Wait for the probe to hang up before disposing: closing first would let Windows
            // answer the probe's last packet with a reset.
            try
            {
                while (await stream.ReadAsync(buffer) > 0)
                {
                }
            }
            catch (IOException)
            {
            }
        });

        CaptivePortalOutcome o = await CaptivePortalProbe.ProbeAsync(
            new Uri($"http://127.0.0.1:{port}/connecttest.txt"), Expected, TimeSpan.FromSeconds(2), CancellationToken.None);
        await Task.WhenAny(serve, Task.Delay(TimeSpan.FromSeconds(2)));
        return o;
    }

    [Fact]
    public void Analyzer_blames_the_login_page_over_the_dns_failure_it_causes()
    {
        StepSnapshot snapshot = Snapshot(
            dns: StepState.Failed,
            target: StepState.Skipped,
            facts: new TargetFacts([], CaptivePortal: true, PortalHost: "portal.example"));

        RootCauseResult result = RootCauseAnalyzer.Analyze(snapshot);

        Assert.Equal(RootCause.CaptivePortal, result.Cause);
        Assert.True(result.IsProblem);
    }

    [Fact]
    public void Analyzer_ignores_a_login_page_without_a_gateway()
    {
        StepSnapshot snapshot = Snapshot(
            gateway: StepState.Failed,
            dns: StepState.Failed,
            target: StepState.Skipped,
            facts: new TargetFacts([], CaptivePortal: true));

        Assert.Equal(RootCause.Gateway, RootCauseAnalyzer.Analyze(snapshot).Cause);
    }

    private static StepSnapshot Snapshot(
        StepState gateway = StepState.Ok,
        StepState dns = StepState.Ok,
        StepState target = StepState.Ok,
        StepFacts? facts = null)
    {
        StepResult R(StepId id, StepState state, StepFacts? f = null) =>
            new(id, state, id.ToString(), null, null, TimeSpan.Zero, DateTimeOffset.UtcNow, [], f);

        return new StepSnapshot(
        [
            R(StepId.Adapter, StepState.Ok),
            R(StepId.Link, StepState.Ok),
            R(StepId.IpAddress, StepState.Ok),
            R(StepId.Gateway, gateway),
            R(StepId.PublicIp, StepState.Failed),
            R(StepId.Dns, dns),
            R(StepId.Target, target, facts),
        ], DateTimeOffset.UtcNow);
    }
}
