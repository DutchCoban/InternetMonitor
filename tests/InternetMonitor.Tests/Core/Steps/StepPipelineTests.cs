using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using Xunit;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>
/// The pipeline runs the local checks in order and stops at the first failure, runs the network
/// checks together and holds their failures back behind an earlier one, guards against a check
/// that throws, never runs two cycles at once, and honours the fault simulator.
/// </summary>
[Collection("Localization")]
public sealed class StepPipelineTests
{
    private static StepPipeline Pipeline(IReadOnlyList<IStepCheck> local, IReadOnlyList<IStepCheck> network) =>
        new(() => new CheckTargets(), local, network);

    [Fact]
    public async Task A_healthy_cycle_runs_every_check_once_and_publishes_a_complete_snapshot()
    {
        (List<IStepCheck> local, List<IStepCheck> network) = FakeCheck.Healthy();
        using StepPipeline pipeline = Pipeline(local, network);
        StepSnapshot? completed = null;
        var changes = new List<StepResult>();
        pipeline.CycleCompleted += s => completed = s;
        pipeline.StepChanged += changes.Add;

        await pipeline.RunCycleAsync();

        Assert.NotNull(completed);
        Assert.Same(completed, pipeline.LatestSnapshot);
        Assert.Equal(7, completed.All.Count);
        Assert.All(completed.All, r => Assert.Equal(StepState.Ok, r.State));
        Assert.All(local.Concat(network).Cast<FakeCheck>(), c => Assert.Equal(1, c.Runs));
        Assert.Contains(changes, r => r.State == StepState.Running);
        Assert.Null(completed.FirstFailure);
    }

    [Fact]
    public async Task A_local_failure_skips_everything_after_it_without_running_it()
    {
        List<IStepCheck> local = [FakeCheck.Ok(StepId.Adapter, true), FakeCheck.Failed(StepId.Link, true), FakeCheck.Ok(StepId.IpAddress, true)];
        List<IStepCheck> network = [FakeCheck.Ok(StepId.Gateway, false), FakeCheck.Ok(StepId.PublicIp, false), FakeCheck.Ok(StepId.Dns, false), FakeCheck.Ok(StepId.Target, false)];
        using StepPipeline pipeline = Pipeline(local, network);

        await pipeline.RunCycleAsync();

        StepSnapshot snapshot = pipeline.LatestSnapshot!;
        Assert.Equal(StepState.Ok, snapshot[StepId.Adapter]!.State);
        Assert.Equal(StepState.Failed, snapshot[StepId.Link]!.State);
        foreach (StepId later in new[] { StepId.IpAddress, StepId.Gateway, StepId.PublicIp, StepId.Dns, StepId.Target })
        {
            StepResult result = snapshot[later]!;
            Assert.Equal(StepState.Skipped, result.State);
            Assert.Equal("step.skipped", result.HeadlineKey);
        }

        Assert.Equal(0, ((FakeCheck)local[2]).Runs);
        Assert.All(network.Cast<FakeCheck>(), c => Assert.Equal(0, c.Runs));
        Assert.Equal(StepId.Link, snapshot.FirstFailure!.Step);
    }

    [Fact]
    public async Task Network_failures_behind_an_earlier_failure_are_reported_as_skipped_not_as_separate_problems()
    {
        (List<IStepCheck> local, _) = FakeCheck.Healthy();
        List<IStepCheck> network = [FakeCheck.Failed(StepId.Gateway, false), FakeCheck.Failed(StepId.PublicIp, false), FakeCheck.Ok(StepId.Dns, false), FakeCheck.Failed(StepId.Target, false)];
        using StepPipeline pipeline = Pipeline(local, network);

        await pipeline.RunCycleAsync();

        StepSnapshot snapshot = pipeline.LatestSnapshot!;
        Assert.Equal(StepState.Failed, snapshot[StepId.Gateway]!.State);
        Assert.Equal(StepState.Skipped, snapshot[StepId.PublicIp]!.State);
        Assert.Equal(StepState.Skipped, snapshot[StepId.Target]!.State);
        Assert.Equal(StepState.Ok, snapshot[StepId.Dns]!.State);
        Assert.Equal(StepId.Gateway, snapshot.FirstFailure!.Step);
        Assert.All(network.Cast<FakeCheck>(), c => Assert.Equal(1, c.Runs));
    }

    [Fact]
    public async Task A_check_that_throws_becomes_an_unexpected_failure_and_the_cycle_still_completes()
    {
        List<IStepCheck> local = [FakeCheck.Ok(StepId.Adapter, true), FakeCheck.Ok(StepId.Link, true), FakeCheck.Ok(StepId.IpAddress, true)];
        List<IStepCheck> network = [FakeCheck.Ok(StepId.Gateway, false), FakeCheck.Throwing(StepId.PublicIp, false), FakeCheck.Ok(StepId.Dns, false), FakeCheck.Ok(StepId.Target, false)];
        using StepPipeline pipeline = Pipeline(local, network);

        await pipeline.RunCycleAsync();

        StepResult result = pipeline.LatestSnapshot![StepId.PublicIp]!;
        Assert.Equal(StepState.Failed, result.State);
        Assert.Equal("step.error.unexpected", result.HeadlineKey);
        Assert.Equal("boom", result.Detail);
        Assert.Equal(StepState.Ok, pipeline.LatestSnapshot[StepId.Dns]!.State);
    }

    [Fact]
    public async Task A_second_cycle_while_one_is_running_is_a_no_op()
    {
        var gate = new TaskCompletionSource();
        var slow = new FakeCheck(StepId.Adapter, true, async (_, _) =>
        {
            await gate.Task;
            return FakeCheck.Result(StepId.Adapter, StepState.Ok);
        });
        using StepPipeline pipeline = Pipeline([slow], []);
        int completed = 0;
        pipeline.CycleCompleted += _ => Interlocked.Increment(ref completed);

        Task first = pipeline.RunCycleAsync();
        await Task.Delay(50);
        Task second = pipeline.RunCycleAsync();
        Assert.True(second.IsCompleted);
        Assert.Equal(1, slow.Runs);

        gate.SetResult();
        await first;
        await second;

        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task The_fault_simulator_forces_a_step_to_fail_and_the_rest_follow()
    {
        (List<IStepCheck> local, List<IStepCheck> network) = FakeCheck.Healthy();
        using StepPipeline pipeline = Pipeline(local, network);
        try
        {
            FaultSimulation.Set([StepId.Gateway]);
            Assert.True(FaultSimulation.ShouldFail(StepId.Gateway));

            await pipeline.RunCycleAsync();

            StepResult gateway = pipeline.LatestSnapshot![StepId.Gateway]!;
            Assert.Equal(StepState.Failed, gateway.State);
            Assert.Equal("step.simulated", gateway.HeadlineKey);
            // The other network checks ran in parallel and succeeded, so they stay Ok: only a
            // failure behind the gateway's is folded into it.
            Assert.Equal(StepState.Ok, pipeline.LatestSnapshot[StepId.Target]!.State);
            Assert.Equal(0, ((FakeCheck)network[0]).Runs);

            FaultSimulation.Toggle(StepId.Gateway, fail: false);
            await pipeline.RunCycleAsync();
            Assert.Equal(StepState.Ok, pipeline.LatestSnapshot![StepId.Gateway]!.State);
        }
        finally
        {
            FaultSimulation.Clear();
        }
    }
}
