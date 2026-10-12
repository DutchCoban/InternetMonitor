using InternetMonitor.App;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// On-demand tools. Traceroute today; the speed test and report export join it in phase 7.
///
/// The traceroute runs on the page rather than in a modal dialog, so it can be left open and
/// compared against the status page while a problem is happening - which is the moment anyone
/// actually wants a traceroute.
/// </summary>
public sealed class ToolsPage(AppServices services) : PageBase(services)
{
    private Card _card = null!;
    private TracerouteView _view = null!;
    private TextBox _target = null!;
    private AppButton _run = null!;
    private Panel _bar = null!;

    private CancellationTokenSource? _running;

    public override string Id => "tools";

    protected override void Build()
    {
        _view = new TracerouteView { Dock = DockStyle.Fill };

        _target = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Width = Dp(240),
            Text = DefaultTarget(),
        };
        _target.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                StartOrCancel();
            }
        };

        _run = new AppButton { Variant = ButtonVariant.Primary, Glyph = Glyph.Diagnostics };
        _run.Click += (_, _) => StartOrCancel();

        _bar = new Panel { Dock = DockStyle.Top, Height = Dp(40) };
        _bar.Controls.Add(_target);
        _bar.Controls.Add(_run);
        _bar.Resize += (_, _) => LayoutBar();

        _card = new Card { Dock = DockStyle.Fill, HeaderGlyph = Glyph.Tools };
        _card.Controls.Add(_view);
        _card.Controls.Add(_bar);
        Controls.Add(_card);

        ApplyBarColors();
        LayoutBar();
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_card is null)
        {
            return;
        }

        _card.Title = Loc.Get("traceroute.title");
        _card.Subtitle = Loc.Get("traceroute.subtitle");
        _run.Text = Loc.Get(_running is null ? "traceroute.run" : "traceroute.stop");
        _run.SizeToContent();
        LayoutBar();
        _view.Status = Loc.Get("traceroute.idle");
    }

    protected override void OnThemeChanged() => ApplyBarColors();

    private void ApplyBarColors()
    {
        _bar.BackColor = Theme.Current.Surface;
        _target.BackColor = Theme.Current.SurfaceAlt;
        _target.ForeColor = Theme.Current.TextPrimary;
    }

    private void LayoutBar()
    {
        if (_bar is null)
        {
            return;
        }

        _target.Top = (_bar.Height - _target.Height) / 2;
        _target.Left = 0;
        _run.Top = (_bar.Height - _run.Height) / 2;
        _run.Left = _target.Right + Sp(2);
    }

    /// <summary>
    /// Pre-fills with the gateway when one is known, since "where does it break after my router"
    /// is the usual question. Falls back to a public address.
    /// </summary>
    private string DefaultTarget()
    {
        GatewayFacts? gateway = Services.Pipeline.LatestSnapshot?.FactsFor<GatewayFacts>(StepId.Gateway);
        return Services.Targets.PublicIpTargets.FirstOrDefault()?.ToString()
            ?? gateway?.Address?.ToString()
            ?? "9.9.9.9";
    }

    /// <summary>Starts a trace against a given target, as if the user had typed it and pressed the button.</summary>
    public void StartTrace(string target)
    {
        _target.Text = target;
        if (_running is null)
        {
            StartOrCancel();
        }
    }

    private void StartOrCancel()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        string target = _target.Text.Trim();
        if (target.Length == 0)
        {
            return;
        }

        _ = RunAsync(target);
    }

    private async Task RunAsync(string target)
    {
        var cts = new CancellationTokenSource();
        _running = cts;

        _view.Clear();
        _view.Status = Loc.Format("traceroute.running", target);
        _run.Text = Loc.Get("traceroute.stop");
        _run.SizeToContent();
        LayoutBar();

        // The ownership lookup is the one thing in this app that tells a third party anything
        // about your route, so it only runs when the setting allows it.
        using IpOwnershipLookup? ownership = Services.Settings.TracerouteOwnerLookup
            ? new IpOwnershipLookup()
            : null;

        var runner = new TracerouteRunner();
        var progress = new Progress<TracerouteHop>(hop => _view.AddHop(hop));

        try
        {
            TracerouteResult result = await runner
                .RunAsync(target, progress, TracerouteRunner.Enricher(ownership), cts.Token)
                .ConfigureAwait(true);

            _view.Status = result.TargetAddress is null
                ? Loc.Format("traceroute.unresolved", target)
                : Loc.Format(
                    result.ReachedDestination ? "traceroute.done" : "traceroute.incomplete",
                    result.Hops.Count,
                    $"{result.Duration.TotalSeconds:F1}");

            _view.RefreshHops();
        }
        catch (OperationCanceledException)
        {
            _view.Status = Loc.Get("traceroute.cancelled");
        }
        finally
        {
            _running = null;
            cts.Dispose();

            if (!IsDisposed)
            {
                _run.Text = Loc.Get("traceroute.run");
                _run.SizeToContent();
                LayoutBar();
            }
        }
    }

    protected override void OnDeactivated() => _running?.Cancel();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running?.Cancel();
        }

        base.Dispose(disposing);
    }
}
