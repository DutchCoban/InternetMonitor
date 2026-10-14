using System.Drawing.Drawing2D;
using InternetMonitor.Core.History;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The chart. One control serves both the full interactive view on the History page and the
/// compact inline strip on the Status page, because two implementations of the same picture is
/// how v1 ended up with a sparkline and a history chart that disagreed about what a gap meant.
///
/// What is different from v1, and why:
///
///  - The x-axis is time. v1 spaced points by index, which made zooming simpler but meant a
///    two-hour outage with no samples in it drew as a single flat segment indistinguishable from
///    two adjacent healthy readings. Here a gap in the data is a gap in the line.
///  - Points are downsampled to the pixel width before drawing (see <see cref="Downsample"/>), so
///    a month of history pans and zooms as smoothly as an hour of it.
///  - Zoom anchors on the cursor rather than the centre, which is what makes "point at the spike
///    and scroll" work the way people expect.
///  - Thresholds are drawn as soft background bands rather than lines, so a reading can be judged
///    without reading the axis.
///
/// State series (adapter, link) are drawn as a connected/disconnected timeline instead: the
/// midpoint between 0 and 1 has no meaning, so a line through it would be a lie.
/// </summary>
public sealed class MetricChart : ThemedControl
{
    private IReadOnlyList<MetricPoint> _points = [];
    private IReadOnlyList<MetricPoint> _visible = [];
    private string _unit = string.Empty;
    private double? _warnThreshold;
    private double? _errorThreshold;
    private bool _stateSeries;
    private bool _markMetadataChanges;
    private int _stateBucketCount = 1;
    private bool _compact;

    private DateTimeOffset _viewStart;
    private DateTimeOffset _viewEnd;
    private bool _hasView;

    private int _hoverIndex = -1;
    private Point? _dragAnchor;
    private DateTimeOffset _dragViewStart;
    private DateTimeOffset _dragViewEnd;

    public MetricChart()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }

    /// <summary>
    /// Compact mode: no axes, no hover, no footer - the inline strip on the status page. A compact
    /// chart is also not focusable, because a focusable control inside a scrolling page gets
    /// scrolled into view the moment it takes focus, which would open the status page already
    /// scrolled past its own heading.
    /// </summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            _compact = value;
            TabStop = !value;
            SetStyle(ControlStyles.Selectable, !value);
        }
    }

    protected override Color BehindColor => Theme.Current.Surface;

    // Wide enough for "1000 ms" at the default font - gridline labels now repeat the unit (see
    // DrawGrid) rather than showing a bare number with no indication of what it is measured in.
    private int AxisLeft => Compact ? 0 : Dp(56);

    private int AxisBottom => Compact ? 0 : Dp(20);

    private int FooterHeight => Compact ? 0 : Dp(22);

    private Rectangle PlotBounds => new(
        AxisLeft,
        Sp(1),
        Math.Max(1, Width - AxisLeft - Sp(2)),
        Math.Max(1, Height - AxisBottom - FooterHeight - Sp(2)));

    public void SetSeries(
        IReadOnlyList<MetricPoint> points,
        string unit,
        bool stateSeries = false,
        double? warnThreshold = null,
        double? errorThreshold = null,
        bool markMetadataChanges = false)
    {
        _points = points;
        _unit = unit;
        _stateSeries = stateSeries;
        _markMetadataChanges = markMetadataChanges;
        _warnThreshold = warnThreshold;
        _errorThreshold = errorThreshold;

        if (!_hasView)
        {
            ResetView();
        }
        else
        {
            RecomputeVisible();
        }

        Invalidate();
    }

    /// <summary>Shows the most recent <paramref name="range"/> of data, or everything when null.</summary>
    public void SetRange(TimeSpan? range)
    {
        if (_points.Count == 0)
        {
            _hasView = false;
            Invalidate();
            return;
        }

        DateTimeOffset end = _points[^1].Timestamp;
        _viewEnd = end;
        _viewStart = range is { } span ? end - span : _points[0].Timestamp;

        // A single point, or every point sharing a timestamp, would give a zero-width window and
        // divide by zero on the first projection.
        if (_viewEnd <= _viewStart)
        {
            _viewStart = _viewEnd - TimeSpan.FromMinutes(1);
        }

        _hasView = true;
        RecomputeVisible();
        Invalidate();
    }

    public void ResetView() => SetRange(null);

    private void RecomputeVisible()
    {
        if (!_hasView || _points.Count == 0)
        {
            _visible = [];
            return;
        }

        var window = new List<MetricPoint>();
        foreach (MetricPoint point in _points)
        {
            if (point.Timestamp >= _viewStart && point.Timestamp <= _viewEnd)
            {
                window.Add(point);
            }
        }

        if (_stateSeries)
        {
            // Coarse, UptimeRobot-style segments rather than one per pixel - on a long healthy
            // stretch, one-per-pixel is visually identical to a single solid bar, which is
            // precisely the "just one thick green line" complaint this exists to fix. The bucket
            // count is stored because DrawStateTimeline needs it again to size each segment.
            _stateBucketCount = Math.Clamp(PlotBounds.Width / Dp(6), 16, 200);
            _visible = Downsample.BucketState(window, _viewStart, _viewEnd, _stateBucketCount);
            return;
        }

        // One point per pixel is the most any chart can show. Asking for a few more than the plot
        // is wide keeps diagonal segments smooth without changing the cost.
        int budget = Math.Max(64, PlotBounds.Width * 2);
        _visible = Downsample.Lttb(window, budget);
    }

    // ---- Interaction -------------------------------------------------------------------------

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Compact || !_hasView || _points.Count < 2)
        {
            return;
        }

        // Anchored on the cursor: the timestamp under the pointer stays under the pointer, which
        // is what makes zooming into a spike feel like zooming into *that* spike.
        double anchorFraction = Math.Clamp((e.X - PlotBounds.Left) / (double)PlotBounds.Width, 0, 1);
        long span = _viewEnd.UtcTicks - _viewStart.UtcTicks;
        long anchorTicks = _viewStart.UtcTicks + (long)(span * anchorFraction);

        double factor = e.Delta > 0 ? 0.75 : 1 / 0.75;
        long newSpan = (long)(span * factor);

        long fullSpan = _points[^1].Timestamp.UtcTicks - _points[0].Timestamp.UtcTicks;
        newSpan = Math.Clamp(newSpan, TimeSpan.FromSeconds(30).Ticks, Math.Max(TimeSpan.FromMinutes(1).Ticks, fullSpan));

        _viewStart = new DateTimeOffset(anchorTicks - (long)(newSpan * anchorFraction), TimeSpan.Zero);
        _viewEnd = new DateTimeOffset(_viewStart.UtcTicks + newSpan, TimeSpan.Zero);
        ClampView();
        RecomputeVisible();
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (Compact || e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();
        _dragAnchor = e.Location;
        _dragViewStart = _viewStart;
        _dragViewEnd = _viewEnd;
        Cursor = Cursors.SizeWE;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragAnchor = null;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Compact)
        {
            return;
        }

        if (_dragAnchor is { } anchor && _hasView)
        {
            long span = _dragViewEnd.UtcTicks - _dragViewStart.UtcTicks;
            long perPixel = span / Math.Max(1, PlotBounds.Width);
            long shift = (anchor.X - e.X) * perPixel;

            _viewStart = new DateTimeOffset(_dragViewStart.UtcTicks + shift, TimeSpan.Zero);
            _viewEnd = new DateTimeOffset(_dragViewEnd.UtcTicks + shift, TimeSpan.Zero);
            ClampView();
            RecomputeVisible();
            Invalidate();
            return;
        }

        int index = NearestVisibleIndex(e.X);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!Compact)
        {
            ResetView();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // The downsample budget is a function of the plot width, so a resize changes how many
        // points are kept.
        RecomputeVisible();
    }

    /// <summary>Keeps the window inside the data, so panning cannot wander off into empty time.</summary>
    private void ClampView()
    {
        if (_points.Count == 0)
        {
            return;
        }

        long span = _viewEnd.UtcTicks - _viewStart.UtcTicks;
        long first = _points[0].Timestamp.UtcTicks;
        long last = _points[^1].Timestamp.UtcTicks;

        if (_viewStart.UtcTicks < first)
        {
            _viewStart = new DateTimeOffset(first, TimeSpan.Zero);
            _viewEnd = new DateTimeOffset(first + span, TimeSpan.Zero);
        }

        if (_viewEnd.UtcTicks > last)
        {
            _viewEnd = new DateTimeOffset(last, TimeSpan.Zero);
            _viewStart = new DateTimeOffset(Math.Max(first, last - span), TimeSpan.Zero);
        }
    }

    private int NearestVisibleIndex(int x)
    {
        if (_visible.Count == 0 || !PlotBounds.Contains(x, PlotBounds.Top + 1))
        {
            return -1;
        }

        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _visible.Count; i++)
        {
            double distance = Math.Abs(ToX(_visible[i].Timestamp) - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return bestDistance <= Dp(24) ? best : -1;
    }

    // ---- Projection --------------------------------------------------------------------------

    private float ToX(DateTimeOffset timestamp)
    {
        long span = Math.Max(1, _viewEnd.UtcTicks - _viewStart.UtcTicks);
        double fraction = (timestamp.UtcTicks - _viewStart.UtcTicks) / (double)span;
        return PlotBounds.Left + (float)(fraction * PlotBounds.Width);
    }

    private float ToY(double value, double max)
    {
        double fraction = max <= 0 ? 0 : Math.Clamp(value / max, 0, 1);
        return PlotBounds.Bottom - (float)(fraction * PlotBounds.Height);
    }

    /// <summary>
    /// The y-axis always starts at zero for latency. A chart auto-scaled to its own minimum makes
    /// a flat 8ms line look like a mountain range, which is actively misleading in a tool people
    /// open when they are already worried.
    ///
    /// The top is the data's own maximum, rounded up to a readable number. It is deliberately NOT
    /// stretched to keep the error threshold on screen: on a healthy connection that puts the
    /// whole series in the bottom few pixels and hides exactly the variation the chart exists to
    /// show. The bands already convey the thresholds - when everything is well below the warning
    /// level the plot is simply all green, which reads correctly at a glance.
    /// </summary>
    private double AxisMax()
    {
        if (_stateSeries)
        {
            return 1;
        }

        double max = 0;
        foreach (MetricPoint point in _visible)
        {
            max = Math.Max(max, point.Value);
        }

        // A floor keeps a sub-millisecond series from producing an axis labelled in fractions.
        return NiceCeiling(Math.Max(max * 1.15, 5));
    }

    /// <summary>
    /// Rounds up to the next 1, 2, 2.5 or 5 times a power of ten, so axis labels land on numbers
    /// people read without effort (0/25/50/75/100) instead of on 0/17/34/52/69.
    /// </summary>
    private static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double normalized = value / magnitude;

        double nice = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10,
        };

        return nice * magnitude;
    }

    // ---- Painting ----------------------------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        if (_points.Count == 0)
        {
            if (!Compact)
            {
                g.DrawTextCrisp(
                    LocalizationManager.Instance.Get("chart.empty"),
                    Theme.Font(FontRole.Body),
                    p.TextMuted,
                    ClientRectangle,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            return;
        }

        if (!_hasView)
        {
            ResetView();
        }

        Rectangle plot = PlotBounds;
        double max = AxisMax();

        if (!Compact)
        {
            DrawBands(g, p, plot, max);
            DrawGrid(g, p, plot, max);
        }

        if (_stateSeries)
        {
            DrawStateTimeline(g, p, plot);
        }
        else
        {
            DrawSeries(g, p, plot, max);
        }

        if (_markMetadataChanges && !Compact)
        {
            DrawMetadataChanges(g, p, plot);
        }

        if (!Compact)
        {
            DrawTimeAxis(g, p, plot);
            DrawFooter(g, p);
            DrawHover(g, p, plot, max);
        }
    }

    /// <summary>Soft horizontal bands for the good/warning/bad latency ranges.</summary>
    private void DrawBands(Graphics g, Palette p, Rectangle plot, double max)
    {
        if (_stateSeries || _warnThreshold is not { } warn || _errorThreshold is not { } error)
        {
            return;
        }

        void Band(double from, double to, Color color)
        {
            float top = ToY(Math.Min(to, max), max);
            float bottom = ToY(Math.Min(from, max), max);
            if (bottom - top < 0.5f)
            {
                return;
            }

            using var brush = new SolidBrush(color.WithAlpha(Theme.IsDark ? 38 : 48));
            g.FillRectangle(brush, plot.Left, top, plot.Width, bottom - top);
        }

        Band(0, warn, p.Ok);
        Band(warn, error, p.Warn);
        Band(error, max, p.Error);
    }

    private void DrawGrid(Graphics g, Palette p, Rectangle plot, double max)
    {
        using var pen = new Pen(p.GridLine, 1f);
        using var brush = new SolidBrush(p.TextMuted);

        int lines = 4;
        for (int i = 0; i <= lines; i++)
        {
            double value = max * i / lines;
            float y = ToY(value, max);
            g.DrawLine(pen, plot.Left, y, plot.Right, y);

            // Every gridline repeats the unit rather than just the top one: it is the simplest,
            // least-ambiguous way to answer "is this milliseconds?" at a glance, and the labels
            // have room for it now that AxisLeft accounts for the extra width.
            string label = _stateSeries
                ? LocalizationManager.Instance.Get(i == lines ? "chart.state.up" : i == 0 ? "chart.state.down" : string.Empty)
                : _unit.Length > 0 ? $"{value:F0} {_unit}" : $"{value:F0}";

            if (label.Length > 0 && !label.StartsWith('['))
            {
                g.DrawTextCrisp(
                    label,
                    Theme.Font(FontRole.Small),
                    p.TextMuted,
                    new Rectangle(0, (int)y - Dp(8), AxisLeft - Sp(1), Dp(16)),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        // No separate unit caption above the axis: there is no room above the plot without
        // stealing it from the data, and it collided with the topmost tick label. The unit is
        // already on the footer line and in the hover card, which is where it is read anyway.
    }

    /// <summary>
    /// Draws the line and the gradient under it, breaking the path wherever there is a gap in the
    /// data rather than drawing a straight line across the hole. A missing stretch of samples is
    /// usually the most interesting thing on the chart - it is the outage.
    /// </summary>
    private void DrawSeries(Graphics g, Palette p, Rectangle plot, double max)
    {
        if (_visible.Count == 0)
        {
            return;
        }

        long gapTicks = GapThresholdTicks();
        List<List<PointF>> segments = [];
        List<PointF> current = [];

        for (int i = 0; i < _visible.Count; i++)
        {
            if (i > 0 && _visible[i].Timestamp.UtcTicks - _visible[i - 1].Timestamp.UtcTicks > gapTicks)
            {
                if (current.Count > 0)
                {
                    segments.Add(current);
                }

                current = [];
            }

            current.Add(new PointF(ToX(_visible[i].Timestamp), ToY(_visible[i].Value, max)));
        }

        if (current.Count > 0)
        {
            segments.Add(current);
        }

        foreach (List<PointF> segment in segments)
        {
            if (segment.Count == 1)
            {
                using var dot = new SolidBrush(p.Accent);
                g.FillEllipse(dot, segment[0].X - DpF(2.5f), segment[0].Y - DpF(2.5f), DpF(5f), DpF(5f));
                continue;
            }

            using (var fill = new GraphicsPath())
            {
                fill.AddLines([.. segment]);
                fill.AddLine(segment[^1].X, segment[^1].Y, segment[^1].X, plot.Bottom);
                fill.AddLine(segment[^1].X, plot.Bottom, segment[0].X, plot.Bottom);
                fill.CloseFigure();

                using var brush = new LinearGradientBrush(
                    new RectangleF(plot.Left, plot.Top, Math.Max(1, plot.Width), Math.Max(1, plot.Height)),
                    p.Accent.WithAlpha(Theme.IsDark ? 85 : 60),
                    p.Accent.WithAlpha(0),
                    LinearGradientMode.Vertical);
                g.FillPath(brush, fill);
            }

            using var pen = new Pen(p.Accent, DpF(1.8f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, [.. segment]);
        }
    }

    /// <summary>
    /// A thin dotted line with a small diamond on top wherever a point's metadata differs from the
    /// previous point's - for the Wi-Fi signal series, a switch to another access point. Read from
    /// the full point list, not the downsampled visible one: a downsample keeps the shape of the
    /// line, not every point where the metadata happened to change.
    /// </summary>
    private void DrawMetadataChanges(Graphics g, Palette p, Rectangle plot)
    {
        using var pen = new Pen(p.TextMuted.WithAlpha(150), 1f) { DashStyle = DashStyle.Dot };
        using var brush = new SolidBrush(p.TextSecondary);
        float size = DpF(4f);

        string? previous = null;
        foreach (MetricPoint point in _points)
        {
            if (point.Metadata is not { Length: > 0 } metadata)
            {
                continue;
            }

            bool changed = previous is not null && metadata != previous;
            previous = metadata;
            if (!changed || point.Timestamp < _viewStart || point.Timestamp > _viewEnd)
            {
                continue;
            }

            float x = ToX(point.Timestamp);
            g.DrawLine(pen, x, plot.Top + size, x, plot.Bottom);
            g.FillPolygon(brush, new[]
            {
                new PointF(x, plot.Top), new PointF(x + size, plot.Top + size),
                new PointF(x, plot.Top + (size * 2)), new PointF(x - size, plot.Top + size),
            });
        }
    }

    /// <summary>
    /// A gap is anything more than a few times the series' own typical spacing. Derived from the
    /// data rather than hardcoded, because the same chart serves a 1-second ping series and a
    /// 15-second step series.
    /// </summary>
    private long GapThresholdTicks()
    {
        if (_visible.Count < 3)
        {
            return TimeSpan.FromMinutes(5).Ticks;
        }

        long[] deltas = new long[_visible.Count - 1];
        for (int i = 1; i < _visible.Count; i++)
        {
            deltas[i - 1] = _visible[i].Timestamp.UtcTicks - _visible[i - 1].Timestamp.UtcTicks;
        }

        Array.Sort(deltas);
        long median = deltas[deltas.Length / 2];
        return Math.Max(median * 4, TimeSpan.FromSeconds(30).Ticks);
    }

    /// <summary>
    /// Draws the bucketed state series as a row of distinct, rounded segments with a thin gap
    /// between them - the familiar "uptime bar" look (UptimeRobot, status pages) - rather than one
    /// edge-to-edge fill. A long healthy stretch used to render as a single solid rectangle with no
    /// visible structure at all; it now reads as a sequence of individually-measured slices even
    /// when every one of them is green.
    ///
    /// Segment width comes from the known, constant bucket span (<see cref="_stateBucketCount"/>
    /// divided into the plot width), not from the distance to the next drawn point - a real gap in
    /// the data simply has no segment in it, rather than the previous segment stretching across the
    /// gap to reach whatever comes next.
    /// </summary>
    private void DrawStateTimeline(Graphics g, Palette p, Rectangle plot)
    {
        if (_visible.Count == 0)
        {
            return;
        }

        int barHeight = Math.Max(Dp(14), (int)(plot.Height * 0.55f));
        float top = plot.Top + ((plot.Height - barHeight) / 2f);
        float gap = DpF(1.5f);
        float segmentWidth = Math.Max(DpF(2f), (plot.Width / (float)_stateBucketCount) - gap);
        float radius = Dp(2);

        foreach (MetricPoint point in _visible)
        {
            float x = ToX(point.Timestamp) - (segmentWidth / 2f);

            Color color = point.Value switch
            {
                >= 0.999 => p.Ok,
                <= 0.001 => p.Error,
                _ => p.Warn, // a slice containing both a down and an up reading
            };

            g.FillRounded(new RectangleF(x, top, segmentWidth, barHeight), radius, color.WithAlpha(220));
        }
    }

    private void DrawTimeAxis(Graphics g, Palette p, Rectangle plot)
    {
        TimeSpan span = _viewEnd - _viewStart;
        string format = span.TotalHours < 2 ? "HH:mm" : span.TotalDays < 2 ? "HH:mm" : "dd/MM";

        // One label per ~90 design pixels, so the axis never crowds regardless of width.
        int count = Math.Max(2, plot.Width / Dp(90));
        using var pen = new Pen(p.GridLine, 1f);

        for (int i = 0; i <= count; i++)
        {
            DateTimeOffset at = _viewStart + (span * i / count);
            float x = ToX(at);
            g.DrawLine(pen, x, plot.Top, x, plot.Bottom);

            string label = at.ToLocalTime().ToString(format);
            Size size = GdiExtensions.MeasureCrisp(label, Theme.Font(FontRole.Small));

            // Clamped to the control, so the first and last labels are not half-cut by the edge.
            int labelX = Math.Clamp((int)x - (size.Width / 2), 0, Math.Max(0, Width - size.Width));
            g.DrawTextCrisp(
                label,
                Theme.Font(FontRole.Small),
                p.TextMuted,
                new Rectangle(labelX, plot.Bottom + Dp(3), size.Width, Dp(14)));
        }
    }

    private void DrawFooter(Graphics g, Palette p)
    {
        if (Downsample.Summarize(_visible) is not { } stats)
        {
            return;
        }

        LocalizationManager loc = LocalizationManager.Instance;
        string text = _stateSeries
            ? loc.Format("chart.footer.uptime", $"{stats.Average * 100:F1}")
            : loc.Format(
                "chart.footer.stats",
                $"{stats.Min:F0}",
                $"{stats.Average:F0}",
                $"{stats.Max:F0}",
                $"{stats.P95:F0}",
                _unit);

        g.DrawTextCrisp(
            text,
            Theme.Font(FontRole.Small),
            p.TextMuted,
            new Rectangle(AxisLeft, Height - FooterHeight, Math.Max(0, Width - AxisLeft), FooterHeight));
    }

    private void DrawHover(Graphics g, Palette p, Rectangle plot, double max)
    {
        if (_hoverIndex < 0 || _hoverIndex >= _visible.Count)
        {
            return;
        }

        MetricPoint point = _visible[_hoverIndex];
        float x = ToX(point.Timestamp);
        float y = ToY(point.Value, max);

        using (var guide = new Pen(p.Accent.WithAlpha(120), 1f) { DashStyle = DashStyle.Dot })
        {
            g.DrawLine(guide, x, plot.Top, x, plot.Bottom);
        }

        using (var halo = new SolidBrush(p.Accent.WithAlpha(60)))
        {
            g.FillEllipse(halo, x - DpF(7f), y - DpF(7f), DpF(14f), DpF(14f));
        }

        using (var dot = new SolidBrush(p.Accent))
        {
            g.FillEllipse(dot, x - DpF(3.5f), y - DpF(3.5f), DpF(7f), DpF(7f));
        }

        string time = point.Timestamp.ToLocalTime().ToString("dd/MM HH:mm:ss");
        string value = _stateSeries
            ? LocalizationManager.Instance.Get(point.Value switch
            {
                >= 0.999 => "chart.state.up",
                <= 0.001 => "chart.state.down",
                _ => "chart.state.partial", // this slice held both a down and an up reading
            })
            : $"{point.Value:F0} {_unit}";

        Size timeSize = GdiExtensions.MeasureCrisp(time, Theme.Font(FontRole.Small));
        Size valueSize = GdiExtensions.MeasureCrisp(value, Theme.Font(FontRole.BodyStrong));

        int cardWidth = Math.Max(timeSize.Width, valueSize.Width) + (Sp(3) * 2);
        int cardHeight = timeSize.Height + valueSize.Height + (Sp(2) * 2) + Dp(2);

        // Flip to the other side of the cursor near the right edge so the card never leaves the
        // plot.
        float cardX = x + Dp(12);
        if (cardX + cardWidth > plot.Right)
        {
            cardX = x - Dp(12) - cardWidth;
        }

        float cardY = Math.Clamp(y - (cardHeight / 2f), plot.Top, Math.Max(plot.Top, plot.Bottom - cardHeight));
        var card = new RectangleF(cardX, cardY, cardWidth, cardHeight);

        g.DrawShadow(card, Theme.RadiusControl(DeviceDpi), p.Shadow, Dp(3));
        g.FillRounded(card, Theme.RadiusControl(DeviceDpi), p.Surface);
        g.DrawRounded(card, Theme.RadiusControl(DeviceDpi), p.BorderStrong);

        g.DrawTextCrisp(
            time,
            Theme.Font(FontRole.Small),
            p.TextMuted,
            new Rectangle((int)card.X + Sp(3), (int)card.Y + Sp(2), cardWidth, timeSize.Height));

        g.DrawTextCrisp(
            value,
            Theme.Font(FontRole.BodyStrong),
            p.TextPrimary,
            new Rectangle((int)card.X + Sp(3), (int)card.Y + Sp(2) + timeSize.Height + Dp(2), cardWidth, valueSize.Height));
    }
}
