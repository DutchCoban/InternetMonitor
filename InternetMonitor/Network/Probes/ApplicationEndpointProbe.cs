using InternetMonitor.Configuration;

namespace InternetMonitor.Network.Probes;

/// <summary>
/// Thin adapter from a user-configured <see cref="EndpointConfig"/> to the underlying probe
/// implementation: Https uses the staged DNS/TCP/TLS/HTTP probe, Port uses the generic
/// TCP/UDP reachability probe. Structured so more types can be added later without changing
/// callers.
/// </summary>
public sealed class ApplicationEndpointProbe : IProbe
{
    public string Id { get; }
    public string Category => "Application";
    public string DisplayName { get; }

    private readonly IProbe _inner;

    public ApplicationEndpointProbe(EndpointConfig config)
    {
        Id = $"endpoint:{config.Id}";
        DisplayName = config.Name;
        _inner = config.Type switch
        {
            EndpointType.Https => new HttpsEndpointProbe(Id, Category, config.Url, TimeSpan.FromMilliseconds(config.TimeoutMs)),
            EndpointType.Port => new PortReachabilityProbe(Id, config.Host, config.Port, config.PortProtocol, TimeSpan.FromMilliseconds(config.TimeoutMs)),
            _ => throw new NotSupportedException($"Endpoint type {config.Type} is not supported yet."),
        };
    }

    public Task<IProbeResult> RunAsync(CancellationToken cancellationToken) => _inner.RunAsync(cancellationToken);
}
