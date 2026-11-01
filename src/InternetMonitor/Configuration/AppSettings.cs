using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Configuration;

/// <summary>Which earlier period the provider report sets the chosen one against.</summary>
public enum ReportComparison
{
    None,

    /// <summary>The same number of days directly before the period.</summary>
    PreviousPeriod,

    /// <summary>The same period one week earlier - weekdays have their own patterns.</summary>
    WeekEarlier,

    /// <summary>The same period one month earlier.</summary>
    MonthEarlier,
}

/// <summary>Which adapter the checks describe - see <see cref="Net.InterfaceSelector.Choose"/>.</summary>
public enum AdapterMode
{
    /// <summary>The adapter Windows routes internet traffic over, falling back to the most plausible physical one.</summary>
    Automatic,

    /// <summary>The chosen adapter while it is up; automatic when it is not.</summary>
    Preferred,

    /// <summary>Always the chosen adapter, even when Windows routes over another; step 1 fails when it is gone.</summary>
    Fixed,
}

/// <summary>How the app tells the user a problem began or ended.</summary>
public enum NotificationStyle
{
    /// <summary>The app's own pop-up in the corner of the screen, gone after a few seconds.</summary>
    Popup,

    /// <summary>A Windows notification from the tray icon: through the notification centre, honouring "do not disturb".</summary>
    Windows,
}

/// <summary>
/// One network-health condition's threshold: fires when more than <see cref="MaxOccurrences"/>
/// occurrences are seen within <see cref="WindowMinutes"/> minutes. <see cref="Enabled"/> is
/// per-condition because a user may want TTL errors flagged without caring about
/// retransmissions, say.
/// </summary>
public sealed record NetworkHealthThreshold(bool Enabled, int MaxOccurrences, int WindowMinutes);

/// <summary>
/// The on-disk settings model, read from and written to the same
/// %AppData%\InternetMonitor\settings.json that v1 used. Unknown properties in an existing v1
/// file are ignored by the deserialiser and new v2 properties simply take their defaults, so an
/// upgrade needs no migration step for anything except values whose meaning actually changed.
///
/// <see cref="Load"/> never throws: a corrupt or unreadable file yields defaults, because an app
/// that refuses to start because of a bad settings file is worse than one that starts with
/// defaults and lets the user fix it.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private int _foregroundIntervalSeconds = 3;
    private int _backgroundIntervalSeconds = 15;

    // ---- General -----------------------------------------------------------------------------

    /// <summary>English unless chosen otherwise - also what a damaged settings file falls back to; a first start picks the Windows display language instead (see <see cref="Load"/>).</summary>
    public string Language { get; set; } = "en";

    /// <summary>System (follow Windows), Light or Dark.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public bool AutoStartWithWindows { get; set; }

    /// <summary>Which adapter the checks describe. The chosen adapter's Windows id and, for the message when it is gone, its name.</summary>
    public AdapterMode AdapterMode { get; set; } = AdapterMode.Automatic;

    public string AdapterId { get; set; } = string.Empty;

    public string AdapterName { get; set; } = string.Empty;

    /// <summary>The series the History page showed last, so it opens where the user left it rather than on the default.</summary>
    public string HistorySeriesId { get; set; } = "internet";

    /// <summary>
    /// A brief pop-up when a new incident opens. On by default, matching v1's equivalent
    /// (ShowOutagePopups) - a connectivity tool that stays silent about a connectivity problem
    /// defeats its own purpose.
    /// </summary>
    public bool ShowIncidentPopups { get; set; } = true;

    public NotificationStyle NotificationStyle { get; set; } = NotificationStyle.Popup;

    // ---- Cadence -----------------------------------------------------------------------------

    /// <summary>
    /// How often the step pipeline runs while a window is open. Clamped to 1-10s: below a second
    /// the checks would overlap their own timeouts, and above ten the dashboard stops feeling live,
    /// at which point the background interval is the right setting to change instead.
    /// </summary>
    public int ForegroundIntervalSeconds
    {
        get => _foregroundIntervalSeconds;
        set => _foregroundIntervalSeconds = Math.Clamp(value, 1, 10);
    }

    /// <summary>
    /// How often the pipeline runs when only the tray icon is live. Clamped to 5-300s. This is
    /// also the interval at which history points are recorded, regardless of the foreground rate -
    /// see MetricStore for why the two are deliberately decoupled.
    /// </summary>
    public int BackgroundIntervalSeconds
    {
        get => _backgroundIntervalSeconds;
        set => _backgroundIntervalSeconds = Math.Clamp(value, 5, 300);
    }

    // ---- Thresholds ---------------------------------------------------------------------------

    private int _latencyWarningMs = 300;
    private int _latencyErrorMs = 1000;
    private int _weakWifiSignalPercent = 40;

    /// <summary>A round trip from here counts as slow (a warning). Clamped to 50-5000 ms; always below <see cref="LatencyErrorMs"/>.</summary>
    public int LatencyWarningMs
    {
        get => _latencyWarningMs;
        set => _latencyWarningMs = Math.Clamp(value, 50, 5000);
    }

    /// <summary>A round trip from here counts as severe. Clamped to 100-10000 ms.</summary>
    public int LatencyErrorMs
    {
        get => _latencyErrorMs;
        set => _latencyErrorMs = Math.Clamp(value, 100, 10_000);
    }

    /// <summary>Below this Windows signal quality step 2 warns about a weak Wi-Fi signal. Clamped to 10-80%.</summary>
    public int WeakWifiSignalPercent
    {
        get => _weakWifiSignalPercent;
        set => _weakWifiSignalPercent = Math.Clamp(value, 10, 80);
    }

    /// <summary>The thresholds as a <see cref="CheckTargets"/> would carry them, with the warning kept below the error.</summary>
    public (int WarningMs, int ErrorMs) LatencyThresholds =>
        (Math.Min(LatencyWarningMs, LatencyErrorMs - 1), LatencyErrorMs);

    // ---- History ------------------------------------------------------------------------------

    /// <summary>
    /// The only retention choices offered. Capped at 90 days: every point is kept at full
    /// resolution in memory, and beyond that a machine that never sleeps would hold several
    /// hundred thousand points per series for charts nobody zooms that far into.
    /// </summary>
    public static readonly int[] AllowedHistoryRetentionDays = [7, 30, 60, 90];

    private int _historyRetentionDays = 30;

    /// <summary>How long per-check history is kept. Incidents are never pruned - they are small, and they are the evidence.</summary>
    public int HistoryRetentionDays
    {
        get => _historyRetentionDays;
        set => _historyRetentionDays = Array.IndexOf(AllowedHistoryRetentionDays, value) >= 0 ? value : _historyRetentionDays;
    }

    // ---- Window -------------------------------------------------------------------------------

    /// <summary>Last main-window size, so reopening lands where the user left it. Zero means "use the default".</summary>
    public int WindowWidth { get; set; }

    public int WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }


    // ---- Tools --------------------------------------------------------------------------------

    /// <summary>
    /// Whether traceroute looks up who owns each hop. On by default because it is most of the
    /// value of the feature, but switchable because it is the one thing in the app that sends
    /// third parties (Team Cymru, the RIR RDAP services) a list of addresses on your route.
    /// Private and bogon ranges are never looked up either way.
    /// </summary>
    public bool TracerouteOwnerLookup { get; set; } = true;

    /// <summary>
    /// Whether a provider-side incident (internet, DNS, target) records a traceroute to the
    /// reachability target the moment it opens - where the packets stopped is the evidence a help
    /// desk asks for. On by default; it is one trace per incident, not per cycle.
    /// </summary>
    public bool TracerouteOnIncident { get; set; } = true;

    /// <summary>
    /// Whether the LAN scanner looks up each device's manufacturer. Same reasoning and the same
    /// default as <see cref="TracerouteOwnerLookup"/>: on by default because it is most of the
    /// value of the feature, switchable because it is the only thing in the scanner that sends a
    /// third party anything (a MAC prefix, never the full device-specific address) about the
    /// user's own network.
    /// </summary>
    public bool LanScanVendorLookup { get; set; } = true;

    /// <summary>
    /// Whether the LAN scanner also tries a handful of common TCP ports for addresses that did not
    /// answer a ping. On by default, unlike the two lookups above: this sends nothing to a third
    /// party, it only talks to devices already on the user's own network, so the only trade-off is
    /// scan time versus completeness - a device that ignores ICMP but has an open port would
    /// otherwise never show up at all.
    /// </summary>
    public bool LanScanTcpFallback { get; set; } = true;

    // ---- Speed test ---------------------------------------------------------------------------

    /// <summary>Whether the Speed Test also runs on its own on a schedule, every <see cref="SpeedTestIntervalHours"/>. Off by default - a manual run from the Tools page is always available regardless.</summary>
    public bool SpeedTestAutoRunEnabled { get; set; }

    /// <summary>The only interval choices offered in Settings - deliberately a small fixed set (not a free-form numeric field) so this stays light on traffic by construction.</summary>
    public static readonly int[] AllowedSpeedTestIntervalHours = [1, 2, 4, 8, 12, 24];

    private int _speedTestIntervalHours = 4;

    /// <summary>Clamped to <see cref="AllowedSpeedTestIntervalHours"/> here (not just in the UI) so a hand-edited settings.json is also protected - an out-of-set value is ignored, keeping whatever was previously valid.</summary>
    public int SpeedTestIntervalHours
    {
        get => _speedTestIntervalHours;
        set => _speedTestIntervalHours = Array.IndexOf(AllowedSpeedTestIntervalHours, value) >= 0 ? value : _speedTestIntervalHours;
    }

    /// <summary>
    /// Whether the automatic speed test also runs on a metered connection (mobile data, a phone's
    /// hotspot). Off by default: a test moves a lot of data, and Windows itself holds back large
    /// downloads on such a connection.
    /// </summary>
    public bool SpeedTestOnMetered { get; set; }

    // ---- Provider report -----------------------------------------------------------------------

    /// <summary>The subscription's advertised download speed in Mbit/s, for the provider report; 0 = not entered.</summary>
    public int SubscribedDownloadMbps { get; set; }

    /// <summary>The subscription's advertised upload speed in Mbit/s, for the provider report; 0 = not entered.</summary>
    public int SubscribedUploadMbps { get; set; }

    /// <summary>The periods offered as a guide; any number of days from 1 to 365 is accepted as well.</summary>
    public static readonly int[] AllowedReportPeriodDays = [7, 30, 90];

    public const int MaxReportPeriodDays = 365;

    private int _reportPeriodDays = 30;

    /// <summary>Which earlier period the report compares against; none by default.</summary>
    public ReportComparison ReportComparison { get; set; } = ReportComparison.None;

    /// <summary>How much goes into the AI export - see <see cref="Core.Export.AiExportDetail"/>. Standard fits a paid chat window; Compact a free one.</summary>
    public Core.Export.AiExportDetail AiExportDetail { get; set; } = Core.Export.AiExportDetail.Standard;

    /// <summary>How far back the provider report looks. Longer than <see cref="HistoryRetentionDays"/> is allowed; the report's coverage figure then shows how much of it was kept.</summary>
    public int ReportPeriodDays
    {
        get => _reportPeriodDays;
        set => _reportPeriodDays = Math.Clamp(value, 1, MaxReportPeriodDays);
    }

    // ---- Updates -------------------------------------------------------------------------------

    /// <summary>
    /// Whether the app checks GitHub for a newer release - once at startup, then once a day. Off
    /// means zero outbound calls for this feature; a manual "Check for updates" from the tray menu
    /// still works regardless.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Whether a release found newer than this one is downloaded and installed automatically (with
    /// a brief pop-up and an automatic restart), instead of only being reported with a link. Off by
    /// default - like <see cref="TracerouteOwnerLookup"/> and <see cref="LanScanVendorLookup"/>,
    /// this is more consequential than merely showing a notice, so it stays off until the user
    /// deliberately turns it on.
    /// </summary>
    public bool AutoUpdateInstall { get; set; }

    // ---- Endpoints ---------------------------------------------------------------------------

    /// <summary>
    /// User-added step-7 targets, appended after the built-in generate_204 reachability check.
    /// Empty by default - this is a generic diagnostics tool with no organisation's endpoints
    /// baked in, exactly as v1 described itself.
    /// </summary>
    public List<EndpointConfig> ApplicationEndpoints { get; set; } = [];

    /// <summary>The default for <see cref="PublicIpTargets"/>: three well-known public resolvers.</summary>
    public static readonly IReadOnlyList<string> DefaultPublicIpTargets = ["9.9.9.9", "8.8.8.8", "1.1.1.1"];

    /// <summary>
    /// The addresses step 5 pings to decide whether the public internet is reachable at all (and
    /// network-health monitoring pings for packet loss). Kept as text so a hand-edited file with a
    /// typo degrades to "that entry is ignored" rather than a settings file that fails to load.
    /// </summary>
    public List<string> PublicIpTargets { get; set; } = [.. DefaultPublicIpTargets];

    /// <summary>
    /// <see cref="PublicIpTargets"/> as addresses: the valid IPv4 entries, or the defaults when
    /// none are - step 5 with nothing to ping would read as "the internet is down".
    /// </summary>
    public IReadOnlyList<IPAddress> ParsedPublicIpTargets()
    {
        List<IPAddress> parsed = ParseIPv4List(PublicIpTargets);
        return parsed.Count > 0 ? parsed : ParseIPv4List(DefaultPublicIpTargets);
    }

    /// <summary>How many <see cref="CustomDnsServers"/> step 6 will try.</summary>
    public const int MaxCustomDnsServers = 2;

    /// <summary>
    /// The user's own last-resort DNS servers for step 6, tried only after the adapter's DNS
    /// servers and the public resolvers (9.9.9.9, 1.1.1.1, 8.8.8.8) have all failed. Empty by
    /// default - the adapter's own servers are what the computer really uses, so this is an
    /// optional extra, not a replacement.
    /// </summary>
    public List<string> CustomDnsServers { get; set; } = [];

    /// <summary>The time server the hourly clock check asks (see <see cref="Core.Monitors.ClockMonitor"/>). A host name; the pool is the sensible default.</summary>
    public string NtpServer { get; set; } = "pool.ntp.org";

    /// <summary><see cref="CustomDnsServers"/> as addresses: the valid IPv4 entries, at most <see cref="MaxCustomDnsServers"/>.</summary>
    public IReadOnlyList<IPAddress> ParsedCustomDnsServers() =>
        [.. ParseIPv4List(CustomDnsServers).Take(MaxCustomDnsServers)];

    /// <summary>The valid IPv4 addresses among <paramref name="entries"/>, in order, without duplicates.</summary>
    public static List<IPAddress> ParseIPv4List(IEnumerable<string> entries) =>
    [
        .. entries
            .Select(e => IPAddress.TryParse(e.Trim(), out IPAddress? a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? a : null)
            .OfType<IPAddress>()
            .Distinct(),
    ];

    // ---- Integrations -------------------------------------------------------------------------

    private int _kumaIntervalSeconds = Core.Monitors.KumaPusher.MinimumIntervalSeconds;

    /// <summary>
    /// An Uptime Kuma push-monitor URL. Empty means the integration is off - there is deliberately
    /// no separate enabled flag, matching <see cref="Core.Monitors.KumaPusher"/>'s own definition
    /// of "active".
    /// </summary>
    public string KumaPushUrl { get; set; } = string.Empty;

    /// <summary>Clamped to a minute up to a day: the Settings field offers the same range, and a timer period of weeks would throw and silently end the push loop.</summary>
    public int KumaIntervalSeconds
    {
        get => _kumaIntervalSeconds;
        set => _kumaIntervalSeconds = Math.Clamp(value, Core.Monitors.KumaPusher.MinimumIntervalSeconds, 86_400);
    }

    /// <summary>Where an incident opening or closing is POSTed as JSON (see <see cref="Core.Monitors.IncidentWebhook"/>). Empty means off; only https is ever called.</summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// What the webhook calls this machine, so a receiver fed by many machines knows which one
    /// speaks: a hostname, an address, a desk, whatever the user chooses. Empty leaves the field out
    /// of the message - nothing identifies the machine unless the user says so.
    /// </summary>
    public string WebhookLabel { get; set; } = string.Empty;

    // ---- Credentials ----------------------------------------------------------------------------

    /// <summary>The settings whose value is a credential by itself: a push URL and a webhook URL carry their token. Endpoint header values are the third kind.</summary>
    public static readonly IReadOnlyList<string> SecretUrlProperties = [nameof(KumaPushUrl), nameof(WebhookUrl)];

    /// <summary>
    /// Whether <see cref="Save"/> writes credentials protected with the user's Windows account
    /// (<see cref="SecretProtection"/>) or as readable text. Null until the user has been asked,
    /// which happens on the first start that finds a readable credential in the file - see
    /// <c>App.SecretProtectionPrompt</c>. False is a deliberate choice, and Settings then warns.
    /// </summary>
    public bool? ProtectSecrets { get; set; }

    /// <summary>Whether any credential is set at all - what the warning about readable credentials hinges on.</summary>
    [JsonIgnore]
    public bool HasSecrets =>
        KumaPushUrl.Length > 0 || WebhookUrl.Length > 0 || ApplicationEndpoints.Any(e => e.Headers.Any(h => h.Value.Length > 0));

    /// <summary>Whether the file <see cref="Load"/> read held at least one credential as readable text.</summary>
    [JsonIgnore]
    public bool LoadedPlaintextSecrets { get; private set; }

    /// <summary>
    /// Turns every protected credential back into its plain text, in place, so the rest of the app
    /// only ever sees plain values. A protected value that cannot be read here (another user's
    /// file) becomes empty. Returns whether any credential was found as readable text.
    /// </summary>
    private bool UnprotectSecrets()
    {
        bool plaintext = false;

        string Plain(string value)
        {
            if (SecretProtection.IsProtected(value))
            {
                return SecretProtection.Unprotect(value) ?? string.Empty;
            }

            plaintext |= value.Length > 0;
            return value;
        }

        KumaPushUrl = Plain(KumaPushUrl);
        WebhookUrl = Plain(WebhookUrl);
        for (int i = 0; i < ApplicationEndpoints.Count; i++)
        {
            EndpointConfig endpoint = ApplicationEndpoints[i];
            if (endpoint.Headers.Count > 0)
            {
                ApplicationEndpoints[i] = endpoint with { Headers = [.. endpoint.Headers.Select(h => h with { Value = Plain(h.Value) })] };
            }
        }

        return plaintext;
    }

    /// <summary>Applies <paramref name="transform"/> to every credential in a serialized settings object - the one place that knows where they all are.</summary>
    internal static void ForEachSecret(JsonObject root, Func<string, string> transform)
    {
        foreach (string property in SecretUrlProperties)
        {
            if (root[property]?.GetValue<string>() is { Length: > 0 } value)
            {
                root[property] = transform(value);
            }
        }

        if (root["ApplicationEndpoints"] is not JsonArray endpoints)
        {
            return;
        }

        foreach (JsonNode? endpointNode in endpoints)
        {
            if (endpointNode is not JsonObject endpoint || endpoint["Headers"] is not JsonArray headers)
            {
                continue;
            }

            foreach (JsonNode? headerNode in headers)
            {
                if (headerNode is JsonObject header && header["Value"]?.GetValue<string>() is { Length: > 0 } value)
                {
                    header["Value"] = transform(value);
                }
            }
        }
    }

    // ---- Diagnostics ----------------------------------------------------------------------------

    /// <summary>Off by default - this is troubleshooting output, not something a monitor needs running to do its job.</summary>
    public DiagnosticLogLevel DiagnosticLogLevel { get; set; } = DiagnosticLogLevel.Off;

    public bool LogSuccessfulChecks { get; set; }

    public bool LogDetailedMeasurements { get; set; }

    private int _maxDiagnosticLogFiles = 5;
    private long _maxDiagnosticLogFileSizeBytes = 5 * 1024 * 1024;
    private string _diagnosticLogDirectory = string.Empty;

    // The two rotation values and the directory are also clamped here, not only in the UI they do
    // not have: a settings file is a trust boundary once it can be imported from someone else.

    /// <summary>How many rotated log files are kept. Clamped to 1-50: rotation renames every one of them before each write past the size limit.</summary>
    public int MaxDiagnosticLogFiles
    {
        get => _maxDiagnosticLogFiles;
        set => _maxDiagnosticLogFiles = Math.Clamp(value, 1, 50);
    }

    /// <summary>Clamped to 1 KB - 1 GB; below that the log would rotate on nearly every write.</summary>
    public long MaxDiagnosticLogFileSizeBytes
    {
        get => _maxDiagnosticLogFileSizeBytes;
        set => _maxDiagnosticLogFileSizeBytes = Math.Clamp(value, 1024, 1024L * 1024 * 1024);
    }

    /// <summary>
    /// Where diagnostics.log is written. Empty means "use the default logs folder" - the same
    /// empty-means-default convention WindowWidth/WindowHeight already use, kept distinct from
    /// AppPaths.Root (settings/incidents/history/cache all stay put regardless of this setting;
    /// only the diagnostic log moves). Only a full local path is accepted: a network path would
    /// have every cycle's log line - and the user's Windows credentials - go to whoever the share
    /// belongs to, which is not what anyone importing a settings file means to agree to.
    /// </summary>
    public string DiagnosticLogDirectory
    {
        get => _diagnosticLogDirectory;
        set => _diagnosticLogDirectory = IsLocalDirectoryPath(value) ? value : string.Empty;
    }

    /// <summary>Empty, or a fully qualified path on a local drive - no UNC path and no network drive.</summary>
    public static bool IsLocalDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            return new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Network changes -----------------------------------------------------------------------

    // All four on by default: a change of network is exactly the kind of thing that is impossible
    // to reconstruct afterwards, and each is one incident per event, not a stream.

    /// <summary>The connection moved to another adapter - see <see cref="Core.Monitors.NetworkChangeMonitor.PathChanged"/>.</summary>
    public bool IncidentOnPathChange { get; set; } = true;

    /// <summary>An adapter other than the primary one holds a 169.254 address - see <see cref="Core.Monitors.NetworkChangeMonitor.Apipa"/>.</summary>
    public bool IncidentOnApipa { get; set; } = true;

    /// <summary>Gateway, network, DNS, DHCP server, IPv6 prefix or Wi-Fi network changed on the same adapter - see <see cref="Core.Monitors.NetworkChangeMonitor.NetworkChanged"/>.</summary>
    public bool IncidentOnNetworkChange { get; set; } = true;

    /// <summary>The gateway answers from a different hardware address - see <see cref="Core.Monitors.NetworkChangeMonitor.GatewayHardwareChanged"/>.</summary>
    public bool IncidentOnGatewayHardwareChange { get; set; } = true;

    [JsonIgnore]
    public Core.Monitors.NetworkChangeOptions NetworkChangeOptions =>
        new(IncidentOnPathChange, IncidentOnApipa, IncidentOnNetworkChange, IncidentOnGatewayHardwareChange);

    // ---- Network health ------------------------------------------------------------------------

    /// <summary>
    /// Whether packet-level monitoring runs: as soon as one condition below is switched on. There is
    /// no separate master switch any more - one that was on with every condition off tested nothing,
    /// and one that was off with conditions on looked like it tested something.
    /// </summary>
    [JsonIgnore]
    public bool NetworkHealthMonitoringEnabled =>
        TcpRetransmissions.Enabled || PacketLoss.Enabled || TtlExceeded.Enabled || RogueDhcp.Enabled;

    /// <summary>The keys of the conditions that are switched on, for the export's context section.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> EnabledNetworkHealthConditions =>
    [
        .. new[] { ("tcpRetransmissions", TcpRetransmissions), ("packetLoss", PacketLoss), ("ttlExceeded", TtlExceeded), ("rogueDhcp", RogueDhcp) }
            .Where(c => c.Item2.Enabled)
            .Select(c => c.Item1),
    ];

    // The defaults below are deliberately uncalibrated - there is no measurement baseline in this
    // codebase to set them from - and every condition starts disabled regardless, so they only
    // matter once a user turns one on and have not yet typed their own numbers.
    // Every window is clamped to a minute up to a day, the range the Settings fields offer: a
    // timer period of weeks would throw and silently end the monitor's loop.
    private NetworkHealthThreshold _tcpRetransmissions = new(false, 500, 5);
    private NetworkHealthThreshold _packetLoss = new(false, 20, 5);
    private NetworkHealthThreshold _ttlExceeded = new(false, 50, 5);
    private NetworkHealthThreshold _rogueDhcp = new(false, 0, 15);

    public NetworkHealthThreshold TcpRetransmissions
    {
        get => _tcpRetransmissions;
        set => _tcpRetransmissions = ClampWindow(value);
    }

    /// <summary>MaxOccurrences here is a percentage (0-100), not a count.</summary>
    public NetworkHealthThreshold PacketLoss
    {
        get => _packetLoss;
        set => _packetLoss = ClampWindow(value);
    }

    public NetworkHealthThreshold TtlExceeded
    {
        get => _ttlExceeded;
        set => _ttlExceeded = ClampWindow(value);
    }

    /// <summary>
    /// MaxOccurrences is unused for this condition - any server other than the known gateway is
    /// rogue. WindowMinutes is this condition's own poll interval instead. Every poll shares UDP
    /// port 68 with Windows' DHCP client for a few seconds and has the real server reserve an offer
    /// for a made-up client (see <see cref="Net.DhcpDiscoverProbe"/>), so the default is deliberately
    /// a quarter of an hour, not the five minutes the other windows use.
    /// </summary>
    public NetworkHealthThreshold RogueDhcp
    {
        get => _rogueDhcp;
        set => _rogueDhcp = ClampWindow(value);
    }

    private static NetworkHealthThreshold ClampWindow(NetworkHealthThreshold threshold) =>
        threshold with { MaxOccurrences = Math.Max(0, threshold.MaxOccurrences), WindowMinutes = Math.Clamp(threshold.WindowMinutes, 1, 1440) };

    // ---- Persistence --------------------------------------------------------------------------

    /// <summary>Reads settings.json, or returns defaults when it is missing or unreadable - see the class summary.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                // A first start: speak the language Windows does, when it is one of ours.
                return new AppSettings { Language = LanguageForThisWindows() };
            }

            string json = File.ReadAllText(AppPaths.SettingsFile);
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
            settings.LoadedPlaintextSecrets = settings.UnprotectSecrets();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>The app language for the language Windows displays itself in: its code when supported, otherwise English.</summary>
    public static string LanguageForThisWindows() => LanguageFor(WindowsLanguage.PreferredTag());

    /// <summary>The app language for a Windows UI culture: its two-letter code when supported, otherwise English.</summary>
    public static string LanguageFor(CultureInfo culture) => LanguageFor(culture.TwoLetterISOLanguageName);

    /// <summary>The app language for a BCP-47 tag or a two-letter code ("nl-NL", "nl"): its two-letter code when supported, otherwise English.</summary>
    public static string LanguageFor(string languageTag)
    {
        int dash = languageTag.IndexOf('-');
        string code = (dash < 0 ? languageTag : languageTag[..dash]).ToLowerInvariant();
        return Localization.LocalizationManager.SupportedLanguages.Any(l => l.Code == code) ? code : "en";
    }

    /// <summary>
    /// Writes to a temporary file and moves it over the real one, so an interrupted write (or a
    /// crash mid-save) cannot leave a half-written settings file behind. Returns false rather than
    /// throwing: a failed save must not take the UI down. With <see cref="ProtectSecrets"/> on,
    /// every credential goes to disk protected; the object itself keeps the plain values.
    /// </summary>
    public bool Save()
    {
        try
        {
            AppPaths.TryEnsureDirectory(AppPaths.Root);
            string tempPath = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tempPath, ToJson(protectSecrets: ProtectSecrets == true));
            File.Move(tempPath, AppPaths.SettingsFile, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes the whole settings object to an arbitrary path, verbatim - unlike
    /// <see cref="Core.Export.DataExportBuilder"/>'s redacted copy inside the "export all data"
    /// zip, this is a literal backup/restore mechanism (mirroring v1's own settings export/import),
    /// and a Kuma push URL or endpoint header with the secret blanked out would not actually
    /// restore anything. Throws on failure, unlike <see cref="Save"/> - this is a user-initiated
    /// action with its own dialog and its own error handling, not a background autosave.
    /// </summary>
    public void ExportTo(string path) => File.WriteAllText(path, ToJson(protectSecrets: false));

    private string ToJson(bool protectSecrets)
    {
        if (!protectSecrets)
        {
            return JsonSerializer.Serialize(this, Json);
        }

        var root = (JsonObject)JsonSerializer.SerializeToNode(this, Json)!;
        ForEachSecret(root, SecretProtection.Protect);
        return root.ToJsonString(Json);
    }

    /// <summary>
    /// Reads a settings file exported by <see cref="ExportTo"/> (or the regular on-disk
    /// settings.json). Throws on a missing or malformed file - the caller decides how to tell the
    /// user. A protected credential from another user's file comes back empty.
    /// </summary>
    public static AppSettings? ImportFrom(string path)
    {
        AppSettings? imported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
        imported?.UnprotectSecrets();
        return imported;
    }

    /// <summary>
    /// Copies every readable/writable property from <paramref name="source"/> onto this instance
    /// in place, rather than replacing the object - other components already hold a reference to
    /// this exact <see cref="AppSettings"/> instance, so swapping it out from under them would mean
    /// finding and updating every one of those references too. Same reflection-based approach v1
    /// used for the same reason.
    /// </summary>
    public void ApplyFrom(AppSettings source)
    {
        foreach (System.Reflection.PropertyInfo property in typeof(AppSettings).GetProperties())
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(this, property.GetValue(source));
            }
        }
    }
}
