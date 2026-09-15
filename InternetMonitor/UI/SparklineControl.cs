using System.Drawing.Drawing2D;

namespace InternetMonitor.UI;

/// <summary>
/// Small hand-drawn line chart (GDI+, no charting dependency), built for the continuous
/// ping-latency graph on the Diagnostics screen. Fixed 0-based y-axis (latency has a natural
/// floor at zero) and periodic time labels on the x-axis - deliberately simple, matching the
/// style already used by <see cref="MonitoringPulseControl"/>.
/// </summary>
public sealed class SparklineControl : Control
{
    private const int PlotPadding = 6;
    private const int AxisStripHeight = 14;
    private const int TickCount = 5;

    private IReadOnlyList<(DateTimeOffset Timestamp, double Value)> _points = [];
    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset? End)> _outages = [];
    private string _emptyMessage = string.Empty;

    public SparklineControl()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        Size = new Size(260, 70);
    }

    public void SetData(IReadOnlyList<(DateTimeOffset Timestamp, double Value)> points, string emptyMessage)
    {
        _points = points;
        _emptyMessage = emptyMessage;
        Invalidate();
    }

    /// <summary>Time ranges (e.g. incidents) to shade behind the plot, so outages are visible alongside the metric. An open-ended range (End == null) is treated as still ongoing.</summary>
    public void SetOutages(IReadOnlyList<(DateTimeOffset Start, DateTimeOffset? End)> outages)
    {
        _outages = outages;
        Invalidate();
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

        // Fixed 0-based y-axis: latency (or any similar non-negative metric) reads more
        // naturally against a zero floor than an auto-scaled min, and a flat/near-zero line
        // near the top of the plot would otherwise look misleadingly dramatic.
        double dataMin = _points.Min(p => p.Value);
        double dataMax = _points.Max(p => p.Value);
        double axisMax = Math.Max(dataMax, 0.0001);

        int plotWidth = Math.Max(1, Width - (2 * PlotPadding));
        int plotHeight = Math.Max(1, Height - (2 * PlotPadding) - AxisStripHeight);

        DateTimeOffset minTime = _points[0].Timestamp;
        DateTimeOffset maxTime = _points[^1].Timestamp;
        double timeSpanSeconds = Math.Max(1, (maxTime - minTime).TotalSeconds);

        float ToX(DateTimeOffset t) => PlotPadding + (float)((t - minTime).TotalSeconds / timeSpanSeconds * plotWidth);

        PointF ToPoint((DateTimeOffset Timestamp, double Value) p) => new(
            ToX(p.Timestamp),
            PlotPadding + plotHeight - (float)(p.Value / axisMax * plotHeight));

        // X-axis: periodic time labels so the viewer can tell how far back the graph reaches.
        using (var gridPen = new Pen(Color.WhiteSmoke))
        using (var axisTextBrush = new SolidBrush(Color.DimGray))
        using (var axisFont = new Font(Font.FontFamily, 7.5f))
        {
            for (int i = 0; i <= TickCount; i++)
            {
                double fraction = (double)i / TickCount;
                float x = PlotPadding + (float)(fraction * plotWidth);
                g.DrawLine(gridPen, x, PlotPadding, x, PlotPadding + plotHeight);

                DateTimeOffset tickTime = minTime + TimeSpan.FromSeconds(fraction * timeSpanSeconds);
                string tickLabel = tickTime.ToLocalTime().ToString("HH:mm");
                SizeF textSize = g.MeasureString(tickLabel, axisFont);
                float textX = Math.Clamp(x - (textSize.Width / 2), 0, Width - textSize.Width);
                g.DrawString(tickLabel, axisFont, axisTextBrush, textX, PlotPadding + plotHeight + 1);
            }
        }

        if (_outages.Count > 0)
        {
            using var outageBrush = new SolidBrush(Color.FromArgb(60, Color.Firebrick));
            foreach ((DateTimeOffset start, DateTimeOffset? end) in _outages)
            {
                DateTimeOffset bandEnd = end ?? maxTime;
                if (bandEnd < minTime || start > maxTime)
                {
                    continue;
                }

                DateTimeOffset bandStart = start < minTime ? minTime : start;
                bandEnd = bandEnd > maxTime ? maxTime : bandEnd;
                float x1 = ToX(bandStart);
                float x2 = Math.Max(x1 + 1.5f, ToX(bandEnd));
                g.FillRectangle(outageBrush, x1, PlotPadding, x2 - x1, plotHeight);
            }
        }

        if (_points.Count == 1)
        {
            PointF pt = ToPoint(_points[0]);
            using var dotBrush = new SolidBrush(Color.SteelBlue);
            g.FillEllipse(dotBrush, pt.X - 3, pt.Y - 3, 6, 6);
        }
        else
        {
            PointF[] pts = _points.Select(ToPoint).ToArray();
            using var linePen = new Pen(Color.SteelBlue, 1.6f) { LineJoin = LineJoin.Round };
            g.DrawLines(linePen, pts);
        }

        string label = $"{_points[^1].Value:F0} ms (min {dataMin:F0} / max {dataMax:F0} ms)";
        using var textBrush = new SolidBrush(Color.DimGray);
        using var smallFont = new Font(Font.FontFamily, 7.5f);
        SizeF labelSize = g.MeasureString(label, smallFont);
        using var labelBackBrush = new SolidBrush(Color.FromArgb(200, Color.White));
        g.FillRectangle(labelBackBrush, PlotPadding, PlotPadding, labelSize.Width, labelSize.Height);
        g.DrawString(label, smallFont, textBrush, PlotPadding, PlotPadding);
    }
}
