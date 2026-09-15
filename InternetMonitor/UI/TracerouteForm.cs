using System.Net;
using InternetMonitor.Localization;
using InternetMonitor.Network;

namespace InternetMonitor.UI;

/// <summary>
/// On-demand traceroute popup - a live-filling hop list, not a cycle probe. Reachable from
/// <see cref="DiagnosticsForm"/>'s toolbar at any time (unlike Speed Test, this has no meaningful
/// traffic footprint - a handful of small ICMP packets - so it isn't gated behind a Settings toggle).
/// </summary>
public sealed class TracerouteForm : Form
{
    private readonly TextBox _targetTextBox;
    private readonly Button _runButton;
    private readonly Label _statusLabel;
    private readonly ListView _hopsListView;
    private readonly SynchronizationContext _uiContext;
    private CancellationTokenSource? _runCts;

    public TracerouteForm(string? initialTarget)
    {
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("TracerouteForm must be constructed on the UI thread.");

        Text = LocalizationManager.Instance.Get("traceroute.title");
        Icon = TrayIconFactory.AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(440, 420);

        var targetLabel = new Label { Text = LocalizationManager.Instance.Get("traceroute.target"), Bounds = new Rectangle(16, 16, 100, 24) };
        _targetTextBox = new TextBox { Text = initialTarget ?? string.Empty, Bounds = new Rectangle(116, 14, 200, 24) };
        _runButton = new Button { Text = LocalizationManager.Instance.Get("traceroute.run"), Bounds = new Rectangle(324, 12, 100, 28) };
        _runButton.Click += OnRunClicked;

        _statusLabel = new Label { AutoSize = false, ForeColor = Color.DimGray, Bounds = new Rectangle(16, 46, 408, 20) };

        _hopsListView = new ListView { View = View.Details, FullRowSelect = true, Bounds = new Rectangle(16, 72, 408, 296) };
        _hopsListView.Columns.Add(LocalizationManager.Instance.Get("traceroute.column.hop"), 40);
        _hopsListView.Columns.Add(LocalizationManager.Instance.Get("traceroute.column.address"), 120);
        _hopsListView.Columns.Add(LocalizationManager.Instance.Get("traceroute.column.hostname"), 150);
        _hopsListView.Columns.Add(LocalizationManager.Instance.Get("traceroute.column.latency"), 80);

        var closeButton = new Button { Text = LocalizationManager.Instance.Get("settings.close"), Bounds = new Rectangle(334, 380, 90, 28), DialogResult = DialogResult.Cancel };

        Controls.Add(targetLabel);
        Controls.Add(_targetTextBox);
        Controls.Add(_runButton);
        Controls.Add(_statusLabel);
        Controls.Add(_hopsListView);
        Controls.Add(closeButton);
        CancelButton = closeButton;
    }

    private async void OnRunClicked(object? sender, EventArgs e)
    {
        string target = _targetTextBox.Text.Trim();
        if (target.Length == 0)
        {
            _statusLabel.Text = LocalizationManager.Instance.Get("traceroute.error.empty");
            return;
        }

        _runButton.Enabled = false;
        _hopsListView.Items.Clear();
        _statusLabel.Text = LocalizationManager.Instance.Get("traceroute.status.running");

        var progress = new Progress<TracerouteHop>(OnHop);
        _runCts = new CancellationTokenSource();
        try
        {
            await Traceroute.RunAsync(target, progress, _runCts.Token).ConfigureAwait(true);
            if (!IsDisposed)
            {
                _statusLabel.Text = LocalizationManager.Instance.Get("traceroute.status.done");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the form is closed mid-run (see Dispose()).
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.PingException or ArgumentException)
        {
            if (!IsDisposed)
            {
                _statusLabel.Text = LocalizationManager.Instance.Format("traceroute.status.error", ex.Message);
            }
        }
        finally
        {
            _runCts?.Dispose();
            _runCts = null;
            if (!IsDisposed)
            {
                _runButton.Enabled = true;
            }
        }
    }

    private void OnHop(TracerouteHop hop)
    {
        if (IsDisposed)
        {
            return;
        }

        string address = hop.Address?.ToString() ?? "*";
        string latency = hop.LatencyMs is { } ms ? $"{ms:F0} ms" : "-";
        var item = new ListViewItem([hop.Ttl.ToString(), address, string.Empty, latency]);
        if (hop.ReachedDestination)
        {
            item.ForeColor = Color.DarkGreen;
        }
        else if (hop.Address is null)
        {
            item.ForeColor = Color.Gray;
        }

        _hopsListView.Items.Add(item);

        if (hop.Address is { } ip)
        {
            _ = ResolveHostnameAsync(ip, item);
        }
    }

    /// <summary>Best-effort reverse DNS for a hop's address, off the UI thread - never blocks the hop list from filling in, and a failure/timeout just leaves the hostname column blank.</summary>
    private async Task ResolveHostnameAsync(IPAddress address, ListViewItem item)
    {
        string hostname;
        try
        {
            Task<IPHostEntry> lookup = Dns.GetHostEntryAsync(address);
            Task completed = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (completed != lookup)
            {
                return;
            }

            hostname = lookup.Result.HostName;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            if (IsDisposed || item.ListView is null)
            {
                return;
            }

            item.SubItems[2].Text = hostname;
        }, null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
        }

        base.Dispose(disposing);
    }
}
