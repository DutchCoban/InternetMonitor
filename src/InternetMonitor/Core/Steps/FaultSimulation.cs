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
