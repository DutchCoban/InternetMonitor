namespace InternetMonitor.Ui.Theming;

/// <summary>
/// The presentation-level severity of anything shown in the UI. Deliberately separate from the
/// core's StepState: the core distinguishes Pending from Running from Skipped because the pipeline
/// cares, while the UI only needs to know which colour and which shape to use. Mapping happens
/// once, at the boundary.
/// </summary>
public enum Severity
{
    Idle,
    Ok,
    Warning,
    Error,
}

public static class SeverityColors
{
    /// <summary>The strong colour: text, icon strokes, chart series.</summary>
    public static Color Strong(Severity severity) => severity switch
    {
        Severity.Ok => Theme.Current.Ok,
        Severity.Warning => Theme.Current.Warn,
        Severity.Error => Theme.Current.Error,
        _ => Theme.Current.Idle,
    };

    /// <summary>The tinted background behind a pill or a highlighted row.</summary>
    public static Color Soft(Severity severity) => severity switch
    {
        Severity.Ok => Theme.Current.OkSoft,
        Severity.Warning => Theme.Current.WarnSoft,
        Severity.Error => Theme.Current.ErrorSoft,
        _ => Theme.Current.IdleSoft,
    };

    /// <summary>
    /// The shape that carries the same meaning as the colour. Status is never communicated by
    /// colour alone anywhere in this app - a rule inherited from v1 (StatusPopupForm) and the
    /// reason every severity has a glyph as well as a palette entry.
    /// </summary>
    public static Glyph Shape(Severity severity) => severity switch
    {
        Severity.Ok => Glyph.Check,
        Severity.Warning => Glyph.Warning,
        Severity.Error => Glyph.Cross,
        _ => Glyph.Dot,
    };
}
