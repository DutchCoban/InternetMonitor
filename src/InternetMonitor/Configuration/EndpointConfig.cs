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

/// <summary>One extra header sent with a <see cref="EndpointKind.Url"/> check - an API key or an Authorization token, most often.</summary>
public sealed record EndpointHeader(string Name, string Value);

/// <summary>
/// One user-configured step-7 target. <see cref="Kind"/> picks which fields apply: a
/// <see cref="EndpointKind.Url"/> target uses <see cref="Url"/> (and, optionally,
/// <see cref="ExpectedStatusCode"/> for an API that should always answer with a specific code, and
/// <see cref="Headers"/> for one that needs an API key or auth token to answer at all); a
/// <see cref="EndpointKind.TcpPort"/> target uses <see cref="Host"/> and <see cref="Port"/>.
///
/// <see cref="Kind"/> defaults to <see cref="EndpointKind.Url"/> - the enum's first value - so a
/// settings.json written before this type existed still deserializes correctly: every endpoint it
/// contains was a URL target. <see cref="Headers"/> defaults to empty for the same reason - an
/// endpoint saved before headers existed simply has none.
/// </summary>
public sealed record EndpointConfig(
    string Id,
    string Name,
    EndpointKind Kind,
    string Url = "",
    string Host = "",
    int Port = 443,
    int? ExpectedStatusCode = null,
    bool Enabled = true,
    IReadOnlyList<EndpointHeader>? Headers = null)
{
    // The positional parameter is nullable because a default parameter value has to be a compile-
    // time constant, which a fresh empty list is not - this property (which shadows the
    // auto-generated one, since it shares the parameter's name) is what turns a null argument into
    // a real empty list everywhere else in the app reads Headers.
    public IReadOnlyList<EndpointHeader> Headers { get; init; } = Headers ?? [];

    public static EndpointConfig CreateUrl(string name, string url, int? expectedStatusCode = null) =>
        new(Guid.NewGuid().ToString("N"), name, EndpointKind.Url, Url: url, ExpectedStatusCode: expectedStatusCode);

    public static EndpointConfig CreateTcpPort(string name, string host, int port) =>
        new(Guid.NewGuid().ToString("N"), name, EndpointKind.TcpPort, Host: host, Port: port);
}
