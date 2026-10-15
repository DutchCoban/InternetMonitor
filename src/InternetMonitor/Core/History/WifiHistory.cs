using System.Text.Json;

namespace InternetMonitor.Core.History;

/// <summary>
/// Reads the access point back out of a <see cref="SeriesNames.WifiSignal"/> point's metadata.
///
/// The series is shared with v1, which recorded it under the same name and in the same unit, so an
/// upgrade keeps weeks of signal history on the chart. The metadata differs, though: v1 stored a
/// URL-escaped JSON object (network name, access point, link speed, PHY type), v2 stores just the
/// access point. Comparing raw metadata would mark a "roam" at the upgrade moment, and at every
/// change of v1's link speed - so both shapes are reduced to the access point alone first.
/// </summary>
public static class WifiHistory
{
    /// <summary>
    /// The access point (BSSID, lower case) a Wi-Fi signal point was measured on. v2 stores the BSSID
    /// itself as metadata; v1 stored a URL-encoded JSON object, of which only the BSSID is used here,
    /// so a v1 change of link speed is not mistaken for a switch of access point. Null when unknown.
    /// </summary>
    public static string? AccessPointOf(string? metadata)
    {
        if (metadata is not { Length: > 0 })
        {
            return null;
        }

        if (!metadata.StartsWith("%7B", StringComparison.OrdinalIgnoreCase) && !metadata.StartsWith('{'))
        {
            return metadata.ToLowerInvariant();
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(Uri.UnescapeDataString(metadata));
            return json.RootElement.TryGetProperty("Bssid", out JsonElement bssid) && bssid.GetString() is { Length: > 0 } value
                ? value.ToLowerInvariant()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
