using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Configuration;

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

    public string Language { get; set; } = "nl";

    /// <summary>System (follow Windows), Light or Dark.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public bool AutoStartWithWindows { get; set; }

    /// <summary>
    /// A brief pop-up when a new incident opens. On by default, matching v1's equivalent
    /// (ShowOutagePopups) - a connectivity tool that stays silent about a connectivity problem
    /// defeats its own purpose.
    /// </summary>
    public bool ShowIncidentPopups { get; set; } = true;

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

    public static readonly int[] AllowedReportPeriodDays = [7, 30, 90];

    private int _reportPeriodDays = 30;

    /// <summary>How far back the provider report looks. Longer than <see cref="HistoryRetentionDays"/> is allowed; the report's coverage figure then shows how much of it was kept.</summary>
    public int ReportPeriodDays
    {
        get => _reportPeriodDays;
        set => _reportPeriodDays = Array.IndexOf(AllowedReportPeriodDays, value) >= 0 ? value : _reportPeriodDays;
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

    public int KumaIntervalSeconds
    {
        get => _kumaIntervalSeconds;
        set => _kumaIntervalSeconds = Math.Max(Core.Monitors.KumaPusher.MinimumIntervalSeconds, value);
    }

    // ---- Diagnostics ----------------------------------------------------------------------------

    /// <summary>Off by default - this is troubleshooting output, not something a monitor needs running to do its job.</summary>
    public DiagnosticLogLevel DiagnosticLogLevel { get; set; } = DiagnosticLogLevel.Off;

    public bool LogSuccessfulChecks { get; set; }

    public bool LogDetailedMeasurements { get; set; }

    public int MaxDiagnosticLogFiles { get; set; } = 5;

    public long MaxDiagnosticLogFileSizeBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Where diagnostics.log is written. Empty means "use the default logs folder" - the same
    /// empty-means-default convention WindowWidth/WindowHeight already use, kept distinct from
    /// AppPaths.Root (settings/incidents/history/cache all stay put regardless of this setting;
    /// only the diagnostic log moves).
    /// </summary>
    public string DiagnosticLogDirectory { get; set; } = string.Empty;

    // ---- Network health ------------------------------------------------------------------------

    /// <summary>Off by default - packet-level monitoring is a deeper diagnostic than this app runs out of the box.</summary>
    public bool NetworkHealthMonitoringEnabled { get; set; }

    // The defaults below are deliberately uncalibrated - there is no measurement baseline in this
    // codebase to set them from - and every condition starts disabled regardless, so they only
    // matter once a user turns one on and have not yet typed their own numbers.
    public NetworkHealthThreshold TcpRetransmissions { get; set; } = new(false, 500, 5);

    /// <summary>MaxOccurrences here is a percentage (0-100), not a count.</summary>
    public NetworkHealthThreshold PacketLoss { get; set; } = new(false, 20, 5);

    public NetworkHealthThreshold TtlExceeded { get; set; } = new(false, 50, 5);

    /// <summary>MaxOccurrences is unused for this condition - any server other than the known gateway is rogue. WindowMinutes is this condition's own poll interval instead.</summary>
    public NetworkHealthThreshold RogueDhcp { get; set; } = new(false, 0, 5);

    // ---- Persistence --------------------------------------------------------------------------

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(AppPaths.SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Writes to a temporary file and moves it over the real one, so an interrupted write (or a
    /// crash mid-save) cannot leave a half-written settings file behind. Returns false rather than
    /// throwing: a failed save must not take the UI down.
    /// </summary>
    public bool Save()
    {
        try
        {
            AppPaths.TryEnsureDirectory(AppPaths.Root);
            string tempPath = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, Json));
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
    public void ExportTo(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Reads a settings file exported by <see cref="ExportTo"/> (or the regular on-disk settings.json). Throws on a missing or malformed file - the caller decides how to tell the user.</summary>
    public static AppSettings? ImportFrom(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);

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
