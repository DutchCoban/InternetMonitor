using InternetMonitor.Ui.Controls;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// Settings, in-place rather than in its own window: checks and endpoints, cadence, appearance,
/// notifications, logging and the traceroute ownership lookup. Filled in by phase 8.
/// </summary>
public sealed class SettingsPage : PageBase
{
    private UnderConstructionCard? _placeholder;

    public override string Id => "settings";

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
