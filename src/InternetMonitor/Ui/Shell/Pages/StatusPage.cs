using InternetMonitor.Ui.Controls;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The page the app opens on: one hero card saying whether the connection works, the seven-step
/// pipeline with the focus arrow, and an inline latency chart. Filled in by phases 3 and 4.
/// </summary>
public sealed class StatusPage : PageBase
{
    private UnderConstructionCard? _placeholder;

    public override string Id => "status";

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
