using InternetMonitor.App;
using InternetMonitor.Ui.Controls;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// On-demand tools: traceroute with per-hop ownership and gap explanations, speed test, report
/// export, and (in Debug builds) the fault simulator. Filled in by phases 6 and 7.
/// </summary>
public sealed class ToolsPage(AppServices services) : PageBase(services)
{
    private UnderConstructionCard? _placeholder;

    public override string Id => "tools";

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
