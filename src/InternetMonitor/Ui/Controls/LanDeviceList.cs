using InternetMonitor.Core.Tools.LanScan;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The LAN scanner's results: four text columns (IP, hostname, MAC, response time), nothing per-row
/// interactive - unlike <see cref="EndpointList"/>, there is no edit or remove action here, so this
/// does not need child controls per row, just text. Self-scrolling like
/// <see cref="TracerouteView"/> rather than relying on the page around it, since a full /24 sweep
/// can return far more rows than fit in the card.
/// </summary>
public sealed class LanDeviceList : ThemedControl
{
    private readonly List<LanDevice> _devices = [];
    private int _scrollOffset;
    private int _contentHeight;
    private string _status = string.Empty;

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
        Invalidate();
    }

    public void Add(LanDevice device)
    {
        _devices.Add(device);
        Invalidate();
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

    /// <summary>Column left edges, as a fraction of the available width - IP, hostname, MAC, then response time (right-aligned, so no left edge of its own).</summary>
    private (int Ip, int Hostname, int Mac, int Right) Columns()
    {
        int left = Sp(2);
        int right = Width - Sp(2);
        int available = Math.Max(0, right - left);

        return (
            left,
            left + (available * 20 / 100),
            left + (available * 55 / 100),
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

        (int ipX, int hostX, int macX, int right) = Columns();
        int msWidth = Dp(60);

        int y = Sp(1) - _scrollOffset;

        if (y + HeaderHeight > 0 && y < Height)
        {
            DrawHeaderCell(g, p, loc.Get("lanScan.column.ip"), ipX, y, hostX - ipX);
            DrawHeaderCell(g, p, loc.Get("lanScan.column.hostname"), hostX, y, macX - hostX);
            DrawHeaderCell(g, p, loc.Get("lanScan.column.mac"), macX, y, right - msWidth - macX);
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
                DrawRow(g, p, device, ipX, hostX, macX, right, msWidth, y);
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

    private void DrawRow(Graphics g, Palette p, LanDevice device, int ipX, int hostX, int macX, int right, int msWidth, int y)
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
            new Rectangle(macX, y, Math.Max(0, right - msWidth - macX), RowHeight),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        g.DrawTextCrisp(
            device.RoundtripMs is { } ms ? $"{ms:F0} ms" : "-",
            Theme.Font(FontRole.Small),
            p.TextMuted,
            new Rectangle(right - msWidth, y, msWidth, RowHeight),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }
}
