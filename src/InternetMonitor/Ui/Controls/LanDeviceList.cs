using System.Drawing.Drawing2D;
using InternetMonitor.Core.Tools.LanScan;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The LAN scanner's results: five text columns (IP, hostname, MAC, vendor, response time), nothing per-row
/// interactive - unlike <see cref="EndpointList"/>, there is no edit or remove action here, so this
/// does not need child controls per row, just text. Self-scrolling like
/// <see cref="TracerouteView"/> rather than relying on the page around it, since a full /24 sweep
/// can return far more rows than fit in the card.
/// </summary>
public sealed class LanDeviceList : ThemedControl
{
    private const int AnimationIntervalMs = 33;

    private readonly List<LanDevice> _devices = [];
    private readonly System.Windows.Forms.Timer _animation;
    private double _phase;
    private int _scrollOffset;
    private int _contentHeight;
    private string _status = string.Empty;

    public LanDeviceList()
    {
        _animation = new System.Windows.Forms.Timer { Interval = AnimationIntervalMs };
        _animation.Tick += OnAnimationTick;
    }

    protected override Color BehindColor => Theme.Current.Surface;

    private int RowHeight => Dp(26);

    private int HeaderHeight => Dp(22);

    public IReadOnlyList<LanDevice> Devices => _devices;

    public string Status
    {
        get => _status;
        set
        {
            _status = value ?? string.Empty;
            Invalidate();
        }
    }

    public void Clear()
    {
        _devices.Clear();
        _scrollOffset = 0;
        UpdateAnimationState();
        Invalidate();
    }

    /// <summary>
    /// Adds a newly-seen device, or - if one with the same <see cref="LanDevice.Address"/> is
    /// already shown - replaces it in place. The scanner reports every device twice when a vendor
    /// lookup is pending: once right away (<see cref="LanDevice.VendorPending"/> true) and again
    /// once the manufacturer is known, and the second report must update that same row rather than
    /// add a duplicate.
    /// </summary>
    public void Upsert(LanDevice device)
    {
        int index = _devices.FindIndex(d => d.Address.Equals(device.Address));
        if (index >= 0)
        {
            _devices[index] = device;
        }
        else
        {
            _devices.Add(device);
        }

        UpdateAnimationState();
        Invalidate();
    }

    private void UpdateAnimationState()
    {
        bool anyPending = _devices.Any(d => d.VendorPending);
        if (anyPending && !_animation.Enabled)
        {
            _animation.Start();
        }
        else if (!anyPending && _animation.Enabled)
        {
            // Nothing is moving, so nothing needs repainting - same reasoning as StepList's own
            // animation gate, an idle window must cost no CPU.
            _animation.Stop();
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        _phase = (_phase + 0.055) % 1.0;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Tick -= OnAnimationTick;
            _animation.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollOffset = Math.Clamp(_scrollOffset - e.Delta, 0, Math.Max(0, _contentHeight - Height));
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Invalidate();
    }

    /// <summary>Column left edges, as a fraction of the available width - IP, hostname, MAC, vendor, then response time (right-aligned, so no left edge of its own).</summary>
    private (int Ip, int Hostname, int Mac, int Vendor, int Right) Columns()
    {
        int left = Sp(2);
        int right = Width - Sp(2);
        int available = Math.Max(0, right - left);

        return (
            left,
            left + (available * 15 / 100),
            left + (available * 45 / 100),
            left + (available * 70 / 100),
            right);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        if (_devices.Count == 0)
        {
            g.DrawTextCrisp(
                _status.Length > 0 ? _status : loc.Get("lanScan.idle"),
                Theme.Font(FontRole.Body),
                p.TextMuted,
                ClientRectangle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        (int ipX, int hostX, int macX, int vendorX, int right) = Columns();
        int msWidth = Dp(60);

        int y = Sp(1) - _scrollOffset;

        if (y + HeaderHeight > 0 && y < Height)
        {
            DrawHeaderCell(g, p, loc.Get("lanScan.column.ip"), ipX, y, hostX - ipX);
            DrawHeaderCell(g, p, loc.Get("lanScan.column.hostname"), hostX, y, macX - hostX);
            DrawHeaderCell(g, p, loc.Get("lanScan.column.mac"), macX, y, vendorX - macX);
            DrawHeaderCell(g, p, loc.Get("lanScan.column.vendor"), vendorX, y, right - msWidth - vendorX);
            DrawHeaderCell(g, p, loc.Get("lanScan.column.responseTime"), right - msWidth, y, msWidth, TextFormatFlags.Right);
        }

        y += HeaderHeight;

        using (var pen = new Pen(p.Border, 1f))
        {
            g.DrawLine(pen, Sp(2), y, right, y);
        }

        y += Sp(1);

        foreach (LanDevice device in _devices)
        {
            if (y + RowHeight > 0 && y < Height)
            {
                DrawRow(g, p, device, ipX, hostX, macX, vendorX, right, msWidth, y);
            }

            y += RowHeight;
        }

        _contentHeight = y + _scrollOffset + Sp(1);
    }

    private void DrawHeaderCell(Graphics g, Palette p, string text, int x, int y, int width, TextFormatFlags extra = TextFormatFlags.Left) =>
        g.DrawTextCrisp(
            text,
            Theme.Font(FontRole.SmallStrong),
            p.TextMuted,
            new Rectangle(x, y, Math.Max(0, width), HeaderHeight),
            extra | TextFormatFlags.VerticalCenter);

    /// <summary>
    /// One device in the scan's columns; a manufacturer still being looked up shows a spinner, and
    /// anything unknown a muted dash.
    /// </summary>
    private void DrawRow(Graphics g, Palette p, LanDevice device, int ipX, int hostX, int macX, int vendorX, int right, int msWidth, int y)
    {
        g.DrawTextCrisp(
            device.Address.ToString(),
            Theme.Font(FontRole.Body),
            p.TextPrimary,
            new Rectangle(ipX, y, Math.Max(0, hostX - ipX), RowHeight),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        g.DrawTextCrisp(
            device.Hostname ?? "-",
            Theme.Font(FontRole.Small),
            device.Hostname is null ? p.TextMuted : p.TextSecondary,
            new Rectangle(hostX, y, Math.Max(0, macX - hostX), RowHeight),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        g.DrawTextCrisp(
            device.MacAddress ?? "-",
            Theme.Font(FontRole.Small),
            device.MacAddress is null ? p.TextMuted : p.TextSecondary,
            new Rectangle(macX, y, Math.Max(0, vendorX - macX), RowHeight),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (device.VendorPending)
        {
            DrawSpinner(g, p, vendorX, y);
        }
        else
        {
            g.DrawTextCrisp(
                device.Vendor ?? "-",
                Theme.Font(FontRole.Small),
                device.Vendor is null ? p.TextMuted : p.TextSecondary,
                new Rectangle(vendorX, y, Math.Max(0, right - msWidth - vendorX), RowHeight),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        g.DrawTextCrisp(
            device.RoundtripMs is { } ms ? $"{ms:F0} ms" : "-",
            Theme.Font(FontRole.Small),
            p.TextMuted,
            new Rectangle(right - msWidth, y, msWidth, RowHeight),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    /// <summary>Same indeterminate track-plus-arc spinner as <see cref="StepList"/>'s, drawn in the vendor cell while a device's manufacturer lookup is still in flight.</summary>
    private void DrawSpinner(Graphics g, Palette p, int x, int y)
    {
        int size = Dp(13);
        var bounds = new RectangleF(x + Dp(1), y + ((RowHeight - size) / 2f), size, size);

        using var track = new Pen(p.Accent.WithAlpha(45), DpF(2f));
        g.DrawEllipse(track, bounds);

        using var arc = new Pen(p.Accent.WithAlpha(190), DpF(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, bounds, (float)(_phase * 360), 90f);
    }
}
