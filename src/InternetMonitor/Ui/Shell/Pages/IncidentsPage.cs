using InternetMonitor.App;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// Recorded disruptions: the list on the left, the full story of the selected one on the right.
///
/// v1 put incidents in a 180px panel pinned to the bottom of the diagnostics window - which is how
/// it ended up falling off short screens - and opened the detail as a plain-text dump in a modal.
/// Here it is a page, and the detail leads with the explanation rather than the probe output.
/// </summary>
public sealed class IncidentsPage(AppServices services) : PageBase(services)
{
    private Card _listCard = null!;
    private Card _detailCard = null!;
    private IncidentList _list = null!;
    private IncidentDetail _detail = null!;
    private AppButton _export = null!;
    private AppButton _clear = null!;
    private TextBox _search = null!;
    private Panel _searchRow = null!;
    private ChipGroup _sideChips = null!;
    private ChipGroup _periodChips = null!;
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 200 };

    // Kept for the session only: a filter left on from last week would make the list look empty.
    private IncidentFilterCriteria _criteria = new();

    public override string Id => "incidents";

    protected override void Build()
    {
        _list = new IncidentList { Dock = DockStyle.Fill };
        _list.SelectionChanged += incident => _detail.Show(incident);

        _search = new TextBox { BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Top, Font = Theme.Font(FontRole.Body) };
        _search.TextChanged += (_, _) =>
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _criteria = _criteria with { Search = _search.Text };
            RefreshIncidents();
        };

        _searchRow = new Panel { Dock = DockStyle.Top, Height = _search.PreferredHeight + Sp(1) };
        _searchRow.Controls.Add(_search);

        _sideChips = new ChipGroup { Dock = DockStyle.Top };
        _sideChips.SelectionChanged += id =>
        {
            _criteria = _criteria with { Side = Enum.Parse<IncidentSideFilter>(id) };
            RefreshIncidents();
        };

        _periodChips = new ChipGroup { Dock = DockStyle.Top };
        _periodChips.SelectionChanged += id =>
        {
            _criteria = _criteria with { Period = Enum.Parse<IncidentPeriodFilter>(id) };
            RefreshIncidents();
        };

        _listCard = new Card { Dock = DockStyle.Left, HeaderGlyph = Glyph.Incidents };
        _listCard.Width = Dp(330);

        // Reverse visual order: later Top siblings sit nearer the top edge, above the filling list.
        _listCard.Controls.Add(_list);
        _listCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Sp(1) });
        _listCard.Controls.Add(_periodChips);
        _listCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Sp(1) });
        _listCard.Controls.Add(_sideChips);
        _listCard.Controls.Add(_searchRow);
        ApplyFilterColors();

        _detail = new IncidentDetail { Dock = DockStyle.Fill };
        _detail.NoteChanged += Services.Incidents.Update;
        _detailCard = new Card { Dock = DockStyle.Fill };
        _detailCard.Controls.Add(_detail);

        _export = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Copy };
        _export.Click += OnExportClicked;
        Header.AddAction(_export);

        _clear = new AppButton { Variant = ButtonVariant.Danger, Glyph = Glyph.Cross };
        _clear.Click += OnClearClicked;
        Header.AddAction(_clear);

        // Fill before Left, so the filled panel takes what is left rather than what is first.
        Controls.Add(_detailCard);
        Controls.Add(_listCard);
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_listCard is null)
        {
            return;
        }

        _listCard.Title = Loc.Get("incidents.list.title");
        _detailCard.Title = Loc.Get("incidents.detail.title");
        _export.Text = Loc.Get("incidents.export");
        Tip(_export, "incidents.export");
        _export.SizeToContent();
        _clear.Text = Loc.Get("incidents.clear");
        Tip(_clear, "incidents.clear");
        _clear.SizeToContent();
        Header.RefreshActions();

        _search.PlaceholderText = Loc.Get("incidents.filter.search");
        _sideChips.SetChips(
            Enum.GetValues<IncidentSideFilter>().Select(s => new ChipGroup.Chip(s.ToString(), Loc.Get($"incidents.filter.side.{s.ToString().ToLowerInvariant()}"))),
            _criteria.Side.ToString());
        _periodChips.SetChips(
            Enum.GetValues<IncidentPeriodFilter>().Select(p => new ChipGroup.Chip(p.ToString(), Loc.Get($"incidents.filter.period.{p.ToString().ToLowerInvariant()}"))),
            _criteria.Period.ToString());

        RefreshIncidents();
    }

    protected override void OnThemeChanged() => ApplyFilterColors();

    private void ApplyFilterColors()
    {
        _search.BackColor = Theme.Current.SurfaceAlt;
        _search.ForeColor = Theme.Current.TextPrimary;
        _searchRow.BackColor = Theme.Current.Surface;
        foreach (Control spacer in _listCard.Controls.OfType<Panel>().Where(p => p != _searchRow))
        {
            spacer.BackColor = Theme.Current.Surface;
        }
    }

    /// <summary>
    /// Opens one incident, from the Status page's timeline: the filters are cleared first, since
    /// the incident clicked on must be in the list whatever was filtered before.
    /// </summary>
    public void ShowIncident(string id)
    {
        _criteria = new IncidentFilterCriteria();
        _search.Text = string.Empty;
        _searchDebounce.Stop();
        _sideChips.Select(nameof(IncidentSideFilter.All), raiseEvent: false);
        _periodChips.Select(nameof(IncidentPeriodFilter.All), raiseEvent: false);
        RefreshIncidents();
        _list.SelectById(id);
    }

    protected override void OnActivated()
    {
        Services.Incidents.Changed += OnIncidentsChanged;
        RefreshIncidents();
        if (_list.Selected is null)
        {
            _list.SelectFirst();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchDebounce.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnDeactivated() => Services.Incidents.Changed -= OnIncidentsChanged;

    /// <summary>Arrives on whichever thread changed the store; the list is touched on the UI thread only, and only while there is one.</summary>
    private void OnIncidentsChanged()
    {
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(RefreshIncidents);
        }
    }

    private void RefreshIncidents()
    {
        IReadOnlyList<Incident> all = Services.Incidents.All;
        IReadOnlyList<Incident> incidents = IncidentFilter.Apply(all, _criteria, DateTimeOffset.UtcNow);
        _list.EmptyTextKey = _criteria.IsEmpty ? "incidents.empty" : "incidents.filter.none";
        _list.SetIncidents(incidents);
        _listCard.Subtitle = _criteria.IsEmpty
            ? Loc.Format("incidents.count", all.Count)
            : Loc.Format("incidents.filter.count", incidents.Count, all.Count);

        if (_detail is not null && _list.Selected is { } selected)
        {
            _detail.Show(selected);
        }
    }

    private void OnExportClicked(object? sender, EventArgs e)
    {
        if (_list.Selected is not { } incident)
        {
            return;
        }

        string report = IncidentReportBuilder.Build(incident);

        using var dialog = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"incident-{incident.Id}.txt",
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth surfacing further for a manual export the user can just retry.
        }
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        // Destructive and not undoable, so it asks - the one place in this app that does.
        DialogResult result = MessageBox.Show(
            Loc.Get("incidents.clear.confirm"),
            Loc.Get("incidents.clear"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result == DialogResult.Yes)
        {
            Services.Incidents.Clear();
            _detail.Show(null);
            RefreshIncidents();
        }
    }
}
