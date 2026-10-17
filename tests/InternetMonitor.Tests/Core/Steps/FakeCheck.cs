using InternetMonitor.Core.Steps;

namespace InternetMonitor.Tests.Core.Steps;

/// <summary>A step check that answers whatever the test says, and counts how often it was asked.</summary>
internal sealed class FakeCheck(StepId id, bool isLocal, Func<StepContext, CancellationToken, Task<StepResult>> run) : IStepCheck
{
    private int _runs;

    public StepId Id => id;

    public bool IsLocal => isLocal;

    public int Runs => Volatile.Read(ref _runs);

    public Task<StepResult> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        return run(context, cancellationToken);
    }

    public static FakeCheck Ok(StepId id, bool isLocal) => new(id, isLocal, (_, _) => Task.FromResult(Result(id, StepState.Ok)));

    public static FakeCheck Failed(StepId id, bool isLocal) => new(id, isLocal, (_, _) => Task.FromResult(Result(id, StepState.Failed)));

    public static FakeCheck Throwing(StepId id, bool isLocal) => new(id, isLocal, (_, _) => throw new InvalidOperationException("boom"));

    public static StepResult Result(StepId id, StepState state, string? headline = null) =>
        new(id, state, headline ?? $"{id} {state}", null, state == StepState.Ok ? 5 : null, TimeSpan.FromMilliseconds(1), DateTimeOffset.UtcNow, [], null);

    /// <summary>The seven steps, all Ok, with the three local ones first - a healthy cycle.</summary>
    public static (List<IStepCheck> Local, List<IStepCheck> Network) Healthy() =>
    (
        [Ok(StepId.Adapter, true), Ok(StepId.Link, true), Ok(StepId.IpAddress, true)],
        [Ok(StepId.Gateway, false), Ok(StepId.PublicIp, false), Ok(StepId.Dns, false), Ok(StepId.Target, false)]
    );
}
