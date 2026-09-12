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
/// </summary>
public sealed class StatusPopupForm : Form
{
    private const int FormWidth = 420;
    private const int HeaderHeight = 112;

    private readonly DiagnosticsCoordinator _coordinator;
    private readonly IncidentTracker _incidentTracker;
    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly Dictionary<string, (Panel Dot, Label Label)> _rows = new();
    private readonly ConnectivityDiagramControl _diagram;
    private readonly Label _incidentBanner;
    private int _rowsBottom;
    private int _y;

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

        // Pulse indicator sits above the diagram, clear of it.
        var pulse = new MonitoringPulseControl { Location = new Point(FormWidth - 24, 4), IsActive = true };
        Controls.Add(pulse);

        _diagram = new ConnectivityDiagramControl { Location = new Point((FormWidth - 380) / 2, 24) };
        Controls.Add(_diagram);

        _y = HeaderHeight;

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

        _rowsBottom = _y + 8;

        // Incident banner lives below all rows and is only present (taking up space) when
        // there's actually an incident to show - no permanently reserved empty area.
        _incidentBanner = new Label
        {
            AutoSize = false,
            ForeColor = Color.White,
            BackColor = Color.Firebrick,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 4, 8, 4),
            Bounds = new Rectangle(0, _rowsBottom, FormWidth, 0),
            Visible = false,
        };
        Controls.Add(_incidentBanner);

        ClientSize = new Size(FormWidth, _rowsBottom);

        RefreshIncidentBanner();

        // Seed every row from the last completed cycle before subscribing to live events.
        // Without this, a row whose ProbeCompleted event fires (or fired) outside this form's
        // subscription window - e.g. a fast probe that already finished earlier in an
        // in-flight cycle RunNowAsync() below silently no-ops into, because RunCycleAsync
        // ignores overlapping calls - would be stuck showing the initial "Checking..."
        // placeholder indefinitely, even though a real result already exists.
        if (coordinator.LatestSnapshot is { } snapshot)
        {
            SeedFromSnapshot(snapshot);
        }

        RefreshDiagram(coordinator.LatestDiagnosis);

        coordinator.ProbeCompleted += OnProbeCompleted;
        coordinator.DiagnosisUpdated += OnDiagnosisUpdated;
        FormClosed += (_, _) =>
        {
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
        var dot = new Panel { BackColor = Color.Gray, Bounds = new Rectangle(16, _y + 4, 12, 12) };
        var text = new Label
        {
            Text = $"{label}: {LocalizationManager.Instance.Get("status.row.checking")}",
            AutoSize = false,
            Bounds = new Rectangle(36, _y, FormWidth - 52, 20),
        };
        Controls.Add(dot);
        Controls.Add(text);
        _rows[id] = (dot, text);
        _y += 24;
    }

    private void OnProbeCompleted(object? sender, ProbeCompletedEventArgs e)
    {
        _uiContext.Post(_ => UpdateRow(e.ProbeId, e.Result), null);
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
    /// the computer/router/cloud/server diagram's five slots - no new diagnosis logic, just a
    /// lookup, following the engine's own layering (adapter -&gt; IP -&gt; gateway -&gt; WAN/DNS -&gt; HTTPS
    /// -&gt; application): everything upstream of the actual fault stays healthy, everything
    /// downstream of it is unknown (not yet meaningfully checked that cycle).
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
            _diagram.SetState(SegmentHealth.Unknown, SegmentHealth.Unknown, SegmentHealth.Unknown, SegmentHealth.Unknown, SegmentHealth.Unknown, serverLabel, null, null);
            return;
        }

        SegmentHealth computer = SegmentHealth.Healthy;
        SegmentHealth computerRouter = SegmentHealth.Healthy;
        SegmentHealth router = SegmentHealth.Healthy;
        SegmentHealth routerCloud = SegmentHealth.Healthy;
        SegmentHealth cloudServer = SegmentHealth.Healthy;

        switch (diagnosis.Classification)
        {
            case DiagnosisClassification.Network:
                computer = SegmentHealth.Broken();
                computerRouter = SegmentHealth.Unknown;
                router = SegmentHealth.Unknown;
                routerCloud = SegmentHealth.Unknown;
                cloudServer = SegmentHealth.Unknown;
                break;
            case DiagnosisClassification.IpConfiguration:
                computerRouter = SegmentHealth.Broken();
                router = SegmentHealth.Unknown;
                routerCloud = SegmentHealth.Unknown;
                cloudServer = SegmentHealth.Unknown;
                break;
            case DiagnosisClassification.Gateway:
                router = SegmentHealth.Broken();
                routerCloud = SegmentHealth.Unknown;
                cloudServer = SegmentHealth.Unknown;
                break;
            case DiagnosisClassification.Internet:
            case DiagnosisClassification.Dns:
                routerCloud = SegmentHealth.Broken();
                cloudServer = SegmentHealth.Unknown;
                break;
            case DiagnosisClassification.FirewallSuspected:
                // HTTPS already confirmed working to reach this classification, so cloud-server
                // stays healthy - only ICMP (the router-cloud leg) is flagged, and only as a
                // warning, not a hard break.
                routerCloud = SegmentHealth.Broken(warningOnly: true);
                break;
            case DiagnosisClassification.Https:
                cloudServer = SegmentHealth.Broken();
                break;
            case DiagnosisClassification.Application:
                // Only break the diagram if the *chosen* server endpoint is among the affected
                // ones - a different (non-chosen) endpoint failing must not mark this broken.
                // AffectedProbeIds carries ApplicationEndpointProbe's "endpoint:{id}" probe id,
                // not the raw EndpointConfig.Id stored in settings - must compare like-for-like.
                if (_settings.StatusDiagramEndpointId is { } id && diagnosis.AffectedProbeIds.Contains($"endpoint:{id}"))
                {
                    cloudServer = SegmentHealth.Broken();
                }
                break;
            // Healthy, TimeSync (not a connectivity concept), and the never-emitted Unknown all
            // leave every slot at its default Healthy value.
        }

        bool broken = computer.Ok == false || computerRouter.Ok == false || router.Ok == false
            || routerCloud.Ok == false || cloudServer.Ok == false;
        _diagram.SetState(computer, computerRouter, router, routerCloud, cloudServer, serverLabel,
            broken ? diagnosis.Headline : null, broken ? diagnosis.Explanation : null);
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
        UpdateRow("network", snapshot.NetworkInterface);
        UpdateRow("ip", snapshot.IpAddress);
        UpdateRow("gateway", snapshot.Gateway);
        UpdateRow("internet", snapshot.Internet);
        UpdateRow("dns", snapshot.Dns);
        foreach (var (_, result) in snapshot.ApplicationEndpoints)
        {
            UpdateRow(result.ProbeId, result);
        }
        UpdateRow("time", snapshot.TimeSync);
    }

    private void UpdateRow(string probeId, IProbeResult result)
    {
        if (IsDisposed || !_rows.TryGetValue(probeId, out var row))
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

        string label = row.Label.Text.Split(':')[0];
        row.Label.Text = $"{label}: {result.Summary}";
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
            if (!_incidentBanner.Visible)
            {
                return;
            }

            _incidentBanner.Visible = false;
            ClientSize = new Size(FormWidth, _rowsBottom);
            ScreenPositioning.PlaceNearTray(this);
            return;
        }

        _incidentBanner.Text = LocalizationManager.Instance.Format(
            "statuspopup.incident.activeSince", incident.Diagnosis, incident.Id, incident.StartUtc.ToLocalTime());
        const int bannerHeight = 44;
        _incidentBanner.Bounds = new Rectangle(0, _rowsBottom, FormWidth, bannerHeight);
        _incidentBanner.Visible = true;
        ClientSize = new Size(FormWidth, _rowsBottom + bannerHeight);
        ScreenPositioning.PlaceNearTray(this);
    }
}
