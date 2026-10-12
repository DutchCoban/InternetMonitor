using InternetMonitor.App;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Steps;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The full-size chart: pick a step, pick a time range, then zoom and pan freely inside it.
///
/// v1 put this behind a double-click on a row in a table, in its own window, one window per probe.
/// Here it is a page with a picker, which makes comparing two checks a matter of one click rather
/// than arranging two windows.
/// </summary>
public sealed class HistoryPage(AppServices services) : PageBase(services)
{
    private static readonly (string Id, TimeSpan? Range)[] Ranges =
    [
        ("15m", TimeSpan.FromMinutes(15)),
        ("1h", TimeSpan.FromHours(1)),
        ("6h", TimeSpan.FromHours(6)),
        ("24h", TimeSpan.FromHours(24)),
        ("7d", TimeSpan.FromDays(7)),
        ("all", null),
    ];

    private Card _card = null!;
    private MetricChart _chart = null!;
    private ChipGroup _steps = null!;
    private ChipGroup _ranges = null!;
    private StepId _selectedStep = StepId.PublicIp;
    private TimeSpan? _selectedRange = TimeSpan.FromHours(1);

    public override string Id => "history";

    protected override void Build()
    {
        _chart = new MetricChart { Dock = DockStyle.Fill };

        _ranges = new ChipGroup { Dock = DockStyle.Top };
        _ranges.SelectionChanged += id =>
        {
            _selectedRange = Ranges.FirstOrDefault(r => r.Id == id).Range;
            _chart.SetRange(_selectedRange);
        };

        _steps = new ChipGroup { Dock = DockStyle.Top };
        _steps.SelectionChanged += id =>
        {
            _selectedStep = Enum.GetValues<StepId>().First(s => s.Key() == id);
            LoadSeries();
        };

        _card = new Card { Dock = DockStyle.Fill, HeaderGlyph = Glyph.History };

        // Reverse visual order again: later Top siblings sit nearer the top edge.
        _card.Controls.Add(_chart);
        _card.Controls.Add(_ranges);
        _card.Controls.Add(_steps);
        Controls.Add(_card);
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_card is null)
        {
            return;
        }

        _card.Title = Loc.Get("history.card.title");
        _card.Subtitle = Loc.Get("history.card.subtitle");

        _steps.SetChips(
            SeriesNames.Chartable.Select(s => new ChipGroup.Chip(s.Key(), Loc.Get($"step.{s.Key()}.name"))),
            _selectedStep.Key());

        _ranges.SetChips(
            Ranges.Select(r => new ChipGroup.Chip(r.Id, Loc.Get($"history.range.{r.Id}"))),
            Ranges.First(r => r.Range == _selectedRange).Id);
    }

    protected override void OnActivated() => LoadSeries();

    private void LoadSeries()
    {
        string? series = SeriesNames.For(_selectedStep);
        if (series is null)
        {
            _chart.SetSeries([], string.Empty);
            return;
        }

        bool isState = SeriesNames.IsStateSeries(_selectedStep);
        _chart.SetSeries(
            Services.History.Get(series),
            SeriesNames.UnitFor(_selectedStep),
            isState,
            isState ? null : Services.Targets.LatencyWarningMs,
            isState ? null : Services.Targets.LatencyErrorMs);

        _chart.SetRange(_selectedRange);
    }
}
