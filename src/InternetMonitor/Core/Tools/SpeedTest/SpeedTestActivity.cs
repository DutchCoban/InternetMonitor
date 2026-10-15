namespace InternetMonitor.Core.Tools.SpeedTest;

/// <summary>
/// Remembers when speed tests ran, so other parts of the app can tell a line filled on purpose
/// from a line in trouble.
///
/// A speed test saturates the connection for several seconds, and TCP answers a full line by
/// retransmitting - so the network-health monitor would otherwise read every test as "many
/// retransmissions". The incident is still raised (the counters really did go up), but its
/// explanation says a speed test was running. Static, like <see cref="Steps.FaultSimulation"/>:
/// a manual test (Tools page) and an automatic one (<see cref="Monitors.SpeedTestScheduler"/>)
/// both go through <see cref="SpeedTestClient.RunAsync"/>, which is the one place that reports here.
/// </summary>
public static class SpeedTestActivity
{
    /// <summary>Only the last day matters - a network-health window is minutes long.</summary>
    private static readonly TimeSpan Keep = TimeSpan.FromDays(1);

    private static readonly object Gate = new();
    private static readonly List<(DateTimeOffset Start, DateTimeOffset? End)> Runs = [];

    /// <summary>A test has started; pair with <see cref="End"/>.</summary>
    public static void Begin(DateTimeOffset? at = null)
    {
        DateTimeOffset now = at ?? DateTimeOffset.UtcNow;
        lock (Gate)
        {
            Runs.RemoveAll(r => r.End is { } end && now - end > Keep);
            Runs.Add((now, null));
        }
    }

    /// <summary>The most recently started test has finished (or failed, or was cancelled).</summary>
    public static void End(DateTimeOffset? at = null)
    {
        DateTimeOffset now = at ?? DateTimeOffset.UtcNow;
        lock (Gate)
        {
            int open = Runs.FindLastIndex(r => r.End is null);
            if (open >= 0)
            {
                Runs[open] = (Runs[open].Start, now);
            }
        }
    }

    /// <summary>Whether any part of a speed test fell between <paramref name="from"/> and <paramref name="to"/>; a test still running counts up to now.</summary>
    public static bool During(DateTimeOffset from, DateTimeOffset to)
    {
        lock (Gate)
        {
            return Runs.Any(r => r.Start <= to && (r.End ?? DateTimeOffset.MaxValue) >= from);
        }
    }

    /// <summary>Forgets every recorded run - for the tests.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Runs.Clear();
        }
    }
}
