using System.Reflection;
using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Reports;
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
    private Card _reportCard = null!;
    private ComboBox _period = null!;
    private NumericUpDown _subscribedDown = null!;
    private NumericUpDown _subscribedUp = null!;
    private AppButton _createReport = null!;
    private AppButton _saveReport = null!;
    private readonly List<AppButton> _reportButtons = [];
    private readonly List<(Label Label, Control Field)> _rows = [];

    private Card _exportCard = null!;
    private AppButton _exportAll = null!;
    private AppButton _exportAi = null!;
    private AppButton _exportPlain = null!;
    private readonly List<AppButton> _exportButtons = [];

    public override string Id => "reports";

    protected override void Build()
    {
        _reportCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Report };
        BuildReportSection();

        _exportCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Copy };
        _exportAll = AddButton(_exportCard, _exportButtons, () => _ = ExportAllDataAsync());
        _exportAi = AddButton(_exportCard, _exportButtons, () => _ = ExportMarkdownAsync(forAi: true));
        _exportPlain = AddButton(_exportCard, _exportButtons, () => _ = ExportMarkdownAsync(forAi: false));

        // Reverse visual order: DockStyle.Top stacks later siblings nearer the top edge.
        Controls.Add(_exportCard);
        Controls.Add(_reportCard);
    }

    // ---- Provider report ------------------------------------------------------------------------

    private void BuildReportSection()
    {
        AppSettings settings = Services.Settings;

        _period = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _period.SelectedIndexChanged += (_, _) =>
        {
            if (_period.SelectedItem is PeriodItem item)
            {
                Services.Settings.ReportPeriodDays = item.Days;
                Services.Settings.Save();
            }
        };
        AddRow(_reportCard, "reports.provider.period", _period);

        _subscribedDown = MakeSpeedField(settings.SubscribedDownloadMbps);
        _subscribedDown.ValueChanged += (_, _) =>
        {
            Services.Settings.SubscribedDownloadMbps = (int)_subscribedDown.Value;
            Services.Settings.Save();
        };
        AddRow(_reportCard, "reports.provider.download", _subscribedDown);

        _subscribedUp = MakeSpeedField(settings.SubscribedUploadMbps);
        _subscribedUp.ValueChanged += (_, _) =>
        {
            Services.Settings.SubscribedUploadMbps = (int)_subscribedUp.Value;
            Services.Settings.Save();
        };
        AddRow(_reportCard, "reports.provider.upload", _subscribedUp);

        _createReport = AddButton(_reportCard, _reportButtons, () => _ = CreateReportAsync(open: true));
        _createReport.Variant = ButtonVariant.Primary;
        _createReport.Glyph = Glyph.Report;
        _saveReport = AddButton(_reportCard, _reportButtons, () => _ = CreateReportAsync(open: false));
    }

    private NumericUpDown MakeSpeedField(int value) => new()
    {
        Minimum = 0,
        Maximum = 100_000,
        Value = Math.Clamp(value, 0, 100_000),
        Width = Dp(90),
        TextAlign = HorizontalAlignment.Right,
    };

    private void AddRow(Card card, string labelKey, Control field)
    {
        var label = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(FontRole.Body), Tag = labelKey };
        card.Controls.Add(label);
        card.Controls.Add(field);
        _rows.Add((label, field));
    }

    /// <summary>
    /// Builds the report and either opens it in the browser straight away (from a temporary file)
    /// or asks where to save it. The history has to be fully loaded first - a report made in the
    /// first seconds after start-up would otherwise cover a fraction of the period.
    /// </summary>
    private async Task CreateReportAsync(bool open)
    {
        await Services.History.Loaded.ConfigureAwait(true);

        string html = BuildProviderReport();
        string fileName = $"internetmonitor-report-{DateTime.Now:yyyy-MM-dd}.html";

        if (!open)
        {
            SaveAs("HTML (*.html)|*.html", fileName, path => File.WriteAllText(path, html));
            return;
        }

        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "InternetMonitor");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(path, html).ConfigureAwait(true);
            ShellOpen.OpenPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp folder being unwritable is not worth a dialog; "Save as" still works.
        }
    }

    private static string AppVersion => (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(2, 0, 0)).ToString(3);

    private string BuildProviderReport()
    {
        DateTimeOffset to = DateTimeOffset.UtcNow;
        DateTimeOffset from = to.AddDays(-Services.Settings.ReportPeriodDays);
        ProviderReportData data = ProviderReportData.Compute(Services.Incidents.All, Services.History.Get, from, to, TimeZoneInfo.Local);
        return ProviderReportHtml.Render(
            data,
            new ProviderReportOptions(Services.Settings.SubscribedDownloadMbps, Services.Settings.SubscribedUploadMbps, AppVersion, DateTimeOffset.Now, TimeZoneInfo.Local),
            Loc);
    }

    /// <summary>
    /// A period longer than history keeps is still offered - incidents are never pruned, so the
    /// outage list covers it in full - but its label says how much of the measurements remain.
    /// </summary>
    private void PopulatePeriodItems()
    {
        int retention = Services.Settings.HistoryRetentionDays;
        _period.Items.Clear();
        foreach (int days in AppSettings.AllowedReportPeriodDays)
        {
            string display = days > retention
                ? Loc.Format("reports.provider.periodDays.beyondRetention", days, retention)
                : Loc.Format("reports.provider.periodDays", days);
            _period.Items.Add(new PeriodItem(days, display));
        }

        _period.SelectedItem = _period.Items.Cast<PeriodItem>().FirstOrDefault(i => i.Days == Services.Settings.ReportPeriodDays);
    }

    private sealed record PeriodItem(int Days, string Display)
    {
        public override string ToString() => Display;
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

    /// <summary>
    /// Every export waits for history to finish loading first, for the same reason as the report:
    /// an export made in the first seconds after start-up would otherwise silently miss most of it.
    /// The zip also carries the readable summary and the provider report, so it really is all of it.
    /// </summary>
    private async Task ExportAllDataAsync()
    {
        await Services.History.Loaded.ConfigureAwait(true);

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

        Dictionary<string, IReadOnlyList<MetricPoint>> history = BuildHistorySnapshot();
        var documents = new Dictionary<string, string>
        {
            ["summary.md"] = AiExportBuilder.BuildPlain(Services.Incidents.All, history, Services.Settings, AppVersion),
            ["provider-report.html"] = BuildProviderReport(),
        };

        byte[] zip = DataExportBuilder.Build(Services.Settings, Services.Incidents.All, history, logBytes, documents);
        SaveAs("ZIP (*.zip)|*.zip", $"internetmonitor-export-{DateTime.Now:yyyy-MM-dd}.zip", path => File.WriteAllBytes(path, zip));
    }

    private async Task ExportMarkdownAsync(bool forAi)
    {
        await Services.History.Loaded.ConfigureAwait(true);

        Dictionary<string, IReadOnlyList<MetricPoint>> history = BuildHistorySnapshot();
        string markdown = forAi
            ? AiExportBuilder.Build(Services.Incidents.All, history, Services.Settings, AppVersion)
            : AiExportBuilder.BuildPlain(Services.Incidents.All, history, Services.Settings, AppVersion);
        string fileName = forAi
            ? $"internetmonitor-ai-export-{DateTime.Now:yyyy-MM-dd}.md"
            : $"internetmonitor-data-export-{DateTime.Now:yyyy-MM-dd}.md";
        SaveAs("Markdown (*.md)|*.md", fileName, path => File.WriteAllText(path, markdown));
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
        // Every series on disk, not just the ones the History page offers: v1's per-address pings
        // and endpoint series are still evidence, and an export is the place for all of it.
        var history = new Dictionary<string, IReadOnlyList<MetricPoint>>();
        foreach (string id in Services.History.SeriesIds.Union(SeriesNames.Chartable.Select(s => s.Id)))
        {
            history[id] = Services.History.Get(id);
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
        // The report card: label/field rows first, then its two buttons on a line below them.
        Rectangle content = _reportCard.DisplayRectangle;
        int rowHeight = Dp(36);
        int width = Math.Max(Dp(200), content.Width);
        int y = content.Top;
        foreach ((Label label, Control field) in _rows)
        {
            label.Bounds = new Rectangle(content.Left, y, Math.Max(0, width - field.Width - Sp(3)), rowHeight);
            field.Location = new Point(content.Left + width - field.Width, y + ((rowHeight - field.Height) / 2));
            y += rowHeight;
        }

        int x = content.Left;
        y += Sp(2);
        foreach (AppButton button in _reportButtons)
        {
            button.Location = new Point(x, y);
            x += button.Width + Sp(2);
        }

        _reportCard.Height = y + Dp(32) + _reportCard.Inset;

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

        _reportCard.Title = Loc.Get("reports.provider.title");
        _reportCard.Subtitle = Loc.Get("reports.provider.subtitle");
        foreach ((Label label, _) in _rows)
        {
            label.Text = Loc.Get((string)label.Tag!);
        }

        PopulatePeriodItems();
        _createReport.Text = Loc.Get("reports.provider.create");
        _createReport.SizeToContent();
        _saveReport.Text = Loc.Get("reports.provider.saveAs");
        _saveReport.SizeToContent();
        ApplyControlColors();

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

    protected override void OnActivated()
    {
        // History retention can have changed in Settings since this page was built.
        if (_period is not null)
        {
            PopulatePeriodItems();
        }
    }

    protected override void OnThemeChanged() => ApplyControlColors();

    /// <summary>Stock WinForms controls take no part in the theme on their own; see <see cref="PageBase.OnThemeChanged"/>.</summary>
    private void ApplyControlColors()
    {
        Palette p = Theme.Current;
        foreach ((Label label, Control field) in _rows)
        {
            label.ForeColor = p.TextSecondary;
            label.BackColor = p.Surface;
            field.BackColor = p.SurfaceAlt;
            field.ForeColor = p.TextPrimary;
        }

        _period.FlatStyle = FlatStyle.Flat;
    }
}
