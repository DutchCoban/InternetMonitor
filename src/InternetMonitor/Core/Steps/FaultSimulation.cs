namespace InternetMonitor.Core.Steps;

/// <summary>
/// Forces chosen steps to fail, so the failure paths can be exercised without breaking anything
/// real.
///
/// This is the highest-value testing affordance in the whole project for the least code. The
/// behaviour that most needs checking - the focus arrow moving, downstream steps reporting as
/// skipped rather than as six separate problems, an incident opening with a timeline and a
/// plain-language explanation, the automatic traceroute - only happens when something breaks, and
/// the alternative is physically unplugging a cable and racing to watch the screen. Here it is a
/// checkbox.
///
/// Debug builds only. In a Release build <see cref="ShouldFail"/> is compiled to a constant false,
/// so a shipped binary cannot be talked into lying about the state of the network.
/// </summary>
public static class FaultSimulation
{
    private static readonly HashSet<StepId> Forced = [];
    private static readonly object Gate = new();

    /// <summary>Raised when the simulated set changes, so the UI can run a fresh cycle at once.</summary>
    public static event Action? Changed;

    public static bool IsActive
    {
        get
        {
#if DEBUG
            lock (Gate)
            {
                return Forced.Count > 0;
            }
#else
            return false;
#endif
        }
    }

    public static IReadOnlyCollection<StepId> ForcedFailures
    {
        get
        {
            lock (Gate)
            {
                return [.. Forced];
            }
        }
    }

    public static void Set(IEnumerable<StepId> steps)
    {
        lock (Gate)
        {
            Forced.Clear();
            foreach (StepId step in steps)
            {
                Forced.Add(step);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Forces a step to fail, or stops forcing it, and raises <see cref="Changed"/> so a fresh cycle
    /// shows the effect straight away.
    /// </summary>
    public static void Toggle(StepId step, bool fail)
    {
        lock (Gate)
        {
            if (fail)
            {
                Forced.Add(step);
            }
            else
            {
                Forced.Remove(step);
            }
        }

        Changed?.Invoke();
    }

    public static void Clear() => Set([]);

    /// <summary>
    /// An artificial delay inserted before each network check, so the in-flight states can be
    /// looked at rather than guessed at. A healthy cycle completes in under 300ms, which is good
    /// news for the product and bad news for anyone trying to verify that the focus arrow and the
    /// spinners render - they are on screen for a fraction of a second. Debug builds only.
    /// </summary>
    public static TimeSpan StepDelay { get; set; } = TimeSpan.Zero;

    public static async Task ApplyDelayAsync(IStepCheck check, CancellationToken cancellationToken)
    {
#if DEBUG
        if (!check.IsLocal && StepDelay > TimeSpan.Zero)
        {
            await Task.Delay(StepDelay, cancellationToken).ConfigureAwait(false);
        }
#else
        await Task.CompletedTask.ConfigureAwait(false);
#endif
    }

    public static bool ShouldFail(StepId step)
    {
#if DEBUG
        lock (Gate)
        {
            return Forced.Contains(step);
        }
#else
        return false;
#endif
    }
}
