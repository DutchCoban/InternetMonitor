using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.History;

/// <summary>
/// What a finished cycle writes into the history: each step's chartable number, the Wi-Fi signal,
/// IPv6, every configured endpoint's round trip, the metered flag, the traffic, and whether an
/// incident is open. Lifted out of the host so the bookkeeping is tested over a store of its own.
/// </summary>
internal static class HistoryRecorder
{
    /// <summary>
    /// Writes each step's one chartable number into its series. Runs on the pipeline's thread
    /// rather than the UI's: it is pure bookkeeping, and the UI thread has no business waiting on
    /// a lock that a flush might be holding.
    /// </summary>
    internal static void Record(MetricStore history, StepSnapshot snapshot)
    {
        foreach (StepResult result in snapshot.All)
        {
            if (result.ChartValue is not { } value || SeriesNames.For(result.Step) is not { } series)
            {
                continue;
            }

            // Skipped steps measured nothing. Recording a zero for them would draw a cliff on the
            // chart that looks like a measurement rather than an absence.
            if (result.State == StepState.Skipped)
            {
                continue;
            }

            history.Sample(series, result.TimestampUtc, value);
        }

        // The Wi-Fi signal is not step 2's chart value (that stays the 0/1 link state), so it gets
        // its own series. The access point goes along as metadata: the History chart marks where it
        // changed, which is how a roam between access points becomes visible after the fact.
        if (snapshot[StepId.Link] is { State: not StepState.Skipped } link
            && link.Facts is LinkFacts { Wifi.SignalPercent: { } signal } facts)
        {
            history.Sample(SeriesNames.WifiSignal, link.TimestampUtc, signal, facts.Wifi.Bssid);
        }

        if (snapshot[StepId.Target]?.Facts is TargetFacts targetFacts)
        {
            if (targetFacts.Ipv6Reachable is { } ipv6)
            {
                history.Sample(SeriesNames.Ipv6, snapshot.TakenUtc, ipv6 ? 1 : 0);
            }

            // Every configured endpoint gets its own series of round trips, so the History page
            // can chart a named target the way it charts the built-in steps. An unreachable one
            // records nothing - a gap in the line, the same as a failed step.
            foreach (TargetEndpointResult endpoint in targetFacts.Endpoints)
            {
                if (endpoint.EndpointId is { } id && endpoint.Reachable && (endpoint.Timings?.Total.TotalMilliseconds ?? endpoint.ConnectMs) is { } ms)
                {
                    history.Sample(SeriesNames.ForEndpoint(id), snapshot.TakenUtc, Math.Round(ms));
                }
            }
        }

        // Whether the line was metered, so the provider report can tell time spent on a phone's
        // hotspot from time on the line it is about.
        if (snapshot[StepId.Link]?.Facts is LinkFacts { Cost.Metered: { } metered })
        {
            history.Sample(SeriesNames.Metered, snapshot.TakenUtc, metered ? 1 : 0);
        }

        if (snapshot.Throughput is { } traffic)
        {
            history.Sample(SeriesNames.TrafficDown, snapshot.TakenUtc, Math.Round(traffic.DownMbps, 2));
            history.Sample(SeriesNames.TrafficUp, snapshot.TakenUtc, Math.Round(traffic.UpMbps, 2));
        }
    }

    /// <summary>
    /// Whether an incident is open, as a history series (see <see cref="SeriesNames.Outage"/>).
    /// Sampled like every other series, but a change is recorded the moment it happens, so an
    /// outage's start and end are exact rather than up to one sample interval late. Returns the
    /// state to remember for the next call.
    /// </summary>
    internal static string? RecordOutage(MetricStore history, Incident? active, DateTimeOffset at, string? lastState)
    {
        string? state = active is null ? null : $"{active.Cause.Key()}|{active.Id}";
        if (state != lastState)
        {
            history.Record(SeriesNames.Outage, at, active is null ? 0 : 1, state);
            return state;
        }

        history.Sample(SeriesNames.Outage, at, active is null ? 0 : 1, state);
        return state;
    }
}
