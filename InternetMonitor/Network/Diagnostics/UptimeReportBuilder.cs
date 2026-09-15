using InternetMonitor.Network.Diagnosis;

namespace InternetMonitor.Network.Diagnostics;

public sealed record UptimeReport(
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    double UptimePercentage,
    TimeSpan Downtime,
    int IncidentCount);

/// <summary>
/// Computes an uptime percentage over a period from already-recorded incidents, honoring which
/// items the user has excluded (Settings > SLA Report - see AppSettings.SlaExcludedClassifications/
/// SlaExcludedEndpointIds). Pure computation, no I/O - incidents never overlap in time by
/// construction (IncidentTracker only ever tracks one <c>_current</c> incident at a time), so
/// summing each counted incident's overlap with the period is safe without interval-merging.
/// </summary>
public static class UptimeReportBuilder
{
    public static UptimeReport Compute(
        IReadOnlyList<Incident> incidents,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        IReadOnlyCollection<DiagnosisClassification> excludedClassifications,
        IReadOnlyCollection<string> excludedEndpointIds)
    {
        TimeSpan downtime = TimeSpan.Zero;
        int countedIncidents = 0;

        foreach (Incident incident in incidents)
        {
            if (excludedClassifications.Contains(incident.Classification))
            {
                continue;
            }

            // Application incidents can implicate several endpoints at once (AffectedProbeIds).
            // Only skip the incident if every implicated endpoint has been excluded - one
            // included endpoint is enough for the incident to still count.
            if (incident.Classification == DiagnosisClassification.Application)
            {
                bool anyEndpointIncluded = incident.AffectedProbeIds.Any(probeId =>
                    !probeId.StartsWith("endpoint:", StringComparison.Ordinal)
                    || !excludedEndpointIds.Contains(probeId["endpoint:".Length..]));
                if (!anyEndpointIncluded)
                {
                    continue;
                }
            }

            DateTimeOffset incidentEnd = incident.EndUtc ?? periodEnd;
            DateTimeOffset overlapStart = incident.StartUtc > periodStart ? incident.StartUtc : periodStart;
            DateTimeOffset overlapEnd = incidentEnd < periodEnd ? incidentEnd : periodEnd;
            if (overlapEnd <= overlapStart)
            {
                continue;
            }

            downtime += overlapEnd - overlapStart;
            countedIncidents++;
        }

        TimeSpan periodLength = periodEnd - periodStart;
        double uptimePercentage = periodLength <= TimeSpan.Zero
            ? 100.0
            : Math.Clamp(100.0 * (1 - (downtime.TotalSeconds / periodLength.TotalSeconds)), 0, 100);

        return new UptimeReport(periodStart, periodEnd, uptimePercentage, downtime, countedIncidents);
    }
}
