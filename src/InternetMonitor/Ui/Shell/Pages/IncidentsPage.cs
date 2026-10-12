using InternetMonitor.App;
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
    private AppButton _clear = null!;

    public override string Id => "incidents";

    protected override void Build()
    {
        _list = new IncidentList { Dock = DockStyle.Fill };
        _list.SelectionChanged += incident => _detail.Show(incident);

        _listCard = new Card { Dock = DockStyle.Left, HeaderGlyph = Glyph.Incidents };
        _listCard.Width = Dp(330);
        _listCard.Controls.Add(_list);

        _detail = new IncidentDetail { Dock = DockStyle.Fill };
        _detailCard = new Card { Dock = DockStyle.Fill };
        _detailCard.Controls.Add(_detail);

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
        _clear.Text = Loc.Get("incidents.clear");
        _clear.SizeToContent();
        Header.RefreshActions();
        RefreshIncidents();
    }

    protected override void OnActivated()
    {
        Services.Incidents.Changed += OnIncidentsChanged;
        RefreshIncidents();
        _list.SelectFirst();
    }

    protected override void OnDeactivated() => Services.Incidents.Changed -= OnIncidentsChanged;

    private void OnIncidentsChanged() => BeginInvoke(RefreshIncidents);

    private void RefreshIncidents()
    {
        IReadOnlyList<Incident> incidents = Services.Incidents.All;
        _list.SetIncidents(incidents);
        _listCard.Subtitle = Loc.Format("incidents.count", incidents.Count);

        if (_detail is not null && _list.Selected is { } selected)
        {
            _detail.Show(selected);
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
