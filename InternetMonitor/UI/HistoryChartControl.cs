using System.Drawing.Drawing2D;

namespace InternetMonitor.UI;

/// <summary>
/// Scrollable, zoomable, hoverable history chart for the Diagnostics screen's double-click
/// detail window - same hand-drawn GDI+ style family as <see cref="SparklineControl"/> (which it
/// otherwise resembles: 0-based y-axis, periodic x-axis time labels, outage-band shading), but
/// built to be hosted inside a plain <c>Panel { AutoScroll = true }</c> rather than always
/// stretching every point to fill its own width. Points are spaced by index, not literal elapsed
/// time - simpler to zoom/scroll correctly and avoids visual weirdness from irregular gaps (a
/// manual "Check now" inserts an out-of-cadence point); each point's real timestamp is still
/// shown via its axis tick label and its hover tooltip, so no information is lost.
///
/// Must NOT be Docked by its host - a docked child is forcibly resized to fit its parent on every
/// layout pass, so it could never exceed the viewport and trigger scrolling. Host it with
/// <c>Location</c>/<c>Size</c> and <c>Anchor = Top | Bottom | Left</c> (omitting Right) so height
/// tracks the panel's own resizes for free while width stays under this control's management -
/// see <see cref="ProbeHistoryDetailForm"/> for the exact recipe.
/// </summary>
public sealed class HistoryChartControl : Control
{
    private const int PlotPadding = 8;
    private const int AxisStripHeight = 16;
    private const double MinPixelsPerPoint = 2;
    private const double MaxPixelsPerPoint = 80;
    private const double DefaultPixelsPerPoint = 6;

    private static readonly Color OutageColor = Color.FromArgb(60, Color.Firebrick);

    private IReadOnlyList<(DateTimeOffset Timestamp, double Value)> _points = [];
    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset? End)> _outages = [];
    private string _valueUnitLabel = "ms";
    private string _emptyMessage = string.Empty;
    private double _pixelsPerPoint = DefaultPixelsPerPoint;
    private bool _userZoomed;
    private int _hoverIndex = -1;

    public HistoryChartControl()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        Size = new Size(600, 300);
    }

    public void SetData(IReadOnlyList<(DateTimeOffset Timestamp, double Value)> points, string valueUnitLabel, string emptyMessage)
    {
        _points = points;
        _valueUnitLabel = valueUnitLabel;
        _emptyMessage = emptyMessage;
        _hoverIndex = -1;
        RecomputeWidth();
        Invalidate();
    }

    /// <summary>Time ranges (e.g. incidents) to shade behind the plot, so a latency spike can be visually correlated with a logged incident. An open-ended range (End == null) is treated as still ongoing.</summary>
    public void SetOutages(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset? End)> outages)
    {
        _outages = outages;
        Invalidate();
    }

    /// <summary>Call when the hosting panel's viewport is resized, so a not-yet-manually-zoomed chart keeps fitting all points to the visible width.</summary>
    public void NotifyViewportResized()
    {
        if (!_userZoomed)
        {
            RecomputeWidth();
            Invalidate();
        }
    }

    private int ViewportWidth => (Parent?.ClientSize.Width ?? Width) is var w && w > 0 ? w : Width;

    private void RecomputeWidth()
    {
        int viewportWidth = ViewportWidth;
        if (!_userZoomed && _points.Count > 1)
        {
            _pixelsPerPoint = Math.Clamp(
                (double)(viewportWidth - (2 * PlotPadding)) / (_points.Count - 1),
                MinPixelsPerPoint, MaxPixelsPerPoint);
        }

        int contentWidth = _points.Count > 1
            ? (int)((_points.Count - 1) * _pixelsPerPoint) + (2 * PlotPadding)
            : viewportWidth;
        Width = Math.Max(viewportWidth, contentWidth);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_points.Count < 2)
        {
            return;
        }

        _userZoomed = true;
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        _pixelsPerPoint = Math.Clamp(_pixelsPerPoint * factor, MinPixelsPerPoint, MaxPixelsPerPoint);
        int contentWidth = (int)((_points.Count - 1) * _pixelsPerPoint) + (2 * PlotPadding);
        Width = Math.Max(ViewportWidth, contentWidth);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_points.Count == 0)
        {
            return;
        }

        int index = _points.Count == 1 ? 0 : (int)Math.Round((e.X - PlotPadding) / _pixelsPerPoint);
        index = Math.Clamp(index, 0, _points.Count - 1);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var borderPen = new Pen(Color.Gainsboro);
        g.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

        if (_points.Count == 0)
        {
            using var emptyBrush = new SolidBrush(Color.Gray);
            g.DrawString(_emptyMessage, Font, emptyBrush, PlotPadding, PlotPadding);
            return;
        }

        double dataMax = _points.Max(p => p.Value);
        double axisMax = Math.Max(dataMax, 0.0001);

        int plotHeight = Math.Max(1, Height - (2 * PlotPadding) - AxisStripHeight);

        float ToX(int index) => PlotPadding + (float)(index * _pixelsPerPoint);
        float ToY(double value) => PlotPadding + plotHeight - (float)(value / axisMax * plotHeight);

        // X-axis: time-of-day labels spaced by pixel budget (not a fixed tick count, since the
        // visible span varies continuously with zoom) so labels never overlap.
        using (var gridPen = new Pen(Color.WhiteSmoke))
        using (var axisTextBrush = new SolidBrush(Color.DimGray))
        using (var axisFont = new Font(Font.FontFamily, 7.5f))
        {
            double minLabelSpacingPx = 70;
            int labelStride = Math.Max(1, (int)(minLabelSpacingPx / Math.Max(_pixelsPerPoint, 0.01)));
            for (int i = 0; i < _points.Count; i += labelStride)
            {
                float x = ToX(i);
                g.DrawLine(gridPen, x, PlotPadding, x, PlotPadding + plotHeight);
                string tickLabel = _points[i].Timestamp.ToLocalTime().ToString("HH:mm:ss");
                SizeF textSize = g.MeasureString(tickLabel, axisFont);
                g.DrawString(tickLabel, axisFont, axisTextBrush, x - (textSize.Width / 2), PlotPadding + plotHeight + 1);
            }
        }

        if (_outages.Count > 0)
        {
            DateTimeOffset minTime = _points[0].Timestamp;
            DateTimeOffset maxTime = _points[^1].Timestamp;
            using var outageBrush = new SolidBrush(OutageColor);
            foreach ((DateTimeOffset start, DateTimeOffset? end) in _outages)
            {
                DateTimeOffset bandEnd = end ?? maxTime;
                if (bandEnd < minTime || start > maxTime)
                {
                    continue;
                }

                int startIndex = NearestIndexAtOrAfter(start);
                int endIndex = NearestIndexAtOrAfter(bandEnd);
                float x1 = ToX(startIndex);
                float x2 = Math.Max(x1 + 1.5f, ToX(endIndex));
                g.FillRectangle(outageBrush, x1, PlotPadding, x2 - x1, plotHeight);
            }
        }

        if (_points.Count == 1)
        {
            PointF pt = new(ToX(0), ToY(_points[0].Value));
            using var dotBrush = new SolidBrush(Color.SteelBlue);
            g.FillEllipse(dotBrush, pt.X - 3, pt.Y - 3, 6, 6);
        }
        else
        {
            PointF[] pts = new PointF[_points.Count];
            for (int i = 0; i < _points.Count; i++)
            {
                pts[i] = new PointF(ToX(i), ToY(_points[i].Value));
            }

            using var linePen = new Pen(Color.SteelBlue, 1.6f) { LineJoin = LineJoin.Round };
            g.DrawLines(linePen, pts);
        }

        if (_hoverIndex >= 0 && _hoverIndex < _points.Count)
        {
            (DateTimeOffset ts, double value) = _points[_hoverIndex];
            float hoverX = ToX(_hoverIndex);
            float hoverY = ToY(value);

            using var guidePen = new Pen(Color.SteelBlue) { DashStyle = DashStyle.Dot };
            g.DrawLine(guidePen, hoverX, PlotPadding, hoverX, PlotPadding + plotHeight);

            using var hoverDotBrush = new SolidBrush(Color.Firebrick);
            g.FillEllipse(hoverDotBrush, hoverX - 3.5f, hoverY - 3.5f, 7, 7);

            string info = $"{ts.ToLocalTime():HH:mm:ss}\n{value:F0} {_valueUnitLabel}";
            using var infoFont = new Font(Font.FontFamily, 8f);
            SizeF infoSize = g.MeasureString(info, infoFont);
            float boxX = Math.Clamp(hoverX + 8, PlotPadding, Width - infoSize.Width - PlotPadding - 4);
            float boxY = Math.Clamp(hoverY - infoSize.Height - 6, PlotPadding, Height - infoSize.Height - AxisStripHeight);
            using var boxBrush = new SolidBrush(Color.FromArgb(235, Color.White));
            using var boxPen = new Pen(Color.SteelBlue);
            var boxRect = new RectangleF(boxX - 3, boxY - 2, infoSize.Width + 6, infoSize.Height + 4);
            g.FillRectangle(boxBrush, boxRect);
            g.DrawRectangle(boxPen, boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height);
            using var infoBrush = new SolidBrush(Color.Black);
            g.DrawString(info, infoFont, infoBrush, boxX, boxY);
        }

        string summary = $"{_points[^1].Value:F0} {_valueUnitLabel} (min {_points.Min(p => p.Value):F0} / max {dataMax:F0} {_valueUnitLabel})";
        using var summaryBrush = new SolidBrush(Color.DimGray);
        using var summaryFont = new Font(Font.FontFamily, 7.5f);
        SizeF summarySize = g.MeasureString(summary, summaryFont);
        using var summaryBackBrush = new SolidBrush(Color.FromArgb(200, Color.White));
        g.FillRectangle(summaryBackBrush, PlotPadding, PlotPadding, summarySize.Width, summarySize.Height);
        g.DrawString(summary, summaryFont, summaryBrush, PlotPadding, PlotPadding);
    }

    private int NearestIndexAtOrAfter(DateTimeOffset time)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            if (_points[i].Timestamp >= time)
            {
                return i;
            }
        }

        return _points.Count - 1;
    }
}
