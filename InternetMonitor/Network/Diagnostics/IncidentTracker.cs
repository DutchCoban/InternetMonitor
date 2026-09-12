using InternetMonitor.Network.Diagnosis;

namespace InternetMonitor.Network.Diagnostics;

/// <summary>
/// Groups a run of consecutive non-healthy diagnosis cycles into a single incident instead of
/// one record per failed probe or per tick. Keys off the diagnosis engine's overall
/// classification (not per-probe status) so e.g. "gateway+internet+dns all down" becomes one
/// incident with one root-cause diagnosis. Debounced (default 2 consecutive cycles each way)
/// so a brief flap doesn't open/close many tiny incidents.
/// </summary>
public sealed class IncidentTracker
{
    private const int OpenThreshold = 2;
    private const int ResolveThreshold = 2;

    private readonly IncidentStore _store;
    private int _consecutiveBad;
    private int _consecutiveHealthy;
    private Incident? _current;

    /// <summary>
    /// Resumes tracking of any incident that was still open (EndUtc null) when the app last
    /// closed. Without this, <see cref="_current"/> would start null on every restart even
    /// though the persisted store still has the incident marked Active - permanently orphaning
    /// it, since nothing would ever set its EndUtc again. If more than one ended up open (only
    /// possible from that older bug), only the most recent is resumed; the rest are closed out
    /// now since their true end time can no longer be known.
    /// </summary>
    public IncidentTracker(IncidentStore store)
    {
        _store = store;

        List<Incident> stillOpen = _store.All.Where(i => i.EndUtc is null).OrderByDescending(i => i.StartUtc).ToList();
        if (stillOpen.Count > 0)
        {
            _current = stillOpen[0];
            foreach (Incident orphan in stillOpen.Skip(1))
            {
                orphan.EndUtc = DateTimeOffset.UtcNow;
                orphan.Status = IncidentStatus.Resolved;
                _store.Update(orphan);
            }
        }
    }

    public Incident? CurrentIncident => _current;

    public void Observe(DiagnosisResult diagnosis, ProbeSnapshot snapshot)
    {
        bool healthy = diagnosis.Classification == DiagnosisClassification.Healthy;

        if (healthy)
        {
            _consecutiveBad = 0;
            _consecutiveHealthy++;

            if (_current is not null && _consecutiveHealthy >= ResolveThreshold)
            {
                _current.EndUtc = DateTimeOffset.UtcNow;
                _current.Status = IncidentStatus.Resolved;
                _current.SnapshotAtResolution = snapshot.ToDetailsByProbe();
                _store.Update(_current);
                _current = null;
            }

            return;
        }

        _consecutiveHealthy = 0;
        _consecutiveBad++;

        if (_current is null)
        {
            if (_consecutiveBad >= OpenThreshold)
            {
                _current = new Incident
                {
                    Id = _store.NextId(),
                    Classification = diagnosis.Classification,
                    StartUtc = DateTimeOffset.UtcNow,
                    Status = IncidentStatus.Active,
                    Diagnosis = diagnosis.Explanation,
                    AffectedProbeIds = diagnosis.AffectedProbeIds.ToList(),
                    SnapshotAtStart = snapshot.ToDetailsByProbe(),
                };
                _store.Add(_current);
            }
        }
        else
        {
            // Keep the ongoing incident's diagnosis current in case the dominant cause shifts
            // slightly while it's still active - same Id throughout its lifecycle.
            _current.Diagnosis = diagnosis.Explanation;
            _current.Classification = diagnosis.Classification;
            _store.Update(_current);
        }
    }
}
