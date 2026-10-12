using InternetMonitor.Core.Tools.Traceroute;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The route, drawn as a vertical path rather than a table.
///
/// Each hop gets three lines when it has something to say: the address and timings, who owns it,
/// and - the reason this control exists - a plain-language note explaining the hop. v1 rendered a
/// four-column list where a silent hop was an asterisk and nothing else, which is accurate and
/// almost useless: the asterisk is the thing people most often misread as "the connection breaks
/// here".
///
/// Silent hops are drawn with a dashed connector and a hollow marker, so the eye reads them as a
/// gap in knowledge rather than a gap in the route.
/// </summary>
public sealed class TracerouteView : ThemedControl
{
    private readonly List<TracerouteHop> _hops = [];
    private int _scrollOffset;
    private int _contentHeight;
    private string _status = string.Empty;

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

    public void Clear()
    {
        _hops.Clear();
        _scrollOffset = 0;
        Invalidate();
    }

    public void AddHop(TracerouteHop hop)
    {
        _hops.Add(hop);
        Invalidate();
    }

    /// <summary>Repaints after enrichment has filled in hostnames, owners and annotations.</summary>
    public void RefreshHops() => Invalidate();

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollOffset = Math.Clamp(_scrollOffset - e.Delta, 0, Math.Max(0, _contentHeight - Height));
        Invalidate();
    }

    /// <summary>
    /// The width available to an annotation. Needed in two places - measuring the hop's height and
    /// drawing the text - and they must agree, or the row is sized for one number of lines and
    /// painted with another.
    /// </summary>
    private int AnnotationWidth => Math.Max(Dp(120), Width - Sp(4) - Dp(22) - Dp(12) - Sp(1) - Sp(3));

    private int HopHeight(TracerouteHop hop)
    {
        int height = Dp(26);
        if (hop.Owner is { HasAnything: true } || hop.Hostname is { Length: > 0 })
        {
            height += Dp(16);
        }

        if (hop.Annotation is { Length: > 0 } annotation)
        {
            // Annotations are whole sentences and the useful ones are the longest - "this router
            // does not answer probes, the route is fine" is the entire point of the feature, so it
            // wraps rather than being cut off with an ellipsis.
            height += GdiExtensions.MeasureCrisp(annotation, Theme.Font(FontRole.Small), AnnotationWidth).Height + Dp(2);
        }

        return height + Dp(6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        if (_hops.Count == 0)
        {
            g.DrawTextCrisp(
                _status.Length > 0 ? _status : loc.Get("traceroute.idle"),
                Theme.Font(FontRole.Body),
                p.TextMuted,
                ClientRectangle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int markerX = Sp(4);
        int textX = markerX + Dp(22);
        int right = Width - Sp(3);
        int y = Sp(2) - _scrollOffset;

        for (int i = 0; i < _hops.Count; i++)
        {
            TracerouteHop hop = _hops[i];
            int height = HopHeight(hop);

            if (y + height > 0 && y < Height)
            {
                DrawHop(g, p, loc, hop, i, markerX, textX, right, y, height);
            }

            y += height;
        }

        _contentHeight = y + _scrollOffset + Sp(2);
    }

    private void DrawHop(
        Graphics g,
        Palette p,
        LocalizationManager loc,
        TracerouteHop hop,
        int index,
        int markerX,
        int textX,
        int right,
        int y,
        int height)
    {
        bool last = index == _hops.Count - 1;
        Color accent = hop.ReachedDestination ? p.Ok : hop.Silent ? p.Idle : p.Accent;

        if (!last)
        {
            using var pen = new Pen(hop.Silent ? p.Border : accent.WithAlpha(110), DpF(2f));
            if (hop.Silent)
            {
                // A dashed connector past a hop that said nothing: the route continues, we just
                // have no information about this stretch of it.
                pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
            }

            g.DrawLine(pen, markerX + DpF(5f), y + Dp(18), markerX + DpF(5f), y + height + Dp(4));
        }

        var marker = new RectangleF(markerX, y + Dp(7), Dp(11), Dp(11));
        if (hop.Silent)
        {
            using var pen = new Pen(p.Idle, DpF(1.6f));
            g.DrawEllipse(pen, marker);
        }
        else
        {
            using var brush = new SolidBrush(accent);
            g.FillEllipse(brush, marker);
        }

        // Hop number, then address, then timings, right-aligned.
        g.DrawTextCrisp(
            hop.Ttl.ToString(),
            Theme.Font(FontRole.Small),
            p.TextMuted,
            new Rectangle(0, y + Dp(4), markerX - Dp(4), Dp(16)),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        string address = hop.Silent
            ? loc.Get("traceroute.noReply")
            : string.Join(", ", hop.Responders.Select(r => r.ToString()));

        string timing = hop.Silent
            ? string.Empty
            : hop.LossFraction > 0
                ? loc.Format("traceroute.timingLoss", $"{hop.MinMs:F0}", $"{hop.AvgMs:F0}", $"{hop.MaxMs:F0}", (int)Math.Round(hop.LossFraction * 100))
                : loc.Format("traceroute.timing", $"{hop.MinMs:F0}", $"{hop.AvgMs:F0}", $"{hop.MaxMs:F0}");

        Size timingSize = GdiExtensions.MeasureCrisp(timing, Theme.Font(FontRole.Small));
        if (timing.Length > 0)
        {
            g.DrawTextCrisp(
                timing,
                Theme.Font(FontRole.Small),
                p.TextMuted,
                new Rectangle(right - timingSize.Width, y + Dp(4), timingSize.Width, Dp(16)));
        }

        g.DrawTextCrisp(
            address,
            hop.Silent ? Theme.Font(FontRole.Body) : Theme.Font(FontRole.BodyStrong),
            hop.Silent ? p.TextMuted : p.TextPrimary,
            new Rectangle(textX, y + Dp(4), Math.Max(0, right - textX - timingSize.Width - Sp(2)), Dp(16)));

        int line = y + Dp(22);

        if (hop.Owner is { HasAnything: true } owner || hop.Hostname is { Length: > 0 })
        {
            string detail = hop.Owner is { HasAnything: true } o
                ? hop.Hostname is { Length: > 0 } host ? $"{o.Describe()}   ·   {host}" : o.Describe()
                : hop.Hostname!;

            g.DrawTextCrisp(
                detail,
                Theme.Font(FontRole.Small),
                p.TextSecondary,
                new Rectangle(textX, line, Math.Max(0, right - textX), Dp(15)));
            line += Dp(16);
        }

        if (hop.Annotation is { Length: > 0 } annotation)
        {
            int glyph = Dp(12);
            Icons.Draw(g, Glyph.Warning, new RectangleF(textX, line + Dp(1), glyph, glyph), p.Accent, 2.2f);

            int width = AnnotationWidth;
            g.DrawTextCrisp(
                annotation,
                Theme.Font(FontRole.Small),
                p.Accent,
                new Rectangle(
                    textX + glyph + Sp(1),
                    line,
                    width,
                    GdiExtensions.MeasureCrisp(annotation, Theme.Font(FontRole.Small), width).Height),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }
}
