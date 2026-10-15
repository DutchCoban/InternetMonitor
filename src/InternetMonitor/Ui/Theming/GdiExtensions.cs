using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace InternetMonitor.Ui.Theming;

/// <summary>
/// The drawing primitives every v2 control is built from: rounded rectangles, soft shadows,
/// gradients, pills and crisp text. Centralising them keeps the look consistent and keeps the
/// controls themselves short - a card, a button and a chart all draw their background through the
/// same two calls.
///
/// Every helper that allocates a GDI+ object disposes it; none of them change the
/// <see cref="Graphics"/> state without restoring it, so callers can mix them freely in one
/// OnPaint.
/// </summary>
internal static class GdiExtensions
{
    /// <summary>
    /// The render-quality setup every control's OnPaint starts with. <see cref="PixelOffsetMode"/>
    /// matters as much as antialiasing here: without HighQuality, antialiased 1px borders land
    /// half on one pixel and half on the next and read as a blurry 2px line.
    /// </summary>
    public static void UseHighQuality(this Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    /// <summary>
    /// A rounded-rectangle path. The radius is clamped to half the shorter side, so passing a
    /// huge radius yields a pill (or a circle on a square) rather than a self-intersecting mess -
    /// that is exactly how the status pills are drawn.
    /// </summary>
    public static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float r = Math.Max(0f, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2f));

        if (r <= 0.01f || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        float d = r * 2f;
        path.AddArc(bounds.Left, bounds.Top, d, d, 180f, 90f);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270f, 90f);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0f, 90f);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(this Graphics g, RectangleF bounds, float radius, Color color)
    {
        if (color.A == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using GraphicsPath path = RoundedPath(bounds, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// Strokes a rounded border *inside* the given bounds. The rectangle is inset by half the pen
    /// width because GDI+ centres a stroke on the path, which would otherwise clip the outer half
    /// of the line against the control edge.
    /// </summary>
    public static void DrawRounded(this Graphics g, RectangleF bounds, float radius, Color color, float width = 1f)
    {
        if (color.A == 0 || bounds.Width <= width || bounds.Height <= width)
        {
            return;
        }

        RectangleF inset = RectangleF.Inflate(bounds, -width / 2f, -width / 2f);
        using GraphicsPath path = RoundedPath(inset, radius - (width / 2f));
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    public static void FillRoundedGradient(this Graphics g, RectangleF bounds, float radius, Color top, Color bottom)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using GraphicsPath path = RoundedPath(bounds, radius);
        // LinearGradientBrush is exclusive of its end edge by a pixel; inflating by 1 avoids a
        // faint hairline of unpainted background along the bottom of every filled shape.
        using var brush = new LinearGradientBrush(
            RectangleF.Inflate(bounds, 1f, 1f), top, bottom, LinearGradientMode.Vertical);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// A soft drop shadow, drawn as a few concentric translucent rounded rectangles below and
    /// behind the shape. A real Gaussian blur would mean an offscreen bitmap per paint; at these
    /// sizes four stacked strokes are indistinguishable and far cheaper.
    /// </summary>
    public static void DrawShadow(this Graphics g, RectangleF bounds, float radius, Color shadow, int depth)
    {
        if (depth <= 0 || shadow.A == 0)
        {
            return;
        }

        for (int i = depth; i >= 1; i--)
        {
            // Alpha falls off quadratically with distance, which is what makes the edge read as a
            // blur rather than as concentric rings.
            int alpha = (int)(shadow.A * (0.42f / (i * i)));
            if (alpha <= 1)
            {
                continue;
            }

            RectangleF ring = RectangleF.Inflate(bounds, i, i);
            ring.Offset(0, i * 0.5f);
            using GraphicsPath path = RoundedPath(ring, radius + i);
            using var pen = new Pen(Color.FromArgb(alpha, shadow), 1.6f);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// Text via GDI (<see cref="TextRenderer"/>) rather than GDI+ <see cref="Graphics.DrawString"/>:
    /// it hints and grid-fits properly, so small UI text stays crisp instead of going soft. GDI
    /// cannot draw rotated or scaled text; nothing in this app needs that.
    /// </summary>
    public static void DrawTextCrisp(
        this Graphics g,
        string text,
        Font font,
        Color color,
        Rectangle bounds,
        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // NoPadding matters for layout: without it GDI adds a few pixels of its own on each side,
        // and measured-then-drawn text no longer lines up with anything measured next to it.
        TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPadding);
    }

    public static Size MeasureCrisp(string text, Font font, int maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Size.Empty;
        }

        return maxWidth > 0
            ? TextRenderer.MeasureText(text, font, new Size(maxWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding)
            : TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
    }

    // ---- Colour helpers ----------------------------------------------------------------------

    public static Color WithAlpha(this Color color, int alpha) =>
        Color.FromArgb(Math.Clamp(alpha, 0, 255), color);

    /// <summary>Blends two colours; <paramref name="t"/> 0 returns <paramref name="from"/>.</summary>
    public static Color Mix(Color from, Color to, float t)
    {
        float k = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(from.A + ((to.A - from.A) * k)),
            (int)(from.R + ((to.R - from.R) * k)),
            (int)(from.G + ((to.G - from.G) * k)),
            (int)(from.B + ((to.B - from.B) * k)));
    }
}
