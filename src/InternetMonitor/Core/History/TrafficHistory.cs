using System.Globalization;

namespace InternetMonitor.Core.History;

/// <summary>
/// The peak a traffic point carries in its metadata. A point's value is the average rate over the
/// recording interval that ends at its timestamp; its metadata, "peak=12.34", is the fastest single
/// cycle inside that interval, which is what a burst just before a fault looks like. Points written
/// before this existed have no metadata, and read back with the value standing in for the peak.
/// </summary>
internal static class TrafficHistory
{
    private const string Prefix = "peak=";

    public static string Metadata(double peakMbps) => Prefix + peakMbps.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>The peak stored on a point, or null when the metadata is missing or not a peak.</summary>
    public static double? PeakOf(string? metadata) =>
        metadata is { Length: > 5 }
        && metadata.StartsWith(Prefix, StringComparison.Ordinal)
        && double.TryParse(metadata.AsSpan(Prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double peak)
            ? peak
            : null;

    /// <summary>A point's peak: the stored one, or its own value for a point written without one.</summary>
    public static double PeakOf(MetricPoint point) => PeakOf(point.Metadata) ?? point.Value;
}
