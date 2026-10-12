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

    /// <summary>
    /// Reduces a binary state series (adapter, link - values are exactly 0 or 1) to discrete time
    /// slices rather than a smoothed line.
    ///
    /// LTTB is the wrong tool for this data: it picks the points that form the largest geometric
    /// triangle, which is a question about shape and has nothing to do with "was there ever a
    /// problem in this slice of time" - a brief real disconnect sandwiched between long healthy
    /// stretches is exactly the kind of point LTTB can discard, because it barely changes the
    /// shape of an otherwise flat line. This instead divides the requested time range into
    /// <paramref name="bucketCount"/> equal slices and aggregates each one with "any down reading
    /// wins": a slice is reported down if even one sample in it was down, up only if every sample
    /// was, and left out of the result entirely if it has no samples at all - a real gap in the
    /// data (the app being closed, say) is shown as a gap, never invented as either state.
    ///
    /// The bucket count is deliberately coarse compared to <see cref="Lttb"/>'s roughly-one-per-
    /// pixel target: a handful of wide, distinct segments reads as a sequence of measurements,
    /// while one per pixel on a long healthy stretch is indistinguishable from a single solid bar.
    /// </summary>
    public static IReadOnlyList<MetricPoint> BucketState(
        IReadOnlyList<MetricPoint> points, DateTimeOffset start, DateTimeOffset end, int bucketCount)
    {
        if (points.Count == 0 || bucketCount < 1 || end <= start)
        {
            return points;
        }

        long startTicks = start.UtcTicks;
        long spanTicks = Math.Max(1, end.UtcTicks - startTicks);
        long bucketSpanTicks = Math.Max(1, spanTicks / bucketCount);

        var counts = new int[bucketCount];
        var downCounts = new int[bucketCount];

        foreach (MetricPoint point in points)
        {
            long offset = point.Timestamp.UtcTicks - startTicks;
            if (offset < 0 || offset > spanTicks)
            {
                continue;
            }

            int index = Math.Clamp((int)(offset * bucketCount / (spanTicks + 1)), 0, bucketCount - 1);
            counts[index]++;
            if (point.Value <= 0.5)
            {
                downCounts[index]++;
            }
        }

        var result = new List<MetricPoint>(bucketCount);
        for (int i = 0; i < bucketCount; i++)
        {
            if (counts[i] == 0)
            {
                // No measurements fell in this slice - leave it out rather than guessing a state
                // for it, so the chart shows a true gap instead of a claim nothing backs up.
                continue;
            }

            // 1 = every sample in this slice was up, 0 = every sample was down, 0.5 = a mix - the
            // slice contained both a real disconnect and a recovery, drawn as a distinct colour
            // rather than rounded away in either direction.
            double value = downCounts[i] == 0 ? 1.0 : downCounts[i] == counts[i] ? 0.0 : 0.5;

            // The slice's own fixed midpoint, not a data-weighted centroid. An earlier version
            // summed each point's raw tick count to average them, which overflows a 64-bit long
            // after only a handful of points - a tick count for a date in the 2020s is already
            // around 6*10^17, and long tops out around 9.2*10^18 - and silently dropped or
            // misplaced whichever slice happened to be dense enough to overflow. The midpoint
            // needs no accumulation at all and places every slice at a predictable, evenly-spaced
            // position regardless of how its samples happen to be distributed within it.
            long midpointTicks = startTicks + (i * bucketSpanTicks) + (bucketSpanTicks / 2);
            result.Add(new MetricPoint(new DateTimeOffset(midpointTicks, TimeSpan.Zero), value, null));
        }

        return result;
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
