namespace InternetMonitor.Configuration;

/// <summary>
/// The check targets a cycle runs with, built from the settings as they are now: thresholds, the
/// adapter choice, the user's addresses and every enabled application endpoint on top of the
/// built-in ones. Pure, so the mapping is tested without a host.
/// </summary>
internal static class TargetsFromSettings
{
    internal static CheckTargets Build(AppSettings settings, CheckTargets defaults)
    {
        List<EndpointConfig> endpoints = settings.ApplicationEndpoints;
        IReadOnlyList<System.Net.IPAddress> publicIpTargets = settings.ParsedPublicIpTargets();
        (int warningMs, int errorMs) = settings.LatencyThresholds;

        return defaults with
        {
            PublicIpTargets = publicIpTargets,
            CustomDnsServers = settings.ParsedCustomDnsServers(),
            LatencyWarningMs = warningMs,
            LatencyErrorMs = errorMs,
            WeakWifiSignalPercent = settings.WeakWifiSignalPercent,
            AdapterMode = settings.AdapterMode,
            AdapterId = settings.AdapterId.Length > 0 ? settings.AdapterId : null,
            AdapterName = settings.AdapterName.Length > 0 ? settings.AdapterName : null,
            Targets =
            [
                .. defaults.Targets,
                .. endpoints.Where(e => e.Enabled).Select(e => e.Kind switch
                {
                    EndpointKind.TcpPort => new TargetEndpoint(e.Name, Url: "", TargetKind.TcpPort, Host: e.Host, Port: e.Port) { Id = e.Id },
                    EndpointKind.Ping => new TargetEndpoint(e.Name, Url: "", TargetKind.Ping, Host: e.Host) { Id = e.Id },
                    _ => new TargetEndpoint(e.Name, e.Url, TargetKind.Url, ExpectedStatusCode: e.ExpectedStatusCode, Headers: e.Headers) { Id = e.Id },
                }),
            ],
        };
    }
}
