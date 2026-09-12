using System.Drawing.Drawing2D;

namespace InternetMonitor.UI;

/// <summary>Health of one node or connector segment in the diagram. Null = not checked yet.</summary>
public readonly record struct SegmentHealth(bool? Ok, bool WarningOnly = false)
{
    public static readonly SegmentHealth Unknown = new(null);
    public static readonly SegmentHealth Healthy = new(true);
    public static SegmentHealth Broken(bool warningOnly = false) => new(false, warningOnly);
}

/// <summary>
/// At-a-glance computer/router/cloud/server diagram for the status popup: computer and router
/// are drawn as broken (red, badged) when the problem is on that node itself, and the three
/// connector segments between them (computer-router, router-cloud, cloud-server) show which leg
/// of the path is healthy, degraded, or broken - a summary view, not a replacement for the
/// detailed row list beneath it. Deliberately simple hand-drawn GDI+ shapes, matching the style
/// already used by <see cref="MonitoringPulseControl"/> and <see cref="SparklineControl"/>.
/// </summary>
public sealed class ConnectivityDiagramControl : Control
{
    private static readonly Color IconColor = Color.DimGray;
    private static readonly Color OkColor = Color.LimeGreen;
    private static readonly Color WarningColor = Color.Orange;
    private static readonly Color ErrorColor = Color.Red;
    private static readonly Color UnknownColor = Color.Gainsboro;

    private readonly ToolTip _toolTip = new();

    // Cached rather than recreated on every OnPaint - their style is fixed (only positions and
    // the dynamic per-node/per-segment colors vary), so there's no reason to allocate a fresh
    // Font/Brush on every repaint.
    private readonly Font _labelFont;
    private readonly Brush _labelBrush = new SolidBrush(Color.DimGray);
    private readonly Font _reasonFont;
    private readonly Brush _warningReasonBrush = new SolidBrush(Color.DarkOrange);
    private readonly Brush _errorReasonBrush = new SolidBrush(Color.Firebrick);

    private SegmentHealth _computer = SegmentHealth.Unknown;
    private SegmentHealth _computerRouter = SegmentHealth.Unknown;
    private SegmentHealth _router = SegmentHealth.Unknown;
    private SegmentHealth _routerCloud = SegmentHealth.Unknown;
    private SegmentHealth _cloudServer = SegmentHealth.Unknown;
    private string _serverLabel = "Server";
    private string? _reasonHeadline;

    public ConnectivityDiagramControl()
    {
        DoubleBuffered = true;
        Size = new Size(380, 80);
        BackColor = Color.White;
        _labelFont = new Font(Font.FontFamily, 7.5f);
        _reasonFont = new Font(Font.FontFamily, 7.5f, FontStyle.Bold);
    }

    /// <summary>
    /// Updates the diagram from the diagnosis engine's single root-cause classification, already
    /// mapped by the caller onto exactly one of these five slots (at most one is ever broken at
    /// a time, matching <c>DiagnosisEngine</c>'s single-root-cause design). Cloud and Server never
    /// show their own broken state - no classification is ever attributed purely to either.
    /// </summary>
    public void SetState(
        SegmentHealth computer,
        SegmentHealth computerRouter,
        SegmentHealth router,
        SegmentHealth routerCloud,
        SegmentHealth cloudServer,
        string serverLabel,
        string? reasonHeadline,
        string? reasonExplanation)
    {
        _computer = computer;
        _computerRouter = computerRouter;
        _router = router;
        _routerCloud = routerCloud;
        _cloudServer = cloudServer;
        _serverLabel = serverLabel;
        _reasonHeadline = reasonHeadline;

        _toolTip.SetToolTip(this, reasonExplanation ?? string.Empty);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        const int iconWidth = 40;
        const int iconY = 10;
        const int iconHeight = 32;
        const int cloudWidth = 44;
        int lineY = iconY + (iconHeight / 2);

        var computerRect = new Rectangle(6, iconY, iconWidth, iconHeight);
        var serverRect = new Rectangle(Width - 6 - iconWidth, iconY, iconWidth, iconHeight);
        int span = (serverRect.X + (iconWidth / 2)) - (computerRect.X + (iconWidth / 2));
        int routerCenterX = computerRect.X + (iconWidth / 2) + (span / 3);
        int cloudCenterX = computerRect.X + (iconWidth / 2) + (2 * span / 3);
        var routerRect = new Rectangle(routerCenterX - (iconWidth / 2), iconY, iconWidth, iconHeight);
        var cloudRect = new Rectangle(cloudCenterX - (cloudWidth / 2), lineY - (iconHeight / 2) + 2, cloudWidth, iconHeight - 4);

        DrawSegment(g, computerRect.Right, routerRect.Left, lineY, _computerRouter);
        DrawSegment(g, routerRect.Right, cloudRect.Left, lineY, _routerCloud);
        DrawSegment(g, cloudRect.Right, serverRect.Left, lineY, _cloudServer);

        DrawComputer(g, computerRect, NodeColor(_computer));
        DrawRouter(g, routerRect, NodeColor(_router));
        DrawCloud(g, cloudRect, IconColor);
        DrawServer(g, serverRect, IconColor);

        if (_computer.Ok == false)
        {
            DrawNodeBrokenBadge(g, computerRect);
        }

        if (_router.Ok == false)
        {
            DrawNodeBrokenBadge(g, routerRect);
        }

        DrawCenteredLabel(g, "Computer", computerRect, _labelFont, _labelBrush);
        DrawCenteredLabel(g, "Router", routerRect, _labelFont, _labelBrush);
        DrawCenteredLabel(g, "Internet", cloudRect, _labelFont, _labelBrush);
        DrawCenteredLabel(g, _serverLabel, serverRect, _labelFont, _labelBrush);

        if (_reasonHeadline is not null)
        {
            bool warning = IsBrokenWarning(_computer) || IsBrokenWarning(_computerRouter) || IsBrokenWarning(_router)
                || IsBrokenWarning(_routerCloud) || IsBrokenWarning(_cloudServer);
            Brush reasonBrush = warning ? _warningReasonBrush : _errorReasonBrush;
            SizeF size = g.MeasureString(_reasonHeadline, _reasonFont);
            g.DrawString(_reasonHeadline, _reasonFont, reasonBrush, Math.Max(0, (Width - size.Width) / 2), 62);
        }
    }

    private static bool IsBrokenWarning(SegmentHealth health) => health is { Ok: false, WarningOnly: true };

    private static Color NodeColor(SegmentHealth health) => health.Ok == false ? ErrorColor : IconColor;

    private static Color SegmentColor(SegmentHealth health) => health.Ok switch
    {
        null => UnknownColor,
        true => OkColor,
        false => health.WarningOnly ? WarningColor : ErrorColor,
    };

    private static void DrawSegment(Graphics g, int x1, int x2, int y, SegmentHealth health)
    {
        Color color = SegmentColor(health);
        using var pen = new Pen(color, 2.5f);
        g.DrawLine(pen, x1, y, x2, y);

        if (health.Ok != false)
        {
            return;
        }

        int midX = (x1 + x2) / 2;
        const int badgeRadius = 8;
        var badgeRect = new Rectangle(midX - badgeRadius, y - badgeRadius, badgeRadius * 2, badgeRadius * 2);
        using var badgeBrush = new SolidBrush(color);
        g.FillEllipse(badgeBrush, badgeRect);

        using var markPen = new Pen(Color.White, 1.8f);
        if (health.WarningOnly)
        {
            // Exclamation mark - flagged/degraded, not a hard break.
            g.DrawLine(markPen, midX, y - 4, midX, y + 1);
            g.DrawEllipse(markPen, midX - 0.5f, y + 3.5f, 1f, 1f);
        }
        else
        {
            // Cross - connection is down.
            int r = badgeRadius - 3;
            g.DrawLine(markPen, midX - r, y - r, midX + r, y + r);
            g.DrawLine(markPen, midX - r, y + r, midX + r, y - r);
        }
    }

    private static void DrawNodeBrokenBadge(Graphics g, Rectangle iconBounds)
    {
        const int r = 7;
        var badgeRect = new Rectangle(iconBounds.Right - r, iconBounds.Top - r, r * 2, r * 2);
        using var badgeBrush = new SolidBrush(ErrorColor);
        g.FillEllipse(badgeBrush, badgeRect);

        using var markPen = new Pen(Color.White, 1.6f);
        int cx = badgeRect.X + r;
        int cy = badgeRect.Y + r;
        int rr = r - 2;
        g.DrawLine(markPen, cx - rr, cy - rr, cx + rr, cy + rr);
        g.DrawLine(markPen, cx - rr, cy + rr, cx + rr, cy - rr);
    }

    private static void DrawComputer(Graphics g, Rectangle bounds, Color color)
    {
        int screenHeight = bounds.Height - 8;
        var screen = new Rectangle(bounds.X, bounds.Y, bounds.Width, screenHeight);
        using var brush = new SolidBrush(color);
        using var path = RoundedRect(screen, 3);
        g.FillPath(brush, path);

        int standWidth = bounds.Width / 3;
        var stand = new Rectangle(bounds.X + ((bounds.Width - standWidth) / 2), screen.Bottom, standWidth, 3);
        g.FillRectangle(brush, stand);

        var baseRect = new Rectangle(bounds.X + 4, stand.Bottom, bounds.Width - 8, 2);
        g.FillRectangle(brush, baseRect);
    }

    private static void DrawRouter(Graphics g, Rectangle bounds, Color color)
    {
        const int bodyHeight = 16;
        var body = new Rectangle(bounds.X, bounds.Bottom - bodyHeight, bounds.Width, bodyHeight);
        using var brush = new SolidBrush(color);
        using var path = RoundedRect(body, 3);
        g.FillPath(brush, path);

        using var antennaPen = new Pen(color, 2f);
        int leftAntennaX = body.X + (body.Width / 3);
        int rightAntennaX = body.Right - (body.Width / 3);
        g.DrawLine(antennaPen, leftAntennaX, body.Top, leftAntennaX - 4, body.Top - 10);
        g.DrawLine(antennaPen, rightAntennaX, body.Top, rightAntennaX + 4, body.Top - 10);

        using var ledBrush = new SolidBrush(Color.White);
        g.FillEllipse(ledBrush, body.X + 5, body.Bottom - 6, 2, 2);
        g.FillEllipse(ledBrush, body.X + 10, body.Bottom - 6, 2, 2);
    }

    private static void DrawCloud(Graphics g, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        var body = new Rectangle(bounds.X, bounds.Y + (bounds.Height / 2), bounds.Width, bounds.Height / 2);
        using var bodyPath = RoundedRect(body, body.Height / 2);
        g.FillPath(brush, bodyPath);

        int puffSize = (int)(bounds.Height * 0.85);
        g.FillEllipse(brush, bounds.X + 2, bounds.Y, puffSize, puffSize);
        g.FillEllipse(brush, bounds.Right - puffSize - 2, bounds.Y, puffSize, puffSize);
        g.FillEllipse(brush, bounds.X + ((bounds.Width - puffSize) / 2), bounds.Y - 2, puffSize, puffSize);
    }

    private static void DrawServer(Graphics g, Rectangle bounds, Color color)
    {
        using var brush = new SolidBrush(color);
        using var path = RoundedRect(bounds, 3);
        g.FillPath(brush, path);

        using var ledBrush = new SolidBrush(Color.White);
        int dividerCount = 2;
        for (int i = 1; i <= dividerCount; i++)
        {
            int y = bounds.Y + (bounds.Height * i / (dividerCount + 1));
            g.DrawLine(Pens.White, bounds.X + 4, y, bounds.Right - 4, y);
            g.FillEllipse(ledBrush, bounds.Right - 8, y - 5, 2, 2);
        }
    }

    private static void DrawCenteredLabel(Graphics g, string text, Rectangle iconBounds, Font font, Brush brush)
    {
        SizeF size = g.MeasureString(text, font);
        float x = iconBounds.X + ((iconBounds.Width - size.Width) / 2);
        g.DrawString(text, font, brush, x, iconBounds.Bottom + 4);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _labelFont.Dispose();
            _labelBrush.Dispose();
            _reasonFont.Dispose();
            _warningReasonBrush.Dispose();
            _errorReasonBrush.Dispose();
        }

        base.Dispose(disposing);
    }
}
