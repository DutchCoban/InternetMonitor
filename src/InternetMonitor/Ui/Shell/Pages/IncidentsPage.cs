using InternetMonitor.Ui.Controls;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// Recorded incidents with their timeline, evidence and plain-language explanation. Filled in by
/// phase 5.
/// </summary>
public sealed class IncidentsPage : PageBase
{
    private UnderConstructionCard? _placeholder;

    public override string Id => "incidents";

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
