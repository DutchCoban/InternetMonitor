using InternetMonitor.App;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Steps;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The page the app opens on: the verdict, then the seven steps running live underneath it.
///
/// It subscribes to the pipeline only while it is the visible page. The pipeline fires on
/// background threads several times a second in foreground cadence, and a page nobody is looking
/// at has no business marshalling all of that onto the UI thread.
/// </summary>
public sealed class StatusPage(AppServices services) : PageBase(services)
{
    private readonly SynchronizationContext _ui = SynchronizationContext.Current
        ?? throw new InvalidOperationException("StatusPage must be constructed on the UI thread.");

    private StatusHero _hero = null!;
    private Card _stepsCard = null!;
    private StepList _steps = null!;
    private Card _latencyCard = null!;
    private MetricChart _latencyChart = null!;
    private AppButton _checkNow = null!;
    private bool _subscribed;

    public override string Id => "status";

    /// <summary>
    /// Raised whenever the steps card's height changes - a row expanding, or a language switch
    /// making a wrapped detail line taller. <see cref="MainWindow"/> listens for this to grow the
    /// window if it no longer fits the content, which a step row growing after the window was
    /// already sized would otherwise leave as an avoidable scrollbar.
    /// </summary>
    public event Action? ContentHeightChanged;

    protected override void Build()
    {
        _steps = new StepList { Dock = DockStyle.Fill };
        _steps.HeightChanged += ResizeStepsCard;

        _stepsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Steps };
        _stepsCard.Controls.Add(_steps);

        _latencyChart = new MetricChart { Dock = DockStyle.Fill, Compact = true };
        _latencyCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Globe };
        _latencyCard.Height = Dp(150);
        _latencyCard.Controls.Add(_latencyChart);

        _hero = new StatusHero { Dock = DockStyle.Top };
        _hero.Height = _hero.PreferredHeight;

        _checkNow = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Refresh };
        _checkNow.Click += (_, _) => Services.Cadence.RunNow();
        Header.AddAction(_checkNow);

        // Added in reverse visual order: DockStyle.Top stacks later siblings nearer the top edge,
        // and PageBase adds its header after this runs.
        Controls.Add(_latencyCard);
        Controls.Add(_stepsCard);
        Controls.Add(_hero);

        ResizeStepsCard();
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_checkNow is null)
        {
            return;
        }

        _checkNow.Text = Loc.Get("menu.checkNow");
        _checkNow.SizeToContent();
        _stepsCard.Title = Loc.Get("status.steps.title");
        _stepsCard.Subtitle = Loc.Get("status.steps.subtitle");
        _latencyCard.Title = Loc.Get("status.latency.title");
        _latencyCard.Subtitle = Loc.Get("status.latency.subtitle");
        ResizeStepsCard();
        Header.RefreshActions();

        // Step names live inside the list's own paint, so a language change just needs a repaint.
        _steps.Invalidate();
        _hero.Apply(Services.Pipeline.LatestSnapshot, checking: false);
        RefreshNetworkHealth(Services.NetworkHealth.CurrentStatus);
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
        Services.Cadence.Foreground = true;

        // Paint whatever the last cycle concluded straight away, so switching to this page never
        // shows an empty list while waiting for the next cycle.
        if (Services.Pipeline.LatestSnapshot is { } snapshot)
        {
            foreach (StepResult result in snapshot.All)
            {
                _steps.Apply(result);
            }

            _hero.Apply(snapshot, checking: false);
        }

        RefreshNetworkHealth(Services.NetworkHealth.CurrentStatus);
        RefreshLatencyChart();
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

    private void OnStepChanged(StepResult result) => Post(() =>
    {
        _steps.Apply(result);

        if (result.State == StepState.Running && result.Step == StepId.Adapter)
        {
            // A new cycle has started. Say so on the hero rather than leaving the previous
            // verdict looking like a fresh one.
            _hero.Apply(Services.Pipeline.LatestSnapshot, checking: true);
        }
    });

    private void OnCycleCompleted(StepSnapshot snapshot) => Post(() =>
    {
        _hero.Apply(snapshot, checking: false);
        RefreshLatencyChart();
    });

    private void OnNetworkHealthStatusChanged(NetworkHealthStatus status) => Post(() => RefreshNetworkHealth(status));

    /// <summary>
    /// The brief version of the Diagnostics page's network-health summary: just whether anything
    /// is currently open, not which condition or by how much - that detail belongs on Diagnostics,
    /// not on the page whose whole point is a glance-and-go verdict.
    /// </summary>
    private void RefreshNetworkHealth(NetworkHealthStatus status)
    {
        int openCount = new[] { status.TcpRetransmissions, status.PacketLoss, status.TtlExceeded, status.RogueDhcp }
            .Count(c => c.Enabled && c.IncidentOpen);

        _hero.SetNetworkHealth(status.MonitoringEnabled, openCount);
    }

    /// <summary>
    /// The inline strip shows the last hour of public-internet latency - the number the hero
    /// quotes, so the card underneath is the history of the number above it rather than a
    /// different measurement that happens to be nearby.
    /// </summary>
    private void RefreshLatencyChart()
    {
        if (SeriesNames.For(StepId.PublicIp) is not { } series)
        {
            return;
        }

        _latencyChart.SetSeries(
            Services.History.Get(series, DateTimeOffset.UtcNow - TimeSpan.FromHours(1)),
            SeriesNames.UnitFor(StepId.PublicIp),
            stateSeries: false,
            Services.Targets.LatencyWarningMs,
            Services.Targets.LatencyErrorMs);

        _latencyChart.ResetView();
    }

    /// <summary>
    /// Marshals onto the UI thread, dropping the callback if the page has gone away in the
    /// meantime. Every pipeline event arrives on a background thread.
    /// </summary>
    private void Post(Action action) => _ui.Post(
        _ =>
        {
            if (!IsDisposed && !Disposing)
            {
                action();
            }
        },
        null);

    /// <summary>
    /// The total height this page's content actually needs. Used once, by the main window, to size
    /// a fresh window to fit without a scrollbar when there is no saved window size yet.
    /// Summing the already-resolved heights of the top-docked children rather than a hardcoded
    /// number: this stays correct on its own if a card is ever added or a row grows, with nothing
    /// here to go stale.
    /// </summary>
    internal int MeasureRequiredHeight() =>
        Header.Height + _hero.Height + _stepsCard.Height + _latencyCard.Height + Padding.Top + Padding.Bottom;

    private void ResizeStepsCard()
    {
        if (_stepsCard is null)
        {
            return;
        }

        // The card sizes to its content rather than the content scrolling inside a fixed card:
        // seven rows is a known quantity, and an inner scrollbar on a short list looks broken.
        int previousHeight = _stepsCard.Height;
        _stepsCard.Height = _steps.PreferredHeight + (_stepsCard.Height - _stepsCard.DisplayRectangle.Height);

        if (_stepsCard.Height != previousHeight)
        {
            ContentHeightChanged?.Invoke();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            OnDeactivated();
        }

        base.Dispose(disposing);
    }
}
