using System.Text;

namespace InternetMonitor.Configuration;

/// <summary>
/// One party this app contacts on its own: where, who runs it, under which law, and the setting
/// that controls it. <paramref name="Host"/> reads the live settings, so the table shows what the
/// app will actually call; the operator and jurisdiction describe the default.
/// </summary>
/// <param name="Id">Names the two text keys: <c>connections.{Id}.name</c> (what the connection is for) and <c>connections.{Id}.sent</c> (what is sent).</param>
/// <param name="SettingKey">The label key of the setting that switches or replaces it, or null when it is always on.</param>
public sealed record OutboundConnection(string Id, Func<AppSettings, string> Host, string Operator, string Jurisdiction, string? SettingKey);

/// <summary>
/// Every address the app contacts without being asked, in one list. The README's "Who this app
/// talks to" table is generated from it (a test keeps the two identical), so nothing the app calls
/// can go unlisted: a new network path is a new row here, or the test fails. The owner chose not
/// to repeat the table inside the app: the README is where people read it.
///
/// The rule behind the defaults: European first, with a long-proven fallback that is only tried
/// when the European address stays silent.
/// </summary>
public static class OutboundConnections
{
    public static readonly IReadOnlyList<OutboundConnection> All =
    [
        new("ping", s => Join(s.ParsedPublicIpTargets()) + " → " + Join(new CheckTargets().FallbackPublicIpTargets), "Quad9 Foundation, DNS4EU (Whalebone); fallback Cloudflare, Google", "CH, EU (CZ); fallback US", "settings.publicIp.targets"),
        new("pingIpv6", _ => Join(new CheckTargets().PublicIpv6Targets) + " → " + Join(new CheckTargets().FallbackPublicIpv6Targets), "Quad9 Foundation, DNS4EU (Whalebone); fallback Cloudflare, Google", "CH, EU (CZ); fallback US", null),
        new("dnsProbe", _ => new CheckTargets().DnsProbeHostname, "RIPE NCC", "NL", null),
        new("resolvers", _ => Join(new CheckTargets().PublicResolvers), "Quad9 Foundation, DNS4EU (Whalebone); then Cloudflare, Google", "CH, EU (CZ); then US", null),
        new("reachability", s => s.ReachabilityUrl, "Volla Systeme GmbH (default)", "DE", "settings.publicIp.reachabilityUrl"),
        new("captivePortal", _ => new CheckTargets().CaptivePortalProbeUrl.Host, "Microsoft", "US", null),
        new("ipv6Test", _ => new CheckTargets().Ipv6CheckUrl.Host, "Microsoft", "US", null),
        new("ntp", s => s.NtpServer + " → " + AppSettings.LegacyNtpServer, "PTB, the German national metrology institute (default); fallback NTP Pool Project", "DE; fallback worldwide", "settings.ntpServer"),
        new("speedTest", s => Join(s.SpeedTestServers.Select(u => new Uri(u).Host)), "CESNET (CZ), GARR (IT), Clouvider (default chain)", "EU; Clouvider is a UK company with the server in NL", "settings.speedTestServer"),
        new("hopOwners", _ => new Uri(Core.Tools.Traceroute.IpOwnershipLookup.ApiBase).Host, "RIPE NCC (RIPEstat)", "NL", "settings.tracerouteLookup"),
        new("ouiList", _ => new Uri(Core.Tools.LanScan.OuiRegistry.ListUrl).Host, "IEEE Registration Authority", "US", "settings.lanScanVendorLookup"),
        new("updateCheck", _ => "api.github.com", "GitHub (Microsoft)", "US", "settings.checkForUpdates"),
        new("updateDownload", _ => "github.com", "GitHub (Microsoft)", "US", "settings.autoUpdateInstall"),
        new("endpoints", s => s.ApplicationEndpoints.Count > 0 ? s.ApplicationEndpoints.Count.ToString() + " configured" : "-", "Your choice", "-", "settings.section.endpoints"),
        new("kuma", s => HostOf(s.KumaPushUrl), "Your choice", "-", "settings.kuma.pushUrl"),
        new("webhook", s => HostOf(s.WebhookUrl), "Your choice", "-", "settings.webhook.url"),
    ];

    /// <summary>
    /// The README table, from the defaults: a header, then one row per connection. The test in
    /// OutboundConnectionsTests compares this with the block between the README's markers.
    /// </summary>
    public static string ToMarkdown(AppSettings settings, Func<string, string> loc)
    {
        var sb = new StringBuilder();
        sb.Append("| ").Append(loc("connections.col.what")).Append(" | ").Append(loc("connections.col.host")).Append(" | ")
          .Append(loc("connections.col.operator")).Append(" | ").Append(loc("connections.col.sent")).Append(" | ")
          .Append(loc("connections.col.switch")).Append(" |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (OutboundConnection c in All)
        {
            sb.Append("| ").Append(loc($"connections.{c.Id}.name"))
              .Append(" | `").Append(c.Host(settings).Replace("|", "/")).Append('`')
              .Append(" | ").Append(c.Operator).Append(" (").Append(c.Jurisdiction).Append(')')
              .Append(" | ").Append(loc($"connections.{c.Id}.sent"))
              .Append(" | ").Append(c.SettingKey is null ? loc("connections.switch.none") : loc(c.SettingKey))
              .Append(" |\n");
        }

        return sb.ToString();
    }

    private static string Join(IEnumerable<object> items) => string.Join(", ", items);

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "-";
}
