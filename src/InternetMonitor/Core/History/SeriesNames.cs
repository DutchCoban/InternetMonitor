using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.History;

/// <summary>
/// Maps a step onto the history series it records into.
///
/// Four of these names are v1's probe ids rather than v2's step keys, and that is deliberate: the
/// files on disk are v1's files, so an upgrading user opens the History page and sees the weeks of
/// latency they already had instead of an empty chart. The measurement behind each of those four
/// is the same quantity measured the same way, so the continuity is real and not just a filename
/// trick.
///
/// Steps 1 and 2 are the exception. v1 recorded one combined "network" series for adapter, link
/// and address together; v2 splits that into two separate questions, so only the adapter inherits
/// the old series and the link starts fresh. Pretending v1's combined 0/1 was a link measurement
/// would put a line on the chart that means something different before and after the upgrade,
/// which is worse than a short history.
/// </summary>
public static class SeriesNames
{
    public static string? For(StepId step) => step switch
    {
        StepId.Adapter => "network",
        StepId.Link => "link",
        StepId.Gateway => "gateway",
        StepId.PublicIp => "internet",
        StepId.Dns => "dns",
        StepId.Target => "https",

        // An address is not a number worth charting. v1 reached the same conclusion and showed a
        // change log instead, which is the right answer and is what the History page does too.
        StepId.IpAddress => null,
        _ => null,
    };

    /// <summary>The unit a series is measured in, for axis labels and the hover card.</summary>
    public static string UnitFor(StepId step) => step switch
    {
        StepId.Adapter or StepId.Link => string.Empty,
        _ => "ms",
    };

    /// <summary>
    /// True for series whose value is a state rather than a magnitude. They are drawn as a
    /// connected/disconnected timeline rather than a line with a y-axis, because the midpoint
    /// between 0 and 1 means nothing.
    /// </summary>
    public static bool IsStateSeries(StepId step) => step is StepId.Adapter or StepId.Link;

    public static IReadOnlyList<StepId> Chartable =>
        [StepId.Gateway, StepId.PublicIp, StepId.Dns, StepId.Target, StepId.Adapter, StepId.Link];
}
