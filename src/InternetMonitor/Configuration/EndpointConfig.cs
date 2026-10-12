namespace InternetMonitor.Configuration;

/// <summary>
/// One user-configured step-7 target: a name to show on the step rows and reports, and the HTTPS
/// URL actually checked. Deliberately simpler than v1's EndpointConfig, which also supported a
/// plain TCP/UDP port check - v2's TargetCheck only speaks HTTPS so far (see
/// Core/Steps/Checks/TargetCheck.cs), and a setting with nothing behind it is worse than one that
/// does not exist yet.
/// </summary>
public sealed record EndpointConfig(string Id, string Name, string Url, bool Enabled = true)
{
    public static EndpointConfig Create(string name, string url) =>
        new(Guid.NewGuid().ToString("N"), name, url);
}
