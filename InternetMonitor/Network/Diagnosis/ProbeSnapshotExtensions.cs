using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network.Diagnosis;

public static class ProbeSnapshotExtensions
{
    /// <summary>Flattens every probe's details into a per-probe dictionary, for incident snapshots and export.</summary>
    public static Dictionary<string, Dictionary<string, string>> ToDetailsByProbe(this ProbeSnapshot snapshot)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        void Add(IProbeResult r) => result[r.ProbeId] = new Dictionary<string, string>(r.ToDetails())
        {
            ["Status"] = r.Status.ToString(),
            ["Summary"] = r.Summary,
        };

        Add(snapshot.NetworkInterface);
        Add(snapshot.IpAddress);
        Add(snapshot.Gateway);
        Add(snapshot.Internet);
        Add(snapshot.Dns);
        Add(snapshot.GeneralHttps);
        Add(snapshot.TimeSync);
        foreach ((string name, IProbeResult probeResult) in snapshot.ApplicationEndpoints)
        {
            var details = new Dictionary<string, string>(probeResult.ToDetails())
            {
                ["Status"] = probeResult.Status.ToString(),
                ["Summary"] = probeResult.Summary,
                ["Name"] = name,
            };
            result[probeResult.ProbeId] = details;
        }

        return result;
    }
}
