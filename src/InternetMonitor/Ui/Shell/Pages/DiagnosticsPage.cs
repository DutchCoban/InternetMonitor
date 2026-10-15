using System.Runtime.InteropServices;
using InternetMonitor.App;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Steps;
using InternetMonitor.Net;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using UiSeverity = InternetMonitor.Ui.Theming.Severity;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The technical screen: every step's raw measurements in one place, for support rather than for
/// a quick glance - that quick glance is what the Status page is for. v1 described this screen's
/// audience exactly right ("a technical screen for support staff") and that description still
/// holds; what changed is the layout, split here into a selectable table and a detail panel
/// instead of v1's ListView-plus-textbox, so it reads like the rest of v2 rather than like a
/// property grid.
///
/// Deliberately does not duplicate the Incidents page's three-paragraph plain-language
/// explanation - this page answers "what did step 4 actually measure", not "what should I do
/// about it". The banner at the top is the shortest possible answer to "is everything fine right
/// now", there to orient a reader before the raw table, not to replace the Status page's hero.
/// </summary>
public sealed class DiagnosticsPage(AppServices services) : PageBase(services)
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current
        ?? throw new InvalidOperationException("DiagnosticsPage must be constructed on the UI thread.");

    private Card _bannerCard = null!;
    private Panel _bannerAccent = null!;
    private SeverityIconChip _bannerChip = null!;
    private Label _bannerHeadline = null!;
    private Label _bannerMeta = null!;

    private Card _networkHealthSummaryCard = null!;
    private Label _networkHealthDisabledLabel = null!;
    private Label _networkHealthUpdatedLabel = null!;
    private readonly List<SummaryRow> _networkHealthRows = [];

    private Card _tableCard = null!;
    private StepTable _table = null!;
    private Card _detailCard = null!;
    private StepDetailPanel _detail = null!;

    private AppButton _checkNow = null!;
    private AppButton _copy = null!;
    private System.Windows.Forms.Timer? _copyResetTimer;

    private bool _subscribed;

    public override string Id => "diagnostics";

    protected override void Build()
    {
        BuildBanner();
        BuildNetworkHealthSummary();

        _table = new StepTable { Dock = DockStyle.Fill };
        _table.SelectionChanged += OnTableSelectionChanged;

        _tableCard = new Card { Dock = DockStyle.Left, HeaderGlyph = Glyph.Steps };
        _tableCard.Width = Dp(300);
        _tableCard.Controls.Add(_table);

        _detail = new StepDetailPanel { Dock = DockStyle.Fill };
        _detailCard = new Card { Dock = DockStyle.Fill, HeaderGlyph = Glyph.Search };
        _detailCard.Controls.Add(_detail);

        _checkNow = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Refresh };
        _checkNow.Click += (_, _) => Services.Cadence.RunNow();
        Header.AddAction(_checkNow);

        _copy = new AppButton { Variant = ButtonVariant.Primary, Glyph = Glyph.Copy };
        _copy.Click += OnCopyClicked;
        Header.AddAction(_copy);

        // Fill before Left, matching IncidentsPage: WinForms docks siblings in the order they are
        // added, so the filled panel has to come first to get what the left panel leaves behind.
        // The summary card is added after both but before the banner, so among the Top-docked
        // controls it lands just below the banner rather than above it.
        Controls.Add(_detailCard);
        Controls.Add(_tableCard);
        Controls.Add(_networkHealthSummaryCard);
        Controls.Add(_bannerCard);
    }

    private void BuildNetworkHealthSummary()
    {
        _networkHealthSummaryCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Pulse };
        _networkHealthSummaryCard.Height = Dp(28);
        _networkHealthSummaryCard.Resize += (_, _) => LayoutNetworkHealthSummary();

        _networkHealthDisabledLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            ForeColor = Theme.Current.TextMuted,
        };
        _networkHealthSummaryCard.Controls.Add(_networkHealthDisabledLabel);

        _networkHealthRows.Add(MakeSummaryRow("tcpRetransmissions"));
        _networkHealthRows.Add(MakeSummaryRow("packetLoss"));
        _networkHealthRows.Add(MakeSummaryRow("ttlExceeded"));
        _networkHealthRows.Add(MakeSummaryRow("rogueDhcp"));

        _networkHealthUpdatedLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            ForeColor = Theme.Current.TextMuted,
        };
        _networkHealthSummaryCard.Controls.Add(_networkHealthUpdatedLabel);

        ApplyNetworkHealthColors();
    }

    private SummaryRow MakeSummaryRow(string key)
    {
        var pill = new StatusPill { ShapeOnly = true };
        var name = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(FontRole.Body) };
        var value = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Font = Theme.Font(FontRole.Small),
            ForeColor = Theme.Current.TextMuted,
        };

        // Second, optional line for a condition that has more to say than a bare count - so far
        // only rogue-DHCP uses it (the detected server addresses), shown only while its incident
        // is open.
        var detail = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            ForeColor = Theme.Current.TextMuted,
            Visible = false,
        };

        _networkHealthSummaryCard.Controls.Add(pill);
        _networkHealthSummaryCard.Controls.Add(name);
        _networkHealthSummaryCard.Controls.Add(value);
        _networkHealthSummaryCard.Controls.Add(detail);

        return new SummaryRow(key, pill, name, value, detail);
    }

    /// <summary>
    /// Shows one dimmed row when the whole feature is off, four condition rows otherwise - same
    /// "one quiet line instead of empty rows" choice the plan made for this card.
    /// </summary>
    private void LayoutNetworkHealthSummary()
    {
        Rectangle content = _networkHealthSummaryCard.DisplayRectangle;
        int rowHeight = Dp(26);
        int detailHeight = Dp(16);
        bool enabled = Services.Settings.NetworkHealthMonitoringEnabled;

        _networkHealthDisabledLabel.Visible = !enabled;
        int y = content.Top;

        if (!enabled)
        {
            _networkHealthDisabledLabel.Bounds = new Rectangle(content.Left, y, content.Width, rowHeight);
            y += rowHeight;
        }

        int pillSize = Dp(14);
        int nameLeft = content.Left + pillSize + Sp(2);

        foreach (SummaryRow row in _networkHealthRows)
        {
            row.Pill.Visible = enabled;
            row.Name.Visible = enabled;
            row.Value.Visible = enabled;
            row.Detail.Visible = enabled && row.Detail.Visible;

            if (!enabled)
            {
                continue;
            }

            row.Pill.Bounds = new Rectangle(content.Left, y + ((rowHeight - pillSize) / 2), pillSize, pillSize);

            int valueWidth = Dp(130);
            row.Name.Bounds = new Rectangle(nameLeft, y, Math.Max(0, content.Width - (nameLeft - content.Left) - valueWidth - Sp(2)), rowHeight);
            row.Value.Bounds = new Rectangle(content.Right - valueWidth, y, valueWidth, rowHeight);

            y += rowHeight;

            if (row.Detail.Visible)
            {
                row.Detail.Bounds = new Rectangle(nameLeft, y, Math.Max(0, content.Width - (nameLeft - content.Left)), detailHeight);
                y += detailHeight;
            }
        }

        _networkHealthUpdatedLabel.Visible = enabled;
        if (enabled)
        {
            _networkHealthUpdatedLabel.Bounds = new Rectangle(content.Left, y, content.Width, detailHeight);
            y += detailHeight;
        }

        _networkHealthSummaryCard.Height = y + _networkHealthSummaryCard.Inset;
    }

    private void RefreshNetworkHealthStatus(NetworkHealthStatus status)
    {
        SetSummaryRow(_networkHealthRows[0], status.TcpRetransmissions);
        SetSummaryRow(_networkHealthRows[1], status.PacketLoss);
        SetSummaryRow(_networkHealthRows[2], status.TtlExceeded);
        SetSummaryRow(_networkHealthRows[3], status.RogueDhcp);

        // The rogue-DHCP probe has its own failure mode distinct from "no incident" - the port
        // bind not working on this system at all (see DhcpDiscoverProbe's own doc comment) - which
        // deserves "not available" rather than a reading of zero that looks like a clean result.
        SummaryRow rogueDhcpRow = _networkHealthRows[3];
        if (rogueDhcpRow.Pill.Visible && status.RogueDhcp.Enabled && status.LastRogueDhcpProbeStatus == DhcpProbeStatus.Unavailable)
        {
            rogueDhcpRow.Value.Text = Loc.Get("diagnostics.networkHealth.unavailable");
            rogueDhcpRow.Pill.Severity = UiSeverity.Idle;
            rogueDhcpRow.Detail.Visible = false;
        }
        else if (status.RogueDhcp.Enabled && status.RogueDhcp.IncidentOpen && status.RogueDhcpServers.Count > 0)
        {
            rogueDhcpRow.Detail.Text = Loc.Format(
                "diagnostics.networkHealth.rogueDhcp.found",
                string.Join(", ", status.RogueDhcpServers));
            rogueDhcpRow.Detail.Visible = true;
        }
        else
        {
            rogueDhcpRow.Detail.Visible = false;
        }

        _networkHealthUpdatedLabel.Text = status.LastUpdatedUtc is { } updated
            ? Loc.Format("diagnostics.networkHealth.lastUpdated", updated.ToLocalTime().ToString("HH:mm:ss"))
            : string.Empty;

        LayoutNetworkHealthSummary();
    }

    private void SetSummaryRow(SummaryRow row, NetworkHealthConditionStatus status)
    {
        row.Name.Text = Loc.Get($"settings.networkHealth.{row.Key}");
        row.Value.Text = Loc.Format("diagnostics.networkHealth.count", status.CountInWindow, status.Threshold);

        row.Pill.Severity = !status.Enabled
            ? UiSeverity.Idle
            : status.IncidentOpen
                ? UiSeverity.Error
                : UiSeverity.Ok;
    }

    private void OnNetworkHealthStatusChanged(NetworkHealthStatus status) => Post(() => RefreshNetworkHealthStatus(status));

    private void BuildBanner()
    {
        _bannerAccent = new Panel();
        _bannerChip = new SeverityIconChip();
        _bannerHeadline = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.BodyStrong),
        };
        _bannerMeta = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            ForeColor = Theme.Current.TextMuted,
        };

        // ShowShadow back on (true, the Card default) and a taller card than the original bare
        // strip - that flat, shadowless 56dp treatment is what read as "not tidy" next to the
        // Status page's hero card; this borrows its shadow, icon chip and accent-bar language
        // without borrowing its stat row, which would be redundant here (the steps table below
        // already is the detail view).
        _bannerCard = new Card { Dock = DockStyle.Top };
        _bannerCard.Height = Dp(88);
        _bannerCard.Controls.Add(_bannerAccent);
        _bannerCard.Controls.Add(_bannerChip);
        _bannerCard.Controls.Add(_bannerHeadline);
        _bannerCard.Controls.Add(_bannerMeta);
        _bannerCard.Resize += (_, _) => LayoutBanner();

        ApplyBannerColors();
    }

    private void LayoutBanner()
    {
        Rectangle area = _bannerCard.DisplayRectangle;

        int accentWidth = Dp(4);
        _bannerAccent.Bounds = new Rectangle(area.Left, area.Top + Dp(4), accentWidth, Math.Max(0, area.Height - Dp(8)));

        int chipSize = Dp(36);
        int chipLeft = area.Left + accentWidth + Sp(3);
        _bannerChip.Bounds = new Rectangle(chipLeft, area.Top + ((area.Height - chipSize) / 2), chipSize, chipSize);

        int textLeft = _bannerChip.Right + Sp(3);
        int textWidth = Math.Max(0, area.Width - (textLeft - area.Left));

        // Fixed, generous row heights for the two text lines, vertically centered as a block -
        // not a blind half-and-half split of whatever height the card happens to have, which is
        // what let the headline and the meta line run into each other at the card's original
        // height.
        int headlineHeight = Dp(24);
        int metaHeight = Dp(18);
        int textGap = Dp(2);
        int textBlockHeight = headlineHeight + textGap + metaHeight;
        int textTop = area.Top + ((area.Height - textBlockHeight) / 2);

        _bannerHeadline.Bounds = new Rectangle(textLeft, textTop, textWidth, headlineHeight);
        _bannerMeta.Bounds = new Rectangle(textLeft, textTop + headlineHeight + textGap, textWidth, metaHeight);
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_tableCard is null)
        {
            return;
        }

        _tableCard.Title = Loc.Get("diagnostics.table.title");
        _tableCard.Subtitle = Loc.Get("diagnostics.table.subtitle");
        _detailCard.Title = Loc.Get("diagnostics.detail.title");

        _networkHealthSummaryCard.Title = Loc.Get("diagnostics.networkHealth.title");
        _networkHealthSummaryCard.Subtitle = Loc.Get("diagnostics.networkHealth.subtitle");
        _networkHealthDisabledLabel.Text = Loc.Get("diagnostics.networkHealth.disabled");
        RefreshNetworkHealthStatus(Services.NetworkHealth.CurrentStatus);

        _checkNow.Text = Loc.Get("menu.checkNow");
        _checkNow.SizeToContent();
        _copy.Text = Loc.Get("diagnostics.copy");
        _copy.SizeToContent();
        Header.RefreshActions();

        RefreshFromSnapshot(Services.Pipeline.LatestSnapshot);
    }

    protected override void OnThemeChanged()
    {
        ApplyBannerColors();
        ApplyNetworkHealthColors();
    }

    private void ApplyBannerColors()
    {
        _bannerHeadline.BackColor = Theme.Current.Surface;
        _bannerMeta.BackColor = Theme.Current.Surface;
        _bannerMeta.ForeColor = Theme.Current.TextMuted;
    }

    private void ApplyNetworkHealthColors()
    {
        Palette p = Theme.Current;
        _networkHealthDisabledLabel.BackColor = p.Surface;
        _networkHealthDisabledLabel.ForeColor = p.TextMuted;
        _networkHealthUpdatedLabel.BackColor = p.Surface;
        _networkHealthUpdatedLabel.ForeColor = p.TextMuted;

        foreach (SummaryRow row in _networkHealthRows)
        {
            row.Name.BackColor = p.Surface;
            row.Name.ForeColor = p.TextSecondary;
            row.Value.BackColor = p.Surface;
            row.Value.ForeColor = p.TextMuted;
            row.Detail.BackColor = p.Surface;
            row.Detail.ForeColor = p.TextMuted;
        }
    }

    protected override void OnActivated()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Services.Pipeline.StepChanged += OnStepChanged;
        Services.Pipeline.CycleCompleted += OnCycleCompleted;
        Services.NetworkHealth.StatusChanged += OnNetworkHealthStatusChanged;
        RefreshNetworkHealthStatus(Services.NetworkHealth.CurrentStatus);

        StepSnapshot? snapshot = Services.Pipeline.LatestSnapshot;
        if (snapshot is not null)
        {
            foreach (StepResult result in snapshot.All)
            {
                _table.Apply(result);
            }
        }

        _detail.Show(_table.Selected, _table.ResultFor(_table.Selected));
        RefreshFromSnapshot(snapshot);
    }

    protected override void OnDeactivated()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        Services.Pipeline.StepChanged -= OnStepChanged;
        Services.Pipeline.CycleCompleted -= OnCycleCompleted;
        Services.NetworkHealth.StatusChanged -= OnNetworkHealthStatusChanged;
    }

    private void OnTableSelectionChanged(StepId step) => _detail.Show(step, _table.ResultFor(step));

    private void OnStepChanged(StepResult result) => Post(() =>
    {
        _table.Apply(result);

        // Only a genuinely resolved result updates the detail panel. Without this, selecting a
        // step and watching it would mean the detail panel blanking to "testing..." on every
        // background refresh of that one step, which is exactly the flicker this whole change
        // exists to remove - the panel now simply keeps showing the last real answer until there
        // is a new one.
        if (result.Step == _table.Selected && result.State.IsResolved())
        {
            _detail.Show(result.Step, result);
        }
    });

    private void OnCycleCompleted(StepSnapshot snapshot) => Post(() => RefreshFromSnapshot(snapshot));

    /// <summary>
    /// Updates the banner from a cycle: the analyser's severity, and the first failure's headline or
    /// else the cause's name. Null means no cycle has finished yet.
    /// </summary>
    private void RefreshFromSnapshot(StepSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _bannerChip.Severity = UiSeverity.Idle;
            _bannerAccent.BackColor = SeverityColors.Strong(UiSeverity.Idle);
            _bannerHeadline.ForeColor = Theme.Current.TextPrimary;
            _bannerHeadline.Text = Loc.Get("hero.checking.title");
            _bannerMeta.Text = string.Empty;
            return;
        }

        RootCauseResult diagnosis = RootCauseAnalyzer.Analyze(snapshot);
        UiSeverity severity = diagnosis.Severity switch
        {
            Core.Diagnosis.Severity.Error => UiSeverity.Error,
            Core.Diagnosis.Severity.Degraded => UiSeverity.Warning,
            _ => UiSeverity.Ok,
        };

        _bannerChip.Severity = severity;
        _bannerAccent.BackColor = SeverityColors.Strong(severity);
        _bannerHeadline.ForeColor = SeverityColors.Strong(severity);
        _bannerHeadline.Text = snapshot.FirstFailure?.Headline ?? Loc.Get($"cause.{diagnosis.Cause.Key()}");
        _bannerMeta.Text = Loc.Format("diagnostics.banner.lastCheck", snapshot.TakenUtc.ToLocalTime().ToString("HH:mm:ss"));
    }

    private void OnCopyClicked(object? sender, EventArgs e)
    {
        StepSnapshot? snapshot = Services.Pipeline.LatestSnapshot;
        if (snapshot is null)
        {
            return;
        }

        string report = DiagnosticReportBuilder.Build(snapshot, RootCauseAnalyzer.Analyze(snapshot));

        try
        {
            Clipboard.SetText(report);
        }
        catch (ExternalException)
        {
            // Another process briefly held the clipboard - not worth surfacing as an error for a
            // one-click convenience action; the user just clicks again.
            return;
        }

        string original = Loc.Get("diagnostics.copy");
        _copy.Text = Loc.Get("diagnostics.copy.done");
        _copy.SizeToContent();
        Header.RefreshActions();

        _copyResetTimer?.Stop();
        _copyResetTimer?.Dispose();
        _copyResetTimer = new System.Windows.Forms.Timer { Interval = 1800 };
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer?.Stop();
            if (!IsDisposed)
            {
                _copy.Text = original;
                _copy.SizeToContent();
                Header.RefreshActions();
            }
        };
        _copyResetTimer.Start();
    }

    /// <summary>Marshals onto the UI thread, dropping the callback if the page has gone away. Every pipeline event arrives on a background thread.</summary>
    private void Post(Action action) => _ui.Post(
        _ =>
        {
            if (!IsDisposed && !Disposing)
            {
                action();
            }
        },
        null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OnDeactivated();
            _copyResetTimer?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>One network-health condition's row in the summary card.</summary>
    private sealed record SummaryRow(string Key, StatusPill Pill, Label Name, Label Value, Label Detail);
}
