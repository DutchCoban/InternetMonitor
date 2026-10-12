using InternetMonitor.Ui.Controls;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The full-size interactive chart with a step picker and range chips. Filled in by phase 4.
/// </summary>
public sealed class HistoryPage : PageBase
{
    private UnderConstructionCard? _placeholder;

    public override string Id => "history";

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
