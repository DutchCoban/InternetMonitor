using System.Text;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Diagnosis;

/// <summary>
/// Builds the plain-text report behind the Diagnostics page's "Copy diagnostics" button - the
/// same purpose v1's DiagnosticReportBuilder served: something a user can paste straight into a
/// support ticket or chat without a screenshot, with every measurement in it rather than just the
/// headline.
///
/// A pure function over a snapshot, like the rest of this layer: no clock reads beyond what the
/// snapshot already carries, no I/O, so it is as straightforward to unit-test as
/// RootCauseAnalyzer or PlainLanguage are.
/// </summary>
public static class DiagnosticReportBuilder
{
    public static string Build(StepSnapshot snapshot, RootCauseResult diagnosis)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        var sb = new StringBuilder();

        sb.AppendLine(loc.Get("report.title"));
        sb.AppendLine(loc.Format("report.generated", snapshot.TakenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")));
        sb.AppendLine();

        string headline = snapshot.FirstFailure?.Headline ?? loc.Get($"cause.{diagnosis.Cause.Key()}");
        sb.AppendLine(loc.Format("report.status", headline));
        sb.AppendLine();

        sb.AppendLine(loc.Get("report.steps"));
        foreach (StepResult result in snapshot.All.OrderBy(r => (int)r.Step))
        {
            string stepName = loc.Get($"step.{result.Step.Key()}.name");
            string duration = result.State.IsResolved() && result.Duration > TimeSpan.Zero
                ? $" ({result.Duration.TotalMilliseconds:F0} ms)"
                : string.Empty;

            sb.AppendLine($"{(int)result.Step}. {stepName} - {result.State}: {result.Headline}{duration}");

            if (!string.IsNullOrEmpty(result.Detail))
            {
                sb.AppendLine($"   {result.Detail}");
            }

            foreach (Measurement measurement in result.Measurements)
            {
                sb.AppendLine($"   - {measurement.Label}: {measurement.Value}");
            }
        }

        return sb.ToString();
    }
}
