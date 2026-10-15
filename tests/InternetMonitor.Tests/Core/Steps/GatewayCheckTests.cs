using System.Net.NetworkInformation;
using InternetMonitor.Core.Steps.Checks;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

public sealed class GatewayCheckTests
{
    private static readonly PingOutcome PingOk = new(IPStatus.Success, 2, null);
    private static readonly PingOutcome PingTimedOut = new(IPStatus.TimedOut, null, null);
    private static readonly (int Port, TcpOutcome Outcome)? Port80 = (80, new TcpOutcome(true, false, 3, null));

    [Fact]
    public async Task A_successful_ping_returns_at_once_and_cancels_the_tcp_attempt()
    {
        using var race = new CancellationTokenSource();
        var tcp = new TaskCompletionSource<(int Port, TcpOutcome Outcome)?>();
        race.Token.Register(() => tcp.TrySetCanceled(race.Token));

        var result = await GatewayCheck.AwaitAnswersAsync(Task.FromResult(PingOk), tcp.Task, race)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Ping.Success);
        Assert.Null(result.Tcp);
        Assert.True(race.IsCancellationRequested);
    }

    [Fact]
    public async Task A_failed_ping_waits_for_tcp()
    {
        using var race = new CancellationTokenSource();
        var tcp = new TaskCompletionSource<(int Port, TcpOutcome Outcome)?>();

        Task<(PingOutcome Ping, (int Port, TcpOutcome Outcome)? Tcp)> pending =
            GatewayCheck.AwaitAnswersAsync(Task.FromResult(PingTimedOut), tcp.Task, race);
        Assert.False(pending.IsCompleted);

        tcp.SetResult(Port80);
        var result = await pending;

        Assert.False(result.Ping.Success);
        Assert.Equal(80, result.Tcp!.Value.Port);
    }

    [Fact]
    public async Task When_tcp_answers_first_the_ping_is_still_awaited()
    {
        using var race = new CancellationTokenSource();
        var ping = new TaskCompletionSource<PingOutcome>();

        Task<(PingOutcome Ping, (int Port, TcpOutcome Outcome)? Tcp)> pending =
            GatewayCheck.AwaitAnswersAsync(ping.Task, Task.FromResult(Port80), race);
        Assert.False(pending.IsCompleted);

        ping.SetResult(PingTimedOut);
        var result = await pending;

        Assert.Equal(IPStatus.TimedOut, result.Ping.Status);
        Assert.NotNull(result.Tcp);
    }

    [Fact]
    public async Task A_cancelled_cycle_still_throws()
    {
        using var cycle = new CancellationTokenSource();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token);
        cycle.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GatewayCheck.AwaitAnswersAsync(Task.FromCanceled<PingOutcome>(cycle.Token), Task.FromResult(Port80), race));
    }
}
