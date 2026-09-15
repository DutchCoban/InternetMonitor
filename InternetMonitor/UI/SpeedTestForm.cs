using InternetMonitor.Localization;
using InternetMonitor.Network;

namespace InternetMonitor.UI;

/// <summary>
/// Manual "Run" popup for the Speed Test - always reachable from <see cref="DiagnosticsForm"/>.
/// A run from this button and an automatic run (see <see cref="Configuration.AppSettings.SpeedTestAutoRunEnabled"/>)
/// share the same result recording - see <see cref="DiagnosticsCoordinator.RecordSpeedTestResult"/>.
/// </summary>
public sealed class SpeedTestForm : Form
{
    private readonly DiagnosticsCoordinator _coordinator;
    private readonly SpeedTestClient _client = new();
    private readonly Button _runButton;
    private readonly Label _statusLabel;
    private readonly Label _pingValueLabel;
    private readonly Label _jitterValueLabel;
    private readonly Label _downloadValueLabel;
    private readonly Label _uploadValueLabel;
    private readonly Font _valueFont;
    private CancellationTokenSource? _runCts;

    public SpeedTestForm(DiagnosticsCoordinator coordinator)
    {
        _coordinator = coordinator;
        Text = LocalizationManager.Instance.Get("speedTest.title");
        Icon = TrayIconFactory.AppIcon.Value;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(340, 285);

        _valueFont = new Font(Font.FontFamily, 16f, FontStyle.Bold);

        var pingLabel = new Label { Text = LocalizationManager.Instance.Get("speedTest.ping"), Bounds = new Rectangle(16, 16, 150, 20) };
        _pingValueLabel = new Label { Text = "—", Font = _valueFont, Bounds = new Rectangle(16, 36, 150, 30) };
        var jitterLabel = new Label { Text = LocalizationManager.Instance.Get("speedTest.jitter"), Bounds = new Rectangle(174, 16, 150, 20) };
        _jitterValueLabel = new Label { Text = "—", Font = _valueFont, Bounds = new Rectangle(174, 36, 150, 30) };

        var downloadLabel = new Label { Text = LocalizationManager.Instance.Get("speedTest.download"), Bounds = new Rectangle(16, 86, 150, 20) };
        _downloadValueLabel = new Label { Text = "—", Font = _valueFont, Bounds = new Rectangle(16, 106, 150, 30) };
        var uploadLabel = new Label { Text = LocalizationManager.Instance.Get("speedTest.upload"), Bounds = new Rectangle(174, 86, 150, 20) };
        _uploadValueLabel = new Label { Text = "—", Font = _valueFont, Bounds = new Rectangle(174, 106, 150, 30) };

        _statusLabel = new Label { AutoSize = false, ForeColor = Color.DimGray, Bounds = new Rectangle(16, 150, 308, 40) };

        _runButton = new Button { Text = LocalizationManager.Instance.Get("speedTest.run"), Bounds = new Rectangle(16, 200, 150, 30) };
        _runButton.Click += OnRunClicked;
        var closeButton = new Button { Text = LocalizationManager.Instance.Get("settings.close"), Bounds = new Rectangle(234, 200, 90, 30), DialogResult = DialogResult.Cancel };

        var poweredByLink = new LinkLabel { Text = LocalizationManager.Instance.Get("speedTest.poweredBy"), AutoSize = true, Location = new Point(16, 242) };
        poweredByLink.LinkClicked += (_, _) => OpenLibreSpeedProjectPage();

        Controls.Add(pingLabel);
        Controls.Add(_pingValueLabel);
        Controls.Add(jitterLabel);
        Controls.Add(_jitterValueLabel);
        Controls.Add(downloadLabel);
        Controls.Add(_downloadValueLabel);
        Controls.Add(uploadLabel);
        Controls.Add(_uploadValueLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_runButton);
        Controls.Add(closeButton);
        Controls.Add(poweredByLink);
        CancelButton = closeButton;
    }

    private static void OpenLibreSpeedProjectPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://librespeed.org/") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No default browser/handler registered - nothing we can do here.
        }
    }

    private async void OnRunClicked(object? sender, EventArgs e)
    {
        _runButton.Enabled = false;
        _pingValueLabel.Text = "—";
        _jitterValueLabel.Text = "—";
        _downloadValueLabel.Text = "—";
        _uploadValueLabel.Text = "—";
        _statusLabel.Text = LocalizationManager.Instance.Get("speedTest.status.running");

        var progress = new Progress<SpeedTestProgress>(OnProgress);
        _runCts = new CancellationTokenSource();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            SpeedTestResult result = await _client.RunAsync(progress, _runCts.Token).ConfigureAwait(true);
            sw.Stop();
            // Makes this run's numbers visible on the Diagnostics screen (and its own history)
            // too - not just automatic runs - so testing manually shows up immediately rather
            // than only ever appearing after the next scheduled automatic run, if any.
            _coordinator.RecordSpeedTestResult(result, sw.Elapsed);
            if (!IsDisposed)
            {
                _statusLabel.Text = LocalizationManager.Instance.Get("speedTest.status.done");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the form is closed mid-run (see Dispose()) - nothing to show, the
            // window is going away regardless.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            if (!IsDisposed)
            {
                _statusLabel.Text = LocalizationManager.Instance.Format("speedTest.status.error", ex.Message);
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

    private void OnProgress(SpeedTestProgress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        switch (progress.Phase)
        {
            case SpeedTestPhase.Ping:
                _pingValueLabel.Text = LocalizationManager.Instance.Format("speedTest.ms", progress.Value.ToString("F0"));
                _statusLabel.Text = LocalizationManager.Instance.Get("speedTest.status.ping");
                break;
            case SpeedTestPhase.Jitter:
                _jitterValueLabel.Text = LocalizationManager.Instance.Format("speedTest.ms", progress.Value.ToString("F0"));
                break;
            case SpeedTestPhase.Download:
                _downloadValueLabel.Text = LocalizationManager.Instance.Format("speedTest.mbps", progress.Value.ToString("F1"));
                _statusLabel.Text = LocalizationManager.Instance.Get("speedTest.status.download");
                break;
            case SpeedTestPhase.Upload:
                _uploadValueLabel.Text = LocalizationManager.Instance.Format("speedTest.mbps", progress.Value.ToString("F1"));
                _statusLabel.Text = LocalizationManager.Instance.Get("speedTest.status.upload");
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _client.Dispose();
            _valueFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
