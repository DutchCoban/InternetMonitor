namespace InternetMonitor.Configuration;

/// <summary>What kind of check a step-7 target actually performs.</summary>
public enum EndpointKind
{
    /// <summary>A full HTTP(S) request - the right choice for a website or a JSON/REST API.</summary>
    Url,

    /// <summary>
    /// A bare TCP connect to a host (an IP address or a domain name) and port, with no protocol
    /// spoken on top - for a database, a mail server, or anything else that does not answer HTTP.
    /// </summary>
    TcpPort,
}

/// <summary>
/// One user-configured step-7 target. <see cref="Kind"/> picks which fields apply: a
/// <see cref="EndpointKind.Url"/> target uses <see cref="Url"/> (and, optionally,
/// <see cref="ExpectedStatusCode"/> for an API that should always answer with a specific code); a
/// <see cref="EndpointKind.TcpPort"/> target uses <see cref="Host"/> and <see cref="Port"/>.
///
/// <see cref="Kind"/> defaults to <see cref="EndpointKind.Url"/> - the enum's first value - so a
/// settings.json written before this type existed still deserializes correctly: every endpoint it
/// contains was a URL target.
/// </summary>
public sealed record EndpointConfig(
    string Id,
    string Name,
    EndpointKind Kind,
    string Url = "",
    string Host = "",
    int Port = 443,
    int? ExpectedStatusCode = null,
    bool Enabled = true)
{
    public static EndpointConfig CreateUrl(string name, string url, int? expectedStatusCode = null) =>
        new(Guid.NewGuid().ToString("N"), name, EndpointKind.Url, Url: url, ExpectedStatusCode: expectedStatusCode);

    public static EndpointConfig CreateTcpPort(string name, string host, int port) =>
        new(Guid.NewGuid().ToString("N"), name, EndpointKind.TcpPort, Host: host, Port: port);
}
