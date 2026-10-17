using InternetMonitor.Configuration;
using Xunit;

namespace InternetMonitor.Tests.Configuration;

/// <summary>The endpoint editor's rules: what is refused with which message, and what a saved endpoint carries.</summary>
public sealed class EndpointInputTests
{
    private static EndpointConfig? Build(
        string name = "API",
        EndpointKind kind = EndpointKind.Url,
        string url = "https://api.example/health",
        int expected = 0,
        IEnumerable<(string, string)>? headers = null,
        string host = "mail.example",
        int port = 993,
        string? editingId = null,
        bool enabled = true,
        string? expectError = null)
    {
        EndpointConfig? result = EndpointInput.TryBuild(editingId, enabled, name, kind, url, expected, headers ?? [], host, port, out string? errorKey);
        Assert.Equal(expectError, errorKey);
        return result;
    }

    [Fact]
    public void A_blank_name_is_refused_whatever_the_kind()
    {
        Assert.Null(Build(name: "  ", expectError: "settings.endpoint.error.name"));
        Assert.Null(Build(name: "", kind: EndpointKind.Ping, expectError: "settings.endpoint.error.name"));
    }

    [Theory]
    [InlineData("ftp://files.example/")]
    [InlineData("api.example/health")]
    [InlineData("")]
    public void A_url_must_be_absolute_http_or_https(string url)
    {
        Assert.Null(Build(url: url, expectError: "settings.endpoint.error.url"));
    }

    [Fact]
    public void A_url_endpoint_keeps_its_status_and_named_headers_and_drops_blank_rows()
    {
        EndpointConfig? config = Build(
            name: " API ",
            url: " https://api.example/health ",
            expected: 204,
            headers: [("X-Key", "secret"), ("   ", "ignored"), ("Accept", "application/json")]);

        Assert.NotNull(config);
        Assert.Equal(("API", EndpointKind.Url, "https://api.example/health", 204), (config.Name, config.Kind, config.Url, config.ExpectedStatusCode));
        Assert.Equal(["X-Key", "Accept"], config.Headers.Select(h => h.Name));
        Assert.Equal(32, config.Id.Length);
        Assert.True(config.Enabled);

        Assert.Null(Build(expected: 0)!.ExpectedStatusCode);
    }

    [Fact]
    public void Ping_and_tcp_endpoints_need_a_host_and_only_tcp_keeps_the_port()
    {
        Assert.Null(Build(kind: EndpointKind.Ping, host: " ", expectError: "settings.endpoint.error.host"));

        EndpointConfig? ping = Build(kind: EndpointKind.Ping, host: " printer.local ", port: 993);
        Assert.Equal((EndpointKind.Ping, "printer.local"), (ping!.Kind, ping.Host));
        Assert.NotEqual(993, ping.Port);

        EndpointConfig? tcp = Build(kind: EndpointKind.TcpPort, host: "mail.example", port: 993);
        Assert.Equal((EndpointKind.TcpPort, "mail.example", 993), (tcp!.Kind, tcp.Host, tcp.Port));
    }

    [Fact]
    public void Editing_keeps_the_id_and_the_enabled_flag()
    {
        EndpointConfig? edited = Build(editingId: "abc123", enabled: false);

        Assert.Equal("abc123", edited!.Id);
        Assert.False(edited.Enabled);
    }
}
