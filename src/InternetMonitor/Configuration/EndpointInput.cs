namespace InternetMonitor.Configuration;

/// <summary>
/// What the endpoint editor makes of its fields: a configuration, or the key of the error to show
/// instead. Pure, so the rules are tested without a window.
/// </summary>
internal static class EndpointInput
{
    internal static EndpointConfig? TryBuild(
        string? editingId,
        bool enabled,
        string nameText,
        EndpointKind kind,
        string urlText,
        int expectedStatus,
        IEnumerable<(string Name, string Value)> headerRows,
        string hostText,
        int port,
        out string? errorKey)
    {
        errorKey = null;
        string name = nameText.Trim();
        if (name.Length == 0)
        {
            errorKey = "settings.endpoint.error.name";
            return null;
        }

        string id = editingId ?? Guid.NewGuid().ToString("N");
        if (kind == EndpointKind.Url)
        {
            string url = urlText.Trim();
            bool validScheme = Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);
            if (!validScheme)
            {
                errorKey = "settings.endpoint.error.url";
                return null;
            }

            int? expected = expectedStatus == 0 ? null : expectedStatus;

            // Rows with no name are dropped rather than rejected - a blank row the user added and
            // left empty is not a mistake worth an error message, it is just not a header yet.
            List<EndpointHeader> headers = [.. headerRows
                .Where(r => r.Name.Trim().Length > 0)
                .Select(r => new EndpointHeader(r.Name.Trim(), r.Value))];

            return new EndpointConfig(id, name, EndpointKind.Url, Url: url, ExpectedStatusCode: expected, Enabled: enabled, Headers: headers);
        }

        string host = hostText.Trim();
        if (host.Length == 0)
        {
            errorKey = "settings.endpoint.error.host";
            return null;
        }

        return kind == EndpointKind.Ping
            ? new EndpointConfig(id, name, EndpointKind.Ping, Host: host, Enabled: enabled)
            : new EndpointConfig(id, name, EndpointKind.TcpPort, Host: host, Port: port, Enabled: enabled);
    }
}
