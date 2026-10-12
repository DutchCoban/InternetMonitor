using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// Scaffolding: a card that says a screen has not been built yet.
///
/// v2 is assembled in phases, each of which has to produce a running application, which means
/// pages exist in the nav rail before they have content. Showing that plainly beats showing an
/// empty page that looks broken. Every use of this card is removed by the phase that builds the
/// page for real - when the last one is gone, so is this file.
/// </summary>
public sealed class UnderConstructionCard : Card
{
    public UnderConstructionCard()
    {
        Height = Theme.Dp(96, DeviceDpi);
        Dock = DockStyle.Top;
        HeaderGlyph = Glyph.Tools;
        ApplyLocalization();
    }

    public void ApplyLocalization() => Title = LocalizationManager.Instance.Get("page.underConstruction");
}
