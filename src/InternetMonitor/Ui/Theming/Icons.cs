using System.Drawing.Drawing2D;

namespace InternetMonitor.Ui.Theming;

public enum Glyph
{
    Status,
    Diagnostics,
    History,
    Incidents,
    Tools,
    Settings,
    Check,
    Warning,
    Cross,
    Dot,
    ChevronRight,
    ChevronDown,
    Arrow,
    Refresh,
    Adapter,
    Cable,
    Ip,
    Gateway,
    Globe,
    Dns,
    Target,
}

/// <summary>
/// Every icon in the app, drawn as geometry rather than loaded as a bitmap.
///
/// Three reasons this is worth the arithmetic: icons stay razor-sharp at 125%, 150% and 200%
/// scaling where a 16px PNG turns to mush; they take the theme's colour for free, so there is no
/// second set of assets for dark mode; and nothing extra has to be embedded in the single-file
/// executable. v1 shipped exactly one bitmap (app.ico) and drew everything else - this keeps that
/// property.
///
/// All geometry is authored on a 24x24 grid and scaled to the requested rectangle, so a glyph can
/// be asked for at any size and still line up with text next to it.
/// </summary>
internal static class Icons
{
    private const float Grid = 24f;

    /// <summary>
    /// Draws <paramref name="glyph"/> centred in <paramref name="bounds"/>. Stroke width is given
    /// in grid units and scales with the icon, so a 16px and a 32px icon look like the same icon
    /// rather than like two different weights.
    /// </summary>
    public static void Draw(Graphics g, Glyph glyph, RectangleF bounds, Color color, float strokeUnits = 1.8f)
    {
        float scale = Math.Min(bounds.Width, bounds.Height) / Grid;
        if (scale <= 0f)
        {
            return;
        }

        GraphicsState state = g.Save();
        try
        {
            g.TranslateTransform(
                bounds.X + ((bounds.Width - (Grid * scale)) / 2f),
                bounds.Y + ((bounds.Height - (Grid * scale)) / 2f));
            g.ScaleTransform(scale, scale);

            using var pen = new Pen(color, strokeUnits)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            using var brush = new SolidBrush(color);
            Paint(g, glyph, pen, brush);
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static void Paint(Graphics g, Glyph glyph, Pen pen, Brush brush)
    {
        switch (glyph)
        {
            case Glyph.Status:
                // A gauge: three-quarter arc with a needle.
                g.DrawArc(pen, 3f, 4f, 18f, 18f, 150f, 240f);
                g.DrawLine(pen, 12f, 13f, 17f, 8.5f);
                g.FillEllipse(brush, 10.6f, 11.6f, 2.8f, 2.8f);
                break;

            case Glyph.Diagnostics:
                // A lightning bolt - the "run the checks now" sense.
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(new[]
                    {
                        new PointF(13.5f, 2f), new PointF(5f, 13.5f), new PointF(11f, 13.5f),
                        new PointF(10f, 22f), new PointF(19f, 10f), new PointF(13f, 10f),
                    });
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }

                break;

            case Glyph.History:
                // Clock face with a counter-clockwise arrow head: "back in time".
                g.DrawArc(pen, 3f, 3f, 18f, 18f, 110f, 300f);
                g.DrawLine(pen, 12f, 7.5f, 12f, 12f);
                g.DrawLine(pen, 12f, 12f, 15.5f, 14f);
                g.DrawLines(pen, new[] { new PointF(3f, 3.5f), new PointF(3.2f, 8.4f), new PointF(8.1f, 8f) });
                break;

            case Glyph.Incidents:
                g.DrawPolygon(pen, new[] { new PointF(12f, 3.2f), new PointF(22f, 20.5f), new PointF(2f, 20.5f) });
                g.DrawLine(pen, 12f, 9.5f, 12f, 14.5f);
                g.FillEllipse(brush, 11f, 16.6f, 2f, 2f);
                break;

            case Glyph.Tools:
                // A wrench at 45 degrees: open jaw plus shaft.
                g.DrawArc(pen, 2.5f, 2.5f, 9.5f, 9.5f, 40f, 265f);
                g.DrawLine(pen, 10.2f, 10.2f, 20f, 20f);
                g.DrawLine(pen, 17.4f, 19f, 19f, 17.4f);
                break;

            case Glyph.Settings:
                DrawGear(g, pen);
                break;

            case Glyph.Check:
                g.DrawLines(pen, new[] { new PointF(4.5f, 12.5f), new PointF(9.5f, 17.5f), new PointF(19.5f, 6.5f) });
                break;

            case Glyph.Warning:
                g.DrawLine(pen, 12f, 4f, 12f, 14f);
                g.FillEllipse(brush, 10.8f, 17f, 2.4f, 2.4f);
                break;

            case Glyph.Cross:
                g.DrawLine(pen, 6f, 6f, 18f, 18f);
                g.DrawLine(pen, 18f, 6f, 6f, 18f);
                break;

            case Glyph.Dot:
                g.FillEllipse(brush, 9f, 9f, 6f, 6f);
                break;

            case Glyph.ChevronRight:
                g.DrawLines(pen, new[] { new PointF(9.5f, 5f), new PointF(16.5f, 12f), new PointF(9.5f, 19f) });
                break;

            case Glyph.ChevronDown:
                g.DrawLines(pen, new[] { new PointF(5f, 9.5f), new PointF(12f, 16.5f), new PointF(19f, 9.5f) });
                break;

            case Glyph.Arrow:
                // The focus marker: a solid triangle, so it reads at 10px and in any colour.
                g.FillPolygon(brush, new[] { new PointF(8f, 5f), new PointF(18f, 12f), new PointF(8f, 19f) });
                break;

            case Glyph.Refresh:
                g.DrawArc(pen, 3.5f, 3.5f, 17f, 17f, 70f, 280f);
                g.FillPolygon(brush, new[] { new PointF(18.2f, 1.5f), new PointF(22.5f, 7.5f), new PointF(15.4f, 7.2f) });
                break;

            case Glyph.Adapter:
                // A board with a chip on it: the adapter hardware itself.
                g.DrawRectangle(pen, 3f, 6f, 18f, 12f);
                g.DrawRectangle(pen, 8f, 10f, 8f, 5f);
                g.DrawLine(pen, 6f, 18f, 6f, 21f);
                g.DrawLine(pen, 18f, 18f, 18f, 21f);
                break;

            case Glyph.Cable:
                // An RJ45 plug with two leads: the physical link.
                g.DrawLine(pen, 12f, 2.5f, 12f, 8f);
                g.DrawRectangle(pen, 7f, 8f, 10f, 7f);
                g.DrawLines(pen, new[] { new PointF(9.5f, 15f), new PointF(9.5f, 18f), new PointF(5f, 21.5f) });
                g.DrawLines(pen, new[] { new PointF(14.5f, 15f), new PointF(14.5f, 18f), new PointF(19f, 21.5f) });
                break;

            case Glyph.Ip:
                // A tag/label - an address assigned to you.
                g.DrawPolygon(pen, new[]
                {
                    new PointF(2.5f, 8f), new PointF(15f, 8f), new PointF(21.5f, 12f),
                    new PointF(15f, 16f), new PointF(2.5f, 16f),
                });
                g.FillEllipse(brush, 5.5f, 11f, 2.4f, 2.4f);
                break;

            case Glyph.Gateway:
                // A router: box with an antenna and link lights.
                g.DrawRectangle(pen, 2.5f, 12f, 19f, 8f);
                g.DrawLine(pen, 12f, 12f, 12f, 7f);
                g.DrawArc(pen, 7f, 2f, 10f, 10f, 200f, 140f);
                g.FillEllipse(brush, 6f, 15.2f, 1.9f, 1.9f);
                g.FillEllipse(brush, 10.2f, 15.2f, 1.9f, 1.9f);
                break;

            case Glyph.Globe:
                g.DrawEllipse(pen, 2.5f, 2.5f, 19f, 19f);
                g.DrawLine(pen, 2.5f, 12f, 21.5f, 12f);
                g.DrawArc(pen, 7f, 2.5f, 10f, 19f, 270f, 180f);
                g.DrawArc(pen, 7f, 2.5f, 10f, 19f, 90f, 180f);
                break;

            case Glyph.Dns:
                // A signpost: name resolved to a direction.
                g.DrawLine(pen, 9f, 3f, 9f, 21.5f);
                g.DrawPolygon(pen, new[]
                {
                    new PointF(9f, 5f), new PointF(19f, 5f), new PointF(21.5f, 8f),
                    new PointF(19f, 11f), new PointF(9f, 11f),
                });
                g.DrawPolygon(pen, new[]
                {
                    new PointF(9f, 13f), new PointF(4.5f, 13f), new PointF(2.5f, 16f),
                    new PointF(4.5f, 19f), new PointF(9f, 19f),
                });
                break;

            case Glyph.Target:
                g.DrawEllipse(pen, 3f, 3f, 18f, 18f);
                g.DrawEllipse(pen, 8f, 8f, 8f, 8f);
                g.FillEllipse(brush, 10.8f, 10.8f, 2.4f, 2.4f);
                break;
        }
    }

    /// <summary>
    /// A six-tooth gear. Built from polar coordinates rather than hand-placed points so the teeth
    /// stay evenly spaced; the hub is a plain circle punched out by drawing it as a second figure.
    /// </summary>
    private static void DrawGear(Graphics g, Pen pen)
    {
        const int teeth = 6;
        const float cx = 12f;
        const float cy = 12f;
        const float inner = 6.4f;
        const float outer = 9.6f;

        using var path = new GraphicsPath();
        var points = new List<PointF>();
        for (int i = 0; i < teeth; i++)
        {
            double mid = (i * 2 * Math.PI / teeth) - (Math.PI / 2);
            double half = Math.PI / teeth * 0.42;

            points.Add(Polar(cx, cy, inner, mid - (Math.PI / teeth) + half));
            points.Add(Polar(cx, cy, outer, mid - half));
            points.Add(Polar(cx, cy, outer, mid + half));
            points.Add(Polar(cx, cy, inner, mid + (Math.PI / teeth) - half));
        }

        path.AddPolygon(points.ToArray());
        g.DrawPath(pen, path);
        g.DrawEllipse(pen, cx - 2.9f, cy - 2.9f, 5.8f, 5.8f);
    }

    private static PointF Polar(float cx, float cy, float radius, double angle) =>
        new((float)(cx + (radius * Math.Cos(angle))), (float)(cy + (radius * Math.Sin(angle))));
}
