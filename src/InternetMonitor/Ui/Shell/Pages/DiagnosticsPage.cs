using System.Runtime.InteropServices;
using InternetMonitor.App;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Steps;
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
    private StatusPill _bannerPill = null!;
    private Label _bannerHeadline = null!;
    private Label _bannerMeta = null!;

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

        _table = new StepTable { Dock = DockStyle.Fill };
        _table.SelectionChanged += OnTableSelectionChanged;

        _tableCard = new Card { Dock = DockStyle.Left, HeaderGlyph = Glyph.Diagnostics };
        _tableCard.Width = Dp(300);
        _tableCard.Controls.Add(_table);

        _detail = new StepDetailPanel { Dock = DockStyle.Fill };
        _detailCard = new Card { Dock = DockStyle.Fill };
        _detailCard.Controls.Add(_detail);

        _checkNow = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Refresh };
        _checkNow.Click += (_, _) => Services.Cadence.RunNow();
        Header.AddAction(_checkNow);

        _copy = new AppButton { Variant = ButtonVariant.Primary, Glyph = Glyph.Copy };
        _copy.Click += OnCopyClicked;
        Header.AddAction(_copy);

        // Fill before Left, matching IncidentsPage: WinForms docks siblings in the order they are
        // added, so the filled panel has to come first to get what the left panel leaves behind.
        Controls.Add(_detailCard);
        Controls.Add(_tableCard);
        Controls.Add(_bannerCard);
    }

    private void BuildBanner()
    {
        _bannerPill = new StatusPill { ShapeOnly = true };
        _bannerHeadline = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.BottomLeft,
            Font = Theme.Font(FontRole.BodyStrong),
        };
        _bannerMeta = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            Font = Theme.Font(FontRole.Small),
            ForeColor = Theme.Current.TextMuted,
        };

        _bannerCard = new Card { Dock = DockStyle.Top, ShowShadow = false };
        _bannerCard.Height = Dp(56);
        _bannerCard.Controls.Add(_bannerPill);
        _bannerCard.Controls.Add(_bannerHeadline);
        _bannerCard.Controls.Add(_bannerMeta);
        _bannerCard.Resize += (_, _) => LayoutBanner();

        ApplyBannerColors();
    }

    private void LayoutBanner()
    {
        Rectangle area = _bannerCard.DisplayRectangle;
        int pillSize = Dp(22);
        _bannerPill.Bounds = new Rectangle(area.Left, area.Top + ((area.Height - pillSize) / 2), pillSize, pillSize);

        int textLeft = _bannerPill.Right + Sp(2);
        _bannerHeadline.Bounds = new Rectangle(textLeft, area.Top, Math.Max(0, area.Width - (textLeft - area.Left)), area.Height / 2);
        _bannerMeta.Bounds = new Rectangle(textLeft, area.Top + (area.Height / 2), Math.Max(0, area.Width - (textLeft - area.Left)), area.Height / 2);
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

        _checkNow.Text = Loc.Get("menu.checkNow");
        _checkNow.SizeToContent();
        _copy.Text = Loc.Get("diagnostics.copy");
        _copy.SizeToContent();
        Header.RefreshActions();

        RefreshFromSnapshot(Services.Pipeline.LatestSnapshot);
    }

    protected override void OnThemeChanged() => ApplyBannerColors();

    private void ApplyBannerColors()
    {
        _bannerHeadline.BackColor = Theme.Current.Surface;
        _bannerMeta.BackColor = Theme.Current.Surface;
        _bannerMeta.ForeColor = Theme.Current.TextMuted;
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

    private void RefreshFromSnapshot(StepSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            _bannerPill.Severity = UiSeverity.Idle;
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

        _bannerPill.Severity = severity;
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
}
