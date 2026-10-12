using InternetMonitor.Ui.Controls;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The technical view: every step's raw measurements, the derived root cause, and the diagnostic
/// report tools. Filled in by phase 7.
/// </summary>
public sealed class DiagnosticsPage : PageBase
{
    private UnderConstructionCard? _placeholder;

    public override string Id => "diagnostics";

    protected override void Build()
    {
        _placeholder = new UnderConstructionCard();
        Controls.Add(_placeholder);
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();
        _placeholder?.ApplyLocalization();
    }
}
