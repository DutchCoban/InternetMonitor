using InternetMonitor.Core.Tools.SpeedTest;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The Speed Test tool's result view: a status line plus four stat tiles (ping, jitter, download,
/// upload), in the same label-over-value shape <see cref="StatusHero"/>'s own stats use. Unlike
/// <see cref="TracerouteView"/>/<see cref="LanDeviceList"/> this never grows a list - a speed test
/// has exactly one result at a time - so there is nothing to scroll, just four numbers that fill in
/// live as each phase of the test reports progress.
/// </summary>
public sealed class SpeedTestResultView : ThemedControl
{
    private string _status = string.Empty;
    private double? _pingMs;
    private double? _jitterMs;
    private double? _downloadMbps;
    private double? _uploadMbps;
    private SpeedTestPhase? _activePhase;

    protected override Color BehindColor => Theme.Current.Surface;

    public string Status
    {
        get => _status;
        set
        {
            _status = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>Clears all four stats back to "-" - called before a new run starts.</summary>
    public void Reset()
    {
        _pingMs = null;
        _jitterMs = null;
        _downloadMbps = null;
        _uploadMbps = null;
        _activePhase = null;
        Invalidate();
    }

    /// <summary>Applies one live progress report - the active phase's tile updates continuously as a run proceeds.</summary>
    public void Apply(SpeedTestProgress progress)
    {
        _activePhase = progress.Phase;
        switch (progress.Phase)
        {
            case SpeedTestPhase.Ping:
                _pingMs = progress.Value;
                break;
            case SpeedTestPhase.Jitter:
                _jitterMs = progress.Value;
                break;
            case SpeedTestPhase.Download:
                _downloadMbps = progress.Value;
                break;
            case SpeedTestPhase.Upload:
                _uploadMbps = progress.Value;
                break;
        }

        Invalidate();
    }

    /// <summary>Applies a finished result outright - used when a result arrives without having been watched live (e.g. nothing to do here today, but keeps the view usable standalone).</summary>
    public void Complete(SpeedTestResult result)
    {
        _pingMs = result.PingMs;
        _jitterMs = result.JitterMs;
        _downloadMbps = result.DownloadMbps;
        _uploadMbps = result.UploadMbps;
        _activePhase = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        g.DrawTextCrisp(
            _status.Length > 0 ? _status : loc.Get("speedtest.idle"),
            Theme.Font(FontRole.Body),
            p.TextMuted,
            new Rectangle(Sp(2), Sp(2), Math.Max(0, Width - Sp(4)), Dp(24)),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        int tileWidth = Dp(180);
        int top = Sp(2) + Dp(36);

        DrawStat(g, p, loc.Get("speedtest.stat.ping"), _pingMs is { } ping ? $"{ping:F0} ms" : "-", SpeedTestPhase.Ping, Sp(2), top, tileWidth);
        DrawStat(g, p, loc.Get("speedtest.stat.jitter"), _jitterMs is { } jitter ? $"{jitter:F0} ms" : "-", SpeedTestPhase.Jitter, Sp(2) + tileWidth, top, tileWidth);
        DrawStat(g, p, loc.Get("speedtest.stat.download"), _downloadMbps is { } down ? $"{down:F1} Mbps" : "-", SpeedTestPhase.Download, Sp(2) + (tileWidth * 2), top, tileWidth);
        DrawStat(g, p, loc.Get("speedtest.stat.upload"), _uploadMbps is { } up ? $"{up:F1} Mbps" : "-", SpeedTestPhase.Upload, Sp(2) + (tileWidth * 3), top, tileWidth);
    }

    private void DrawStat(Graphics g, Palette p, string label, string value, SpeedTestPhase phase, int x, int y, int width)
    {
        bool active = _activePhase == phase;
        g.DrawTextCrisp(label, Theme.Font(FontRole.Small), p.TextMuted, new Rectangle(x, y, width, Dp(16)));
        g.DrawTextCrisp(
            value,
            Theme.Font(FontRole.Heading),
            active ? p.Accent : p.TextPrimary,
            new Rectangle(x, y + Dp(16), width, Dp(28)));
    }
}
