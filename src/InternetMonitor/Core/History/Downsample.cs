namespace InternetMonitor.Core.History;

/// <summary>
/// Reduces a series to roughly one point per pixel before it is drawn.
///
/// This is what makes the History page usable at long retention. Thirty days at one sample every
/// fifteen seconds is about 170,000 points; v1 drew every one of them, so panning a month of
/// history meant GDI+ stroking a polyline with six figures of vertices on every mouse move. The
/// chart is a thousand pixels wide, so all but about a thousand of those points are drawn on top
/// of each other.
///
/// The algorithm is Largest-Triangle-Three-Buckets, chosen over simple averaging or stride
/// sampling because it keeps the *shape*. A latency spike is one tall point among hundreds of flat
/// ones: averaging flattens it away and stride sampling hits it only by luck, and in a tool whose
/// entire purpose is noticing outages, silently deleting the outage from its own graph is the one
/// unacceptable failure. LTTB picks, from each bucket, the point forming the largest triangle with
/// its neighbours, which is precisely a preference for the points that change direction.
/// </summary>
public static class Downsample
{
    /// <summary>
    /// Returns at most <paramref name="threshold"/> points, always including the first and last so
    /// the series still starts and ends where it really does.
    /// </summary>
    public static IReadOnlyList<MetricPoint> Lttb(IReadOnlyList<MetricPoint> points, int threshold)
    {
        if (threshold < 3 || points.Count <= threshold)
        {
            return points;
        }

        var sampled = new List<MetricPoint>(threshold) { points[0] };

        // Buckets span everything between the fixed first and last points.
        double bucketSize = (double)(points.Count - 2) / (threshold - 2);
        int previousIndex = 0;

        for (int i = 0; i < threshold - 2; i++)
        {
            int bucketStart = (int)Math.Floor((i + 1) * bucketSize) + 1;
            int bucketEnd = (int)Math.Floor((i + 2) * bucketSize) + 1;
            bucketEnd = Math.Min(bucketEnd, points.Count - 1);

            // The next bucket's average stands in for "where the line is heading", which is what
            // makes the triangle meaningful.
            int nextStart = bucketEnd;
            int nextEnd = Math.Min((int)Math.Floor((i + 3) * bucketSize) + 1, points.Count);
            (double avgX, double avgY) = Average(points, nextStart, nextEnd);

            MetricPoint previous = points[previousIndex];
            double previousX = previous.Timestamp.UtcTicks;

            double largestArea = -1;
            int chosen = bucketStart;

            for (int j = bucketStart; j < bucketEnd; j++)
            {
                double area = Math.Abs(
                    ((previousX - avgX) * (points[j].Value - previous.Value))
                    - ((previousX - points[j].Timestamp.UtcTicks) * (avgY - previous.Value)));

                if (area > largestArea)
                {
                    largestArea = area;
                    chosen = j;
                }
            }

            sampled.Add(points[chosen]);
            previousIndex = chosen;
        }

        sampled.Add(points[^1]);
        return sampled;
    }

    private static (double X, double Y) Average(IReadOnlyList<MetricPoint> points, int start, int end)
    {
        if (end <= start)
        {
            return (points[^1].Timestamp.UtcTicks, points[^1].Value);
        }

        double x = 0;
        double y = 0;
        for (int i = start; i < end; i++)
        {
            x += points[i].Timestamp.UtcTicks;
            y += points[i].Value;
        }

        int count = end - start;
        return (x / count, y / count);
    }

    /// <summary>Min, average, max and 95th percentile of a series, for the chart's footer.</summary>
    public static (double Min, double Average, double Max, double P95)? Summarize(IReadOnlyList<MetricPoint> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        double[] values = [.. points.Select(p => p.Value).Order()];
        double sum = 0;
        foreach (MetricPoint point in points)
        {
            sum += point.Value;
        }

        // Nearest-rank percentile: for a handful of points this is the honest answer, where
        // interpolation would invent a value that was never measured.
        int rank = (int)Math.Ceiling(0.95 * values.Length) - 1;
        return (values[0], sum / values.Length, values[^1], values[Math.Clamp(rank, 0, values.Length - 1)]);
    }
}
