using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Network;
using InternetMonitor.Network.Diagnosis;
using InternetMonitor.Network.Diagnostics;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.UI;

/// <summary>
/// Compact status view for end users: a computer/cloud/server connectivity diagram for an
/// at-a-glance summary, followed by a fixed, opinionated set of detail rows in diagnostic order
/// (network -&gt; IP -&gt; gateway -&gt; internet -&gt; DNS -&gt; configured application endpoints -&gt; time),
/// each a colored dot + short label - never color alone. Unlike the old version, this form
/// stays open and subscribes to live probe/diagnosis updates for as long as it's shown, and
/// triggers an immediate fresh check on open rather than displaying whatever was last cached.
///
/// Results are revealed in that same fixed order regardless of which probe actually finishes
/// first - the checks themselves still run fully in parallel (for speed), but probes finish in
/// whatever order their network conditions happen to produce, and showing rows flip in that raw,
/// unrelated order reads as "random" rather than as a coherent diagnosis. A completed result for
/// a row further down the list is held back (see <see cref="_pendingResults"/>) until every row
/// above it has been revealed, so the display always progresses top-to-bottom the same way a
/// technician would reason through the problem.
///
/// On a healthy connection most rows reveal within milliseconds of each other, and resizing the
/// window on every single one in that burst reads as a distracting flicker rather than progress.
/// The actual resize/relayout is therefore debounced (<see cref="ScheduleRelayout"/>): it only
/// really runs once reveals have paused for a short moment, so a fast, healthy check settles
/// straight into its final layout in one step instead of visibly resizing several times. An
/// earlier version of this form also showed a "Checking: X" caption naming whatever hadn't been
/// revealed yet, but on a healthy connection that text was gone before it was readable - removed
/// rather than tuned further, since the row colors/text already convey the same information once
/// they settle.
/// </summary>
public sealed class StatusPopupForm : Form
{
    private const int FormWidth = 420;
    private const int HeaderHeight = 112;
    private const int RowTextWidth = FormWidth - 52;

    private readonly DiagnosticsCoordinator _coordinator;
    private readonly IncidentTracker _incidentTracker;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly Dictionary<string, (Panel Dot, Label Label, string DisplayLabel)> _rows = new();
    private readonly List<string> _rowOrder = [];
    private readonly List<string> _gatedRowOrder = [];
    private readonly Dictionary<string, IProbeResult> _pendingResults = new();
    private readonly ConnectivityDiagramControl _diagram;
    private readonly Label _incidentBanner;
    private readonly System.Windows.Forms.Timer _relayoutDebounceTimer;
    private int _revealIndex;
    private int _rowsBottom;

    // Long enough to coalesce a burst of near-simultaneous reveals (a healthy check typically
    // finishes every row within well under this) into a single resize, short enough that a
    // genuinely slow probe's "Checking: X" caption still appears promptly.
    private static readonly int RelayoutDebounceMs = 150;

    public StatusPopupForm(DiagnosticsCoordinator coordinator, IncidentTracker incidentTracker, AppSettings settings)
    {
        _coordinator = coordinator;
        _incidentTracker = incidentTracker;
        _settings = settings;
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("StatusPopupForm must be constructed on the UI thread.");

        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        Text = LocalizationManager.Instance.Get("status.title");
        Icon = TrayIconFactory.AppIcon.Value;

        // Pulse indicator sits above the diagram, clear of it.
        var pulse = new MonitoringPulseControl { Location = new Point(FormWidth - 24, 4), IsActive = true };
        Controls.Add(pulse);

        _diagram = new ConnectivityDiagramControl { Location = new Point((FormWidth - 380) / 2, 24) };
        Controls.Add(_diagram);

        _relayoutDebounceTimer = new System.Windows.Forms.Timer { Interval = RelayoutDebounceMs };
        _relayoutDebounceTimer.Tick += (_, _) =>
        {
            _relayoutDebounceTimer.Stop();
            RelayoutRows();
        };

        AddRow("network", LocalizationManager.Instance.Get("diag.row.network"));
        AddRow("ip", LocalizationManager.Instance.Get("diag.row.ip"));
        AddRow("gateway", LocalizationManager.Instance.Get("diag.row.gateway"));
        AddRow("internet", LocalizationManager.Instance.Get("diag.row.internet"));
        AddRow("dns", LocalizationManager.Instance.Get("diag.row.dns"));
        foreach ((string id, string name) in coordinator.ApplicationEndpointDescriptors)
        {
            AddRow(id, name);
        }
        AddRow("time", LocalizationManager.Instance.Get("diag.row.time"));

        // Time sync now runs on its own slow (hourly) schedule (see DiagnosticsCoordinator),
        // independent of the main cycle - it rarely has a fresh result on any given cycle, so
        // making the ordered reveal wait for its turn would stall every row after it forever.
        // It's excluded from the gated sequence and just updates immediately whenever its
        // (infrequent) result arrives, same as every row did before ordering existed.
        _gatedRowOrder.AddRange(_rowOrder.Where(id => id != "time"));

        // Incident banner lives below all rows and is only present (taking up space) when
        // there's actually an incident to show - no permanently reserved empty area.
        _incidentBanner = new Label
        {
            AutoSize = false,
            ForeColor = Color.White,
            BackColor = Color.Firebrick,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 4, 8, 4),
            Visible = false,
        };
        Controls.Add(_incidentBanner);

        // Seed every row from the last completed cycle before subscribing to live events.
        // Without this, a row whose ProbeCompleted event fires (or fired) outside this form's
        // subscription window - e.g. a fast probe that already finished earlier in an
        // in-flight cycle RunNowAsync() below silently no-ops into, because RunCycleAsync
        // ignores overlapping calls - would be stuck showing the initial "Checking..."
        // placeholder indefinitely, even though a real result already exists. This is a cosmetic
        // pre-fill only (not counted as "revealed") - the fresh cycle kicked off below still
        // reveals its own results in order, overwriting these seeded values row by row.
        if (coordinator.LatestSnapshot is { } snapshot)
        {
            SeedFromSnapshot(snapshot);
        }

        RelayoutRows();
        RefreshDiagram(coordinator.LatestDiagnosis);

        coordinator.CycleStarted += OnCycleStarted;
        coordinator.ProbeCompleted += OnProbeCompleted;
        coordinator.DiagnosisUpdated += OnDiagnosisUpdated;
        FormClosed += (_, _) =>
        {
            coordinator.CycleStarted -= OnCycleStarted;
            coordinator.ProbeCompleted -= OnProbeCompleted;
            coordinator.DiagnosisUpdated -= OnDiagnosisUpdated;
        };

        _ = RunNowSafelyAsync();
    }

    /// <summary>
    /// Fire-and-forget wrapper around RunNowAsync() for the constructor's initial check, which
    /// can't await it directly. Without this, an unexpected exception would be silently
    /// discarded along with the task.
    /// </summary>
    private async Task RunNowSafelyAsync()
    {
        try
        {
            await _coordinator.RunNowAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"RunNowAsync failed: {ex}");
        }
    }

    private void AddRow(string id, string label)
    {
        var dot = new Panel { BackColor = Color.Gray, Size = new Size(12, 12) };
        var text = new Label
        {
            Text = $"{label}: {LocalizationManager.Instance.Get("status.row.checking")}",
            AutoSize = false,
        };
        Controls.Add(dot);
        Controls.Add(text);
        _rows[id] = (dot, text, label);
        _rowOrder.Add(id);
    }

    private void OnCycleStarted(object? sender, EventArgs e)
    {
        _uiContext.Post(_ =>
        {
            if (IsDisposed) return;

            // Deliberately no relayout here. This form stays open across many cycles (a new one
            // starts every 15 seconds for as long as it's shown), so an immediate relayout on
            // every single cycle start would itself be the recurring "hiccup" this whole ordered/
            // debounced reveal scheme exists to avoid. Leaving the previous cycle's settled
            // display untouched here means a fast/healthy cycle - the common case - reveals every
            // row before the debounce timer (see ScheduleRelayout) ever fires, so it transitions
            // straight from one settled state to the next with no visible flash at all.
            _revealIndex = 0;
            _pendingResults.Clear();
        }, null);
    }

    private void OnProbeCompleted(object? sender, ProbeCompletedEventArgs e)
    {
        _uiContext.Post(_ =>
        {
            if (IsDisposed || !_rows.ContainsKey(e.ProbeId)) return;

            // "time" isn't part of the gated sequence (see the comment where _gatedRowOrder is
            // built) - it updates as soon as its own, independently-scheduled result arrives.
            if (e.ProbeId == "time")
            {
                ApplyRowResult(e.ProbeId, e.Result);
                ScheduleRelayout();
                return;
            }

            _pendingResults[e.ProbeId] = e.Result;
            AdvanceReveal();
        }, null);
    }

    /// <summary>Coalesces a burst of reveals into a single relayout - see the class doc comment for why.</summary>
    private void ScheduleRelayout()
    {
        _relayoutDebounceTimer.Stop();
        _relayoutDebounceTimer.Start();
    }

    /// <summary>
    /// Reveals every row whose turn has come and whose result is already buffered, in
    /// <see cref="_gatedRowOrder"/> order - stops at the first row that's either not yet reached
    /// its turn's predecessor or whose result hasn't arrived yet. A result for a later row that
    /// arrived first just waits quietly in <see cref="_pendingResults"/> until its turn comes.
    /// </summary>
    private void AdvanceReveal()
    {
        bool changed = false;
        while (_revealIndex < _gatedRowOrder.Count && _pendingResults.Remove(_gatedRowOrder[_revealIndex], out IProbeResult? result))
        {
            ApplyRowResult(_gatedRowOrder[_revealIndex], result);
            _revealIndex++;
            changed = true;
        }

        if (changed)
        {
            ScheduleRelayout();
        }
    }

    private void ApplyRowResult(string probeId, IProbeResult result)
    {
        if (!_rows.TryGetValue(probeId, out var row))
        {
            return;
        }

        row.Dot.BackColor = result.Status switch
        {
            ProbeStatus.Ok => Color.LimeGreen,
            ProbeStatus.Warning => Color.Orange,
            ProbeStatus.Error or ProbeStatus.Blocked => Color.Red,
            _ => Color.Gray,
        };
        row.Label.Text = $"{row.DisplayLabel}: {result.Summary}";
    }

    /// <summary>
    /// Repositions every row (sized to fit however many lines its current text needs - never
    /// clipped) and the incident banner, then resizes the form to fit. Called after any change
    /// to row content or the reveal position.
    /// </summary>
    private void RelayoutRows()
    {
        int y = HeaderHeight;

        foreach (string id in _rowOrder)
        {
            var (dot, label, _) = _rows[id];
            Size measured = TextRenderer.MeasureText(label.Text, label.Font, new Size(RowTextWidth, int.MaxValue), TextFormatFlags.WordBreak);
            int rowHeight = Math.Max(20, measured.Height);
            dot.Location = new Point(16, y + 4);
            label.Bounds = new Rectangle(36, y, RowTextWidth, rowHeight);
            y += rowHeight + 4;
        }

        _rowsBottom = y + 4;
        RefreshIncidentBanner();
    }

    private void OnDiagnosisUpdated(object? sender, DiagnosisResult e)
    {
        _uiContext.Post(_ =>
        {
            RefreshIncidentBanner();
            RefreshDiagram(e);
        }, null);
    }

    /// <summary>
    /// Maps the single layered root-cause classification from <see cref="DiagnosisEngine"/> onto
    /// the diagram's four nodes (computer, router, internet, server) and three connecting legs,
    /// following the engine's own layering (adapter -&gt; IP -&gt; gateway -&gt; WAN -&gt; DNS -&gt; HTTPS -&gt;
    /// application): everything upstream of the actual fault stays healthy, the fault itself and
    /// everything downstream of it is marked broken. Network/IpConfiguration/Gateway failures are
    /// hardcoded to a full downstream break rather than read from each probe's live status: those
    /// three mean the local machine has no usable path out at all, so nothing downstream can
    /// possibly have succeeded regardless of what any individual probe's result happens to say
    /// (e.g. a self-assigned APIPA address is only ever reported as ProbeStatus.Warning by
    /// IpAddressProbe, which would otherwise under-represent a problem DiagnosisEngine already
    /// decided warrants full escalation). The final leg (cloud-server) is the one place real,
    /// live data is used even here, via <see cref="ResolveCloudServerHealth"/> - a specifically
    /// chosen application endpoint could legitimately be a LAN-only service that keeps working
    /// even with zero internet access, and showing that as broken would be untrue.
    /// </summary>
    private void RefreshDiagram(DiagnosisResult? diagnosis)
    {
        if (IsDisposed)
        {
            return;
        }

        string serverLabel = ResolveServerLabel();

        if (diagnosis is null)
        {
            SegmentHealth unknown = SegmentHealth.Unknown;
            _diagram.SetState(unknown, unknown, unknown, unknown, unknown, unknown, unknown, serverLabel, null, null);
            return;
        }

        SegmentHealth computer = SegmentHealth.Healthy;
        SegmentHealth computerRouter = SegmentHealth.Healthy;
        SegmentHealth router = SegmentHealth.Healthy;
        SegmentHealth routerCloud = SegmentHealth.Healthy;
        SegmentHealth internet = SegmentHealth.Healthy;
        SegmentHealth cloudServer = SegmentHealth.Healthy;
        SegmentHealth server = SegmentHealth.Healthy;

        switch (diagnosis.Classification)
        {
            case DiagnosisClassification.Network:
            case DiagnosisClassification.IpConfiguration:
                // Both mean "this machine has no usable network identity" (no adapter, or a
                // self-assigned APIPA address) - equally total for diagram purposes, since
                // nothing downstream can possibly work either way.
                computer = SegmentHealth.Broken();
                computerRouter = SegmentHealth.Broken();
                router = SegmentHealth.Broken();
                routerCloud = SegmentHealth.Broken();
                internet = SegmentHealth.Broken();
                cloudServer = ResolveCloudServerHealth();
                server = cloudServer;
                break;
            case DiagnosisClassification.Gateway:
                router = SegmentHealth.Broken();
                routerCloud = SegmentHealth.Broken();
                internet = SegmentHealth.Broken();
                cloudServer = ResolveCloudServerHealth();
                server = cloudServer;
                break;
            case DiagnosisClassification.Internet:
                internet = SegmentHealth.Broken();
                cloudServer = ResolveCloudServerHealth();
                server = cloudServer;
                break;
            case DiagnosisClassification.Dns:
                // DNS specifically, not ICMP/"internet" itself: DiagnosisEngine only reaches this
                // classification once the ICMP check has already succeeded, so the internet node
                // and the leg leading to it stay healthy - only the DNS-dependent leg to the
                // server, and the server itself, are affected.
                cloudServer = ResolveCloudServerHealth();
                server = cloudServer;
                break;
            case DiagnosisClassification.FirewallSuspected:
                // HTTPS already confirmed working to reach this classification, so internet/
                // cloud-server/server all stay healthy - only ICMP (the router-cloud leg) is
                // flagged, and only as a warning, not a hard break.
                routerCloud = SegmentHealth.Broken(warningOnly: true);
                break;
            case DiagnosisClassification.Https:
            case DiagnosisClassification.Application:
                // Only the server itself is affected here, not the leg leading to it - DNS
                // resolved fine and the network path is intact; it's specifically the endpoint
                // (or the general HTTPS check) that isn't answering correctly.
                server = ResolveCloudServerHealth();
                break;
            // Healthy, TimeSync (not a connectivity concept), and the never-emitted Unknown all
            // leave every slot at its default Healthy value.
        }

        bool broken = computer.Ok == false || computerRouter.Ok == false || router.Ok == false
            || routerCloud.Ok == false || internet.Ok == false || cloudServer.Ok == false || server.Ok == false;
        _diagram.SetState(computer, computerRouter, router, routerCloud, internet, cloudServer, server, serverLabel,
            broken ? diagnosis.Headline : null, broken ? diagnosis.Explanation : null);
    }

    private static SegmentHealth FromProbeStatus(ProbeStatus status) => status switch
    {
        ProbeStatus.Ok => SegmentHealth.Healthy,
        ProbeStatus.Warning => SegmentHealth.Broken(warningOnly: true),
        ProbeStatus.Unknown or ProbeStatus.Checking => SegmentHealth.Unknown,
        _ => SegmentHealth.Broken(),
    };

    /// <summary>
    /// Whichever thing the diagram's "server" node actually represents - the specifically chosen
    /// application endpoint, or (when none is chosen) the general HTTPS check - reflecting its
    /// real, current status regardless of which classification the overall diagnosis reached.
    /// </summary>
    private SegmentHealth ResolveCloudServerHealth()
    {
        if (_coordinator.LatestSnapshot is not { } snapshot)
        {
            return SegmentHealth.Unknown;
        }

        if (_settings.StatusDiagramEndpointId is { } id)
        {
            var match = snapshot.ApplicationEndpoints.FirstOrDefault(e => e.Result.ProbeId == $"endpoint:{id}");
            return match.Result is null ? SegmentHealth.Unknown : FromProbeStatus(match.Result.Status);
        }

        return FromProbeStatus(snapshot.GeneralHttps.Status);
    }

    private string ResolveServerLabel()
    {
        if (_settings.StatusDiagramEndpointId is { } id)
        {
            EndpointConfig? endpoint = _settings.ApplicationEndpoints.Find(x => x.Id == id);
            if (endpoint is not null)
            {
                return endpoint.Name;
            }
        }

        return LocalizationManager.Instance.Get("status.diagram.server");
    }

    private void SeedFromSnapshot(ProbeSnapshot snapshot)
    {
        ApplyRowResult("network", snapshot.NetworkInterface);
        ApplyRowResult("ip", snapshot.IpAddress);
        ApplyRowResult("gateway", snapshot.Gateway);
        ApplyRowResult("internet", snapshot.Internet);
        ApplyRowResult("dns", snapshot.Dns);
        foreach (var (_, result) in snapshot.ApplicationEndpoints)
        {
            ApplyRowResult(result.ProbeId, result);
        }
        ApplyRowResult("time", snapshot.TimeSync);
    }

    private void RefreshIncidentBanner()
    {
        if (IsDisposed)
        {
            return;
        }

        Incident? incident = _incidentTracker.CurrentIncident;
        if (incident is null)
        {
            _incidentBanner.Visible = false;
            ClientSize = new Size(FormWidth, _rowsBottom);
            ScreenPositioning.PlaceNearTray(this);
            return;
        }

        _incidentBanner.Text = LocalizationManager.Instance.Format(
            "statuspopup.incident.activeSince", incident.Diagnosis, incident.Id, incident.StartUtc.ToLocalTime());
        const int horizontalPadding = 16;
        int textWidth = FormWidth - horizontalPadding;
        Size measured = TextRenderer.MeasureText(_incidentBanner.Text, _incidentBanner.Font, new Size(textWidth, int.MaxValue), TextFormatFlags.WordBreak);
        int bannerHeight = measured.Height + _incidentBanner.Padding.Vertical + 4;
        _incidentBanner.Bounds = new Rectangle(0, _rowsBottom, FormWidth, bannerHeight);
        _incidentBanner.Visible = true;
        ClientSize = new Size(FormWidth, _rowsBottom + bannerHeight);
        ScreenPositioning.PlaceNearTray(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _relayoutDebounceTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
