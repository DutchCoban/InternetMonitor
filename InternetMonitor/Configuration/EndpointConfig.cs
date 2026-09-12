namespace InternetMonitor.Configuration;

public enum EndpointType
{
    Https,
    Port,
}

public enum PortProtocol
{
    Tcp,
    Udp,
    Both,
}

/// <summary>A user-configured application endpoint to monitor (e.g. a company's own API, or any TCP/UDP port).</summary>
public sealed class EndpointConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public EndpointType Type { get; set; } = EndpointType.Https;

    /// <summary>Used only when <see cref="Type"/> is <see cref="EndpointType.Https"/>.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Used only when <see cref="Type"/> is <see cref="EndpointType.Port"/> - hostname or IP, no scheme/port.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 443;
    public PortProtocol PortProtocol { get; set; } = PortProtocol.Tcp;

    public int TimeoutMs { get; set; } = 5000;
    public bool Enabled { get; set; } = true;
}
