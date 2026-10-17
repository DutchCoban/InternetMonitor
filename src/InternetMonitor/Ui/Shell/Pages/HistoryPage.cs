using InternetMonitor.App;
using InternetMonitor.Core.History;
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
        ("30d", TimeSpan.FromDays(30)),
        ("all", null),
    ];

    private Card _card = null!;
    private MetricChart _chart = null!;
    private Label _stepsCaption = null!;
    private ChipGroup _steps = null!;
    private Panel _chipGroupSpacer = null!;
    private Label _rangesCaption = null!;
    private ChipGroup _ranges = null!;
    private string _selectedSeriesId = "internet";
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
        _rangesCaption = MakeCaption("history.caption.range");

        // A plain gap rather than another control - the two chip rows otherwise stack directly
        // on top of each other with nothing but their own captions between them, which still read
        // as one dense block at a glance.
        _chipGroupSpacer = new Panel { Dock = DockStyle.Top, Height = Sp(3) };

        _steps = new ChipGroup { Dock = DockStyle.Top };
        _steps.SelectionChanged += id =>
        {
            _selectedSeriesId = id;
            LoadSeries();
        };
        _stepsCaption = MakeCaption("history.caption.series");

        _card = new Card { Dock = DockStyle.Fill, HeaderGlyph = Glyph.History };

        // Reverse visual order again: later Top siblings sit nearer the top edge, so this adds
        // (bottom to top) the chart, then the range group (caption above its chips), a spacer,
        // then the series group - ending with its caption nearest the card's own header.
        _card.Controls.Add(_chart);
        _card.Controls.Add(_ranges);
        _card.Controls.Add(_rangesCaption);
        _card.Controls.Add(_chipGroupSpacer);
        _card.Controls.Add(_steps);
        _card.Controls.Add(_stepsCaption);
        Controls.Add(_card);
    }

    private Label MakeCaption(string key) => new()
    {
        AutoSize = false,
        Dock = DockStyle.Top,
        Height = Dp(18),
        TextAlign = ContentAlignment.BottomLeft,
        Font = Theme.Font(FontRole.SmallStrong),
        ForeColor = Theme.Current.TextMuted,
        BackColor = Theme.Current.Surface,
        Tag = key,
    };

    protected override void OnThemeChanged()
    {
        if (_stepsCaption is null)
        {
            return;
        }

        foreach (Label caption in new[] { _stepsCaption, _rangesCaption })
        {
            caption.ForeColor = Theme.Current.TextMuted;
            caption.BackColor = Theme.Current.Surface;
        }
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

        _stepsCaption.Text = Loc.Get((string)_stepsCaption.Tag!);
        _rangesCaption.Text = Loc.Get((string)_rangesCaption.Tag!);

        RefreshSeriesChips();
        RefreshRangeChips();
    }

    protected override void OnActivated()
    {
        // Retention and the endpoint list can have changed in Settings since this page was last shown.
        RefreshSeriesChips();
        RefreshRangeChips();
        LoadSeries();
    }

    /// <summary>The built-in series, then one chip per enabled step-7 endpoint, named as the user named it.</summary>
    private void RefreshSeriesChips()
    {
        IEnumerable<ChipGroup.Chip> builtIn = SeriesNames.Chartable.Select(s => new ChipGroup.Chip(s.Id, Loc.Get(s.NameKey)));
        IEnumerable<ChipGroup.Chip> endpoints = Services.Settings.ApplicationEndpoints
            .Where(e => e.Enabled)
            .Select(e => new ChipGroup.Chip(SeriesNames.ForEndpoint(e.Id), e.Name));

        _steps.SetChips(builtIn.Concat(endpoints), _selectedSeriesId);
    }

    /// <summary>A configured endpoint's series: round trips in ms with the latency bands, like the built-in network steps.</summary>
    private ChartableSeries? EndpointSeries(string? seriesId)
    {
        if (seriesId is null || SeriesNames.EndpointOf(seriesId) is not { } endpointId)
        {
            return null;
        }

        return Services.Settings.ApplicationEndpoints.Any(e => e.Id == endpointId)
            ? new ChartableSeries(seriesId, string.Empty, "ms", false, LatencyBands: true)
            : null;
    }

    /// <summary>
    /// Only the ranges shorter than what history keeps: at a 30-day retention "30 days" would just
    /// be "All" under another name, and at 7 days "7 days" would be.
    /// </summary>
    private void RefreshRangeChips()
    {
        TimeSpan retention = TimeSpan.FromDays(Services.Settings.HistoryRetentionDays);
        (string Id, TimeSpan? Range)[] visible = [.. Ranges.Where(r => r.Range is null || r.Range < retention)];

        if (!visible.Any(r => r.Range == _selectedRange))
        {
            _selectedRange = null;
            _chart.SetRange(null);
        }

        _ranges.SetChips(
            visible.Select(r => new ChipGroup.Chip(r.Id, Loc.Get($"history.range.{r.Id}"))),
            visible.First(r => r.Range == _selectedRange).Id);
    }

    private void LoadSeries()
    {
        ChartableSeries? series = SeriesNames.Chartable.FirstOrDefault(s => s.Id == _selectedSeriesId) ?? EndpointSeries(_selectedSeriesId);
        if (series is null)
        {
            _chart.SetSeries([], string.Empty);
            return;
        }

        // The good/warning/bad bands are latency thresholds, so they only belong under a round
        // trip - not under a speed, a signal, a loss percentage or a jitter.
        bool latency = series.LatencyBands;
        _chart.SetSeries(
            Services.History.Get(series.Id),
            series.Unit,
            series.IsState,
            latency ? Services.Targets.LatencyWarningMs : null,
            latency ? Services.Targets.LatencyErrorMs : null,
            series.MetadataChangeKey);

        _chart.SetRange(_selectedRange);
    }
}
