using InternetMonitor.App;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Steps;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The full-size chart: pick a group, pick a series in it, pick a time range, then zoom and pan
/// freely inside it.
///
/// v1 put this behind a double-click on a row in a table, in its own window, one window per probe.
/// Here it is a page with a picker, which makes comparing two checks a matter of one click rather
/// than arranging two windows. The picker is two rows rather than one: seventeen series plus one
/// per configured target wrapped over three rows and read as a wall, so the first row names the
/// groups (connection, latency, line quality, traffic, speed test, your targets) and the second
/// only the handful in the chosen group. Each group remembers its last series, and the last series
/// shown is remembered across restarts.
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
    private Label _groupsCaption = null!;
    private ChipGroup _groups = null!;
    private Panel _groupSpacer = null!;
    private Label _stepsCaption = null!;
    private ChipGroup _steps = null!;
    private Panel _chipGroupSpacer = null!;
    private Label _rangesCaption = null!;
    private ChipGroup _ranges = null!;
    /// <summary>What the page shows until the user picks something: the public-internet round trip, the step that says most about the line.</summary>
    private const string DefaultSeriesId = "internet";

    private SeriesGroup _selectedGroup = SeriesGroup.Latency;
    private string? _selectedSeriesId;
    private readonly Dictionary<SeriesGroup, string> _lastInGroup = [];
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

        // A plain gap rather than another control - the chip rows otherwise stack directly on top
        // of each other with nothing but their own captions between them, which still read as one
        // dense block at a glance.
        _chipGroupSpacer = new Panel { Dock = DockStyle.Top, Height = Sp(3) };

        _steps = new ChipGroup { Dock = DockStyle.Top };
        _steps.SelectionChanged += id =>
        {
            _selectedSeriesId = id;
            _lastInGroup[_selectedGroup] = id;
            Services.Settings.HistorySeriesId = id;
            Services.Settings.Save();
            LoadSeries();
        };
        _stepsCaption = MakeCaption("history.caption.series");

        _groupSpacer = new Panel { Dock = DockStyle.Top, Height = Sp(3) };

        // The menu variant: fewer, larger chips, so the two rows read as "which kind" and then
        // "which one" rather than as two rows of the same thing.
        _groups = new ChipGroup { Dock = DockStyle.Top, Variant = ChipGroupVariant.Menu };
        _groups.SelectionChanged += id =>
        {
            _selectedGroup = Enum.Parse<SeriesGroup>(id);
            RefreshSeriesChips();
            LoadSeries();
        };
        _groupsCaption = MakeCaption("history.caption.group");

        _card = new Card { Dock = DockStyle.Fill, HeaderGlyph = Glyph.History };

        // Reverse visual order again: later Top siblings sit nearer the top edge, so this adds
        // (bottom to top) the chart, then the range group (caption above its chips), a spacer,
        // the series group, a spacer, then the group row - ending with its caption nearest the
        // card's own header.
        _card.Controls.Add(_chart);
        _card.Controls.Add(_ranges);
        _card.Controls.Add(_rangesCaption);
        _card.Controls.Add(_chipGroupSpacer);
        _card.Controls.Add(_steps);
        _card.Controls.Add(_stepsCaption);
        _card.Controls.Add(_groupSpacer);
        _card.Controls.Add(_groups);
        _card.Controls.Add(_groupsCaption);
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

        foreach (Label caption in new[] { _groupsCaption, _stepsCaption, _rangesCaption })
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

        foreach (Label caption in new[] { _groupsCaption, _stepsCaption, _rangesCaption })
        {
            caption.Text = Loc.Get((string)caption.Tag!);
        }

        RefreshGroupChips();
        RefreshSeriesChips();
        RefreshRangeChips();
    }

    protected override void OnActivated()
    {
        // Retention and the endpoint list can have changed in Settings since this page was last shown.
        RefreshGroupChips();
        RefreshSeriesChips();
        RefreshRangeChips();
        LoadSeries();
    }

    /// <summary>
    /// The groups in order, the endpoints one only when there is an enabled endpoint to show. The
    /// first time through, the series shown last time decides the group.
    /// </summary>
    private void RefreshGroupChips()
    {
        _selectedSeriesId ??= Services.Settings.HistorySeriesId;
        bool anyEndpoint = Services.Settings.ApplicationEndpoints.Any(e => e.Enabled);
        List<SeriesGroup> visible = [.. Enum.GetValues<SeriesGroup>().Where(g => g != SeriesGroup.Endpoints || anyEndpoint)];

        if (_selectedSeriesId is { } selected && !_lastInGroup.ContainsValue(selected))
        {
            _selectedGroup = SeriesNames.GroupOf(selected);
        }

        if (!visible.Contains(_selectedGroup))
        {
            _selectedGroup = SeriesNames.GroupOf(DefaultSeriesId);
            _selectedSeriesId = DefaultSeriesId;
        }

        _groups.SetChips(visible.Select(g => new ChipGroup.Chip(g.ToString(), Loc.Get(SeriesNames.GroupKey(g)))), _selectedGroup.ToString());
    }

    /// <summary>
    /// The series of the chosen group: the built-in ones listed under it, or one chip per enabled
    /// step-7 endpoint, named as the user named it. When the current series is not in this group
    /// (the group was just switched, an endpoint was removed), the group's last series is shown,
    /// else its first.
    /// </summary>
    private void RefreshSeriesChips()
    {
        List<ChipGroup.Chip> chips = _selectedGroup == SeriesGroup.Endpoints
            ? [.. Services.Settings.ApplicationEndpoints.Where(e => e.Enabled).Select(e => new ChipGroup.Chip(SeriesNames.ForEndpoint(e.Id), e.Name))]
            : [.. SeriesNames.Chartable.Where(s => s.Group == _selectedGroup).Select(s => new ChipGroup.Chip(s.Id, Loc.Get(s.NameKey)))];

        if (!chips.Any(c => c.Id == _selectedSeriesId))
        {
            _selectedSeriesId = _lastInGroup.TryGetValue(_selectedGroup, out string? last) && chips.Any(c => c.Id == last)
                ? last
                : chips.FirstOrDefault()?.Id;
        }

        if (_selectedSeriesId is { } id)
        {
            _lastInGroup[_selectedGroup] = id;
        }

        _steps.SetChips(chips, _selectedSeriesId);
    }

    /// <summary>A configured endpoint's series: round trips in ms with the latency bands, like the built-in network steps.</summary>
    private ChartableSeries? EndpointSeries(string? seriesId)
    {
        if (seriesId is null || SeriesNames.EndpointOf(seriesId) is not { } endpointId)
        {
            return null;
        }

        return Services.Settings.ApplicationEndpoints.Any(e => e.Id == endpointId)
            ? new ChartableSeries(seriesId, string.Empty, "ms", false, LatencyBands: true, Group: SeriesGroup.Endpoints)
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

        // A failed lookup has no latency and so no point on the DNS line; the moments it happened
        // are recorded in a series of their own and marked along the line instead.
        bool dns = series.Id == SeriesNames.For(StepId.Dns);
        _chart.SetSeries(
            Services.History.Get(series.Id),
            series.Unit,
            series.IsState,
            latency ? Services.Targets.LatencyWarningMs : null,
            latency ? Services.Targets.LatencyErrorMs : null,
            series.MetadataChangeKey,
            dns ? [.. Services.History.Get(SeriesNames.DnsFailures).Select(p => p.Timestamp)] : null,
            dns ? "history.dnsFailures.footer" : null);

        _chart.SetRange(_selectedRange);
    }
}
