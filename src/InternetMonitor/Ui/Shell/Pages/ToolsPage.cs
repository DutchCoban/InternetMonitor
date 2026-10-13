using InternetMonitor.App;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Tools.LanScan;
using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// On-demand tools: traceroute and a simplified LAN scanner, switched with a chip row rather than
/// being on separate nav pages - both are "point this at something and watch it run" tools used
/// the same way, and neither is reached for often enough to earn its own place in the rail.
///
/// Each tool runs on its own page rather than in a modal dialog, so it can be left open and
/// compared against the status page while a problem is happening - which is the moment anyone
/// actually wants either of these.
/// </summary>
public sealed class ToolsPage(AppServices services) : PageBase(services)
{
    private const string TracerouteChipId = "traceroute";
    private const string LanScanChipId = "lanScan";

    private ChipGroup _toolSwitcher = null!;

    private Card _tracerouteCard = null!;
    private TracerouteView _view = null!;
    private TextBox _target = null!;
    private AppButton _run = null!;
    private Panel _bar = null!;

    private CancellationTokenSource? _running;

    private Card _lanScanCard = null!;
    private LanDeviceList _lanDevices = null!;
    private AppButton _scan = null!;
    private AppButton _exportCsv = null!;
    private Panel _lanBar = null!;

    private CancellationTokenSource? _scanning;

    public override string Id => "tools";

    protected override void Build()
    {
        BuildToolSwitcher();
        BuildTracerouteCard();
        BuildLanScanCard();

        // Fill before Top, matching DiagnosticsPage: WinForms docks siblings in the order they are
        // added, so the two filled cards have to come first to get what the switcher leaves below
        // it. The two cards occupy the exact same area and ShowTool toggles which one is Visible.
        Controls.Add(_lanScanCard);
        Controls.Add(_tracerouteCard);
        Controls.Add(_toolSwitcher);

        ApplyBarColors();
        LayoutBar();
        LayoutLanBar();
        ShowTool(TracerouteChipId);
    }

    private void BuildToolSwitcher()
    {
        _toolSwitcher = new ChipGroup { Dock = DockStyle.Top };
        _toolSwitcher.SelectionChanged += ShowTool;
    }

    private void ShowTool(string id)
    {
        _tracerouteCard.Visible = id == TracerouteChipId;
        _lanScanCard.Visible = id == LanScanChipId;
    }

    // ---- Traceroute -----------------------------------------------------------------------------

    private void BuildTracerouteCard()
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

        _tracerouteCard = new Card { Dock = DockStyle.Fill, HeaderGlyph = Glyph.Tools };
        _tracerouteCard.Controls.Add(_view);
        _tracerouteCard.Controls.Add(_bar);
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
        _toolSwitcher.Select(TracerouteChipId);
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

        // The user's own subnet, so HopAnnotator can tell "this hop is inside your LAN" apart from
        // "this hop is private address space the ISP happens to use internally" - both are RFC
        // 1918 and otherwise indistinguishable.
        IpAddressFacts? localIp = Services.Pipeline.LatestSnapshot?.FactsFor<IpAddressFacts>(StepId.IpAddress);
        (System.Net.IPAddress Address, System.Net.IPAddress? Mask)? localNetwork =
            localIp?.Address is { } address ? (address, localIp.SubnetMask) : null;

        try
        {
            TracerouteResult result = await runner
                .RunAsync(target, progress, TracerouteRunner.Enricher(ownership), cts.Token, localNetwork)
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

    // ---- LAN scan -------------------------------------------------------------------------------

    private void BuildLanScanCard()
    {
        _lanDevices = new LanDeviceList { Dock = DockStyle.Fill };

        _scan = new AppButton { Variant = ButtonVariant.Primary, Glyph = Glyph.Gateway };
        _scan.Click += (_, _) => StartOrCancelScan();

        _exportCsv = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Copy };
        _exportCsv.Click += (_, _) => ExportCsv();

        _lanBar = new Panel { Dock = DockStyle.Top, Height = Dp(40) };
        _lanBar.Controls.Add(_scan);
        _lanBar.Controls.Add(_exportCsv);
        _lanBar.Resize += (_, _) => LayoutLanBar();

        _lanScanCard = new Card { Dock = DockStyle.Fill, HeaderGlyph = Glyph.Gateway, Visible = false };
        _lanScanCard.Controls.Add(_lanDevices);
        _lanScanCard.Controls.Add(_lanBar);
    }

    private void LayoutLanBar()
    {
        if (_lanBar is null)
        {
            return;
        }

        _scan.Top = (_lanBar.Height - _scan.Height) / 2;
        _scan.Left = 0;
        _exportCsv.Top = (_lanBar.Height - _exportCsv.Height) / 2;
        _exportCsv.Left = _scan.Right + Sp(2);
    }

    private void StartOrCancelScan()
    {
        if (_scanning is not null)
        {
            _scanning.Cancel();
            return;
        }

        _ = RunScanAsync();
    }

    private async Task RunScanAsync()
    {
        IpAddressFacts? local = Services.Pipeline.LatestSnapshot?.FactsFor<IpAddressFacts>(StepId.IpAddress);
        if (local?.Address is not { } address || local.SubnetMask is not { } mask)
        {
            _lanDevices.Status = Loc.Get("lanScan.noLocalAddress");
            return;
        }

        var cts = new CancellationTokenSource();
        _scanning = cts;

        _lanDevices.Clear();
        _lanDevices.Status = Loc.Get("lanScan.scanning");
        _scan.Text = Loc.Get("lanScan.stop");
        _scan.SizeToContent();
        LayoutLanBar();

        var scanner = new LanScanner();
        var progress = new Progress<LanDevice>(device => _lanDevices.Add(device));

        try
        {
            LanScanResult result = await scanner.ScanAsync(address, mask, progress, cts.Token).ConfigureAwait(true);

            _lanDevices.Status = result.Devices.Count == 0
                ? Loc.Get("lanScan.none")
                : result.Truncated
                    ? Loc.Format("lanScan.doneTruncated", result.Devices.Count, result.AddressesScanned)
                    : Loc.Format("lanScan.done", result.Devices.Count, result.AddressesScanned);
        }
        catch (OperationCanceledException)
        {
            _lanDevices.Status = Loc.Get("lanScan.cancelled");
        }
        finally
        {
            _scanning = null;
            cts.Dispose();

            if (!IsDisposed)
            {
                _scan.Text = Loc.Get("lanScan.scan");
                _scan.SizeToContent();
                LayoutLanBar();
            }
        }
    }

    private void ExportCsv()
    {
        if (_lanDevices.Devices.Count == 0)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"netwerkscan-{DateTime.Now:yyyy-MM-dd}.csv",
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, LanScanCsvBuilder.Build(_lanDevices.Devices));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth surfacing further for a manual export the user can just retry.
        }
    }

    // ---- Shared -----------------------------------------------------------------------------------

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_tracerouteCard is null)
        {
            return;
        }

        _toolSwitcher.SetChips(
            [
                new ChipGroup.Chip(TracerouteChipId, Loc.Get("tools.chip.traceroute")),
                new ChipGroup.Chip(LanScanChipId, Loc.Get("tools.chip.lanScan")),
            ],
            _toolSwitcher.SelectedId ?? TracerouteChipId);

        _tracerouteCard.Title = Loc.Get("traceroute.title");
        _tracerouteCard.Subtitle = Loc.Get("traceroute.subtitle");
        _run.Text = Loc.Get(_running is null ? "traceroute.run" : "traceroute.stop");
        _run.SizeToContent();
        LayoutBar();
        _view.Status = Loc.Get("traceroute.idle");

        _lanScanCard.Title = Loc.Get("lanScan.title");
        _lanScanCard.Subtitle = Loc.Get("lanScan.subtitle");
        _scan.Text = Loc.Get(_scanning is null ? "lanScan.scan" : "lanScan.stop");
        _scan.SizeToContent();
        _exportCsv.Text = Loc.Get("lanScan.export");
        _exportCsv.SizeToContent();
        LayoutLanBar();
        _lanDevices.Status = Loc.Get("lanScan.idle");
    }

    protected override void OnThemeChanged() => ApplyBarColors();

    private void ApplyBarColors()
    {
        _bar.BackColor = Theme.Current.Surface;
        _target.BackColor = Theme.Current.SurfaceAlt;
        _target.ForeColor = Theme.Current.TextPrimary;
        _lanBar.BackColor = Theme.Current.Surface;
    }

    protected override void OnDeactivated()
    {
        _running?.Cancel();
        _scanning?.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _running?.Cancel();
            _scanning?.Cancel();
        }

        base.Dispose(disposing);
    }
}
