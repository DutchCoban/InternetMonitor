using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

/// <summary>
/// Shared "Key: Value" formatting for a probe result's <see cref="IProbeResult.ToDetails"/>
/// dictionary plus its <see cref="IProbeResult.ErrorDetail"/>, if any - used by both the
/// Diagnostics screen's details pane and the per-check history popup so the two never drift out
/// of sync with each other (they used to duplicate this logic independently).
/// </summary>
public static class ProbeDetailsFormatter
{
    public static string Format(IProbeResult result)
    {
        var lines = result.ToDetails().Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        if (!string.IsNullOrEmpty(result.ErrorDetail))
        {
            lines.Add(LocalizationManager.Instance.Format("diag.details.error", result.ErrorDetail));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
