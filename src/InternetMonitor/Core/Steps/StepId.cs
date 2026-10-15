namespace InternetMonitor.Core.Steps;

/// <summary>
/// The seven steps, in the order a person actually reasons about a broken connection: is the card
/// there, is something plugged into it, do I have an address, can I reach my router, can I reach
/// the internet at all, can I turn a name into an address, and can I reach the thing I actually
/// wanted.
///
/// The numbering is the dependency order and is relied on: a step can only be short-circuited by a
/// step with a lower number, and the focus arrow sits on the lowest-numbered step still running.
/// </summary>
public enum StepId
{
    Adapter = 1,
    Link = 2,
    IpAddress = 3,
    Gateway = 4,
    PublicIp = 5,
    Dns = 6,
    Target = 7,
}

/// <summary>The life cycle of one step within one cycle, and its verdict once resolved.</summary>
public enum StepState
{
    /// <summary>Not started. Drawn dimmed.</summary>
    Pending,

    /// <summary>In flight right now. This is what the arrow and the spinners point at.</summary>
    Running,

    Ok,
    Warning,
    Failed,

    /// <summary>
    /// Not meaningfully testable because something it depends on failed. Distinct from Failed on
    /// purpose: reporting six consequences as six problems is what made v1's raw probe list hard
    /// to read, and it is not true that DNS is broken when the cable is out.
    /// </summary>
    Skipped,
}

/// <summary>The short key of a <see cref="StepId"/>, used in localization keys and logs.</summary>
public static class StepIdExtensions
{
    /// <summary>
    /// The step's localization key fragment. Spelled out rather than derived from the enum name
    /// so that the key set stays stable if a member is ever renamed, and so every step's keys read
    /// the same way: step.&lt;key&gt;.name, step.&lt;key&gt;.ok, step.&lt;key&gt;.failed.
    /// </summary>
    public static string Key(this StepId step) => step switch
    {
        StepId.Adapter => "adapter",
        StepId.Link => "link",
        StepId.IpAddress => "ip",
        StepId.Gateway => "gateway",
        StepId.PublicIp => "publicIp",
        StepId.Dns => "dns",
        StepId.Target => "target",
        _ => step.ToString().ToLowerInvariant(),
    };
}

/// <summary>Whether a <see cref="StepState"/> is final yet, and whether it is a failure.</summary>
public static class StepStateExtensions
{
    public static bool IsResolved(this StepState state) =>
        state is StepState.Ok or StepState.Warning or StepState.Failed or StepState.Skipped;

    /// <summary>A step that did not do what it is supposed to do. Warnings are degraded, not broken.</summary>
    public static bool IsFailure(this StepState state) => state is StepState.Failed;
}
