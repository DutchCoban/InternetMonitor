using InternetMonitor.App;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.History;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// Everything that takes the monitor's collected data *out* of the app: the provider report and
/// the data exports. These used to sit at the bottom of Settings, but they are not settings - they
/// are things you do with what the monitor has recorded - and grouping them here keeps Settings
/// about configuration only. Backing up and restoring the configuration itself stays on Settings.
/// </summary>
public sealed class ReportsPage(AppServices services) : PageBase(services)
{
    private Card _exportCard = null!;
    private AppButton _exportAll = null!;
    private AppButton _exportAi = null!;
    private AppButton _exportPlain = null!;
    private readonly List<AppButton> _exportButtons = [];

    public override string Id => "reports";

    protected override void Build()
    {
        _exportCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Copy };
        _exportAll = AddButton(_exportCard, _exportButtons, ExportAllData);
        _exportAi = AddButton(_exportCard, _exportButtons, ExportForAi);
        _exportPlain = AddButton(_exportCard, _exportButtons, ExportPlainData);

        // Reverse visual order: DockStyle.Top stacks later siblings nearer the top edge.
        Controls.Add(_exportCard);
    }

    private static AppButton AddButton(Card card, List<AppButton> buttons, Action onClick)
    {
        var button = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Copy };
        button.Click += (_, _) => onClick();
        card.Controls.Add(button);
        buttons.Add(button);
        return button;
    }

    // ---- Data exports ---------------------------------------------------------------------------

    private void ExportAllData()
    {
        byte[]? logBytes = null;
        try
        {
            if (File.Exists(Services.DiagnosticLog.LogPath))
            {
                logBytes = File.ReadAllBytes(Services.DiagnosticLog.LogPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Export still proceeds without the log - better than failing the whole export over a
            // file that happened to be locked or unreadable at that moment.
        }

        byte[] zip = DataExportBuilder.Build(Services.Settings, Services.Incidents.All, BuildHistorySnapshot(), logBytes);
        SaveAs("ZIP (*.zip)|*.zip", $"internetmonitor-export-{DateTime.Now:yyyy-MM-dd}.zip", path => File.WriteAllBytes(path, zip));
    }

    private void ExportForAi()
    {
        string markdown = AiExportBuilder.Build(Services.Incidents.All, BuildHistorySnapshot(), Services.Settings);
        SaveAs("Markdown (*.md)|*.md", $"internetmonitor-ai-export-{DateTime.Now:yyyy-MM-dd}.md", path => File.WriteAllText(path, markdown));
    }

    private void ExportPlainData()
    {
        string markdown = AiExportBuilder.BuildPlain(Services.Incidents.All, BuildHistorySnapshot(), Services.Settings);
        SaveAs("Markdown (*.md)|*.md", $"internetmonitor-data-export-{DateTime.Now:yyyy-MM-dd}.md", path => File.WriteAllText(path, markdown));
    }

    private void SaveAs(string filter, string fileName, Action<string> write)
    {
        using var dialog = new SaveFileDialog { Filter = filter, FileName = fileName };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            write(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth surfacing further for a manual export the user can just retry.
        }
    }

    private Dictionary<string, IReadOnlyList<MetricPoint>> BuildHistorySnapshot()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricPoint>>();
        foreach (ChartableSeries series in SeriesNames.Chartable)
        {
            history[series.Id] = Services.History.Get(series.Id);
        }

        return history;
    }

    // ---- Layout ---------------------------------------------------------------------------------

    /// <summary>
    /// Wraps a card's buttons left-to-right onto as many lines as the width needs - the labels vary
    /// a lot in length by language, so a fixed side-by-side row would overflow a narrower window.
    /// </summary>
    private void LayoutButtons(Card card, List<AppButton> buttons)
    {
        Rectangle content = card.DisplayRectangle;
        int rowHeight = Dp(32);
        int x = content.Left;
        int y = content.Top;

        foreach (AppButton button in buttons)
        {
            if (x > content.Left && x + button.Width > content.Right)
            {
                x = content.Left;
                y += rowHeight + Sp(1);
            }

            button.Location = new Point(x, y);
            x += button.Width + Sp(2);
        }

        card.Height = y + rowHeight + card.Inset;
    }

    private void LayoutCards()
    {
        LayoutButtons(_exportCard, _exportButtons);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_exportCard is not null)
        {
            LayoutCards();
        }
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_exportCard is null)
        {
            return;
        }

        _exportCard.Title = Loc.Get("settings.section.export");
        _exportCard.Subtitle = Loc.Get("settings.section.export.subtitle");

        _exportAll.Text = Loc.Get("settings.export.all");
        _exportAll.SizeToContent();
        _exportAi.Text = Loc.Get("settings.export.ai");
        _exportAi.SizeToContent();
        _exportPlain.Text = Loc.Get("settings.export.plain");
        _exportPlain.SizeToContent();

        LayoutCards();
    }
}
