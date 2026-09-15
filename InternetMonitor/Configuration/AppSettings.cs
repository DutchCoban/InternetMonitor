using System.Text.Json;
using System.Text.Json.Serialization;
using InternetMonitor.Network.Diagnosis;

namespace InternetMonitor.Configuration;

public enum DiagnosticLogLevel
{
    Off,
    Basic,
    Extended,
    Full,
}

public sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public bool AutoStartWithWindows { get; set; }

    /// <summary>
    /// Whether an outage shows the corner pop-up window (and, on recovery, the "back online"
    /// balloon tip). Off does not affect the tray icon, the status window, or incident logging -
    /// those keep reflecting the real state regardless; this only controls the interruptive
    /// notification.
    /// </summary>
    public bool ShowOutagePopups { get; set; } = true;

    /// <summary>
    /// When enabled, suppresses the outage pop-up/balloon (but nothing else - tray icon, status
    /// window, and incident logging are unaffected, same as <see cref="ShowOutagePopups"/>)
    /// during a configured daily time window. <see cref="QuietHoursStart"/>/<see cref="QuietHoursEnd"/>
    /// may wrap past midnight (e.g. 22:00-06:00) - see TrayApplicationContext.IsWithinQuietHours.
    /// </summary>
    public bool QuietHoursEnabled { get; set; }

    public TimeOnly QuietHoursStart { get; set; } = new(22, 0);
    public TimeOnly QuietHoursEnd { get; set; } = new(7, 0);

    /// <summary>Whether the app checks GitHub for a newer release - once at startup, then once a day. Off means zero outbound calls for this feature; a manual "Check for updates" from the tray menu still works regardless.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Whether the Speed Test also runs on its own on a schedule, every <see cref="SpeedTestIntervalHours"/>. Off by default - a manual run from the Speed Test screen is always available regardless.</summary>
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
    /// Whether the hourly NTP time-synchronization check runs at all. On by default. When off,
    /// the Diagnostics screen's Time Synchronization row reports a neutral "disabled" reading
    /// (see DiagnosticsCoordinator) rather than a stale or misleading measurement, and it can
    /// never become the root cause of a diagnosed outage while off.
    /// </summary>
    public bool TimeSyncCheckEnabled { get; set; } = true;

    /// <summary>
    /// Legacy single ping-target address, superseded by <see cref="PingTargets"/>. Kept only so
    /// <see cref="Load"/> can migrate an existing settings.json's custom address into a seeded
    /// "Primary" entry the first time it's read after upgrading - nothing else in the app reads
    /// this property live any more.
    /// </summary>
    public string PingTargetAddress { get; set; } = "9.9.9.9";

    /// <summary>
    /// Named continuous-ping targets shown on the Diagnostics screen (multi-target comparison) -
    /// each enabled entry gets its own live latency graph. Seeded on first load - see
    /// <see cref="Load"/> - so this is never actually empty in practice, but isn't guaranteed
    /// non-empty by the type itself (the user can remove every entry via Settings).
    /// </summary>
    public List<PingTargetConfig> PingTargets { get; set; } = [];

    /// <summary>
    /// Latency classification thresholds, user-configurable in Settings. Below this is Ok. The
    /// Warning-&lt;-Error invariant is enforced in SettingsForm at commit time (an inline error
    /// label), not here - matches how PingTargetAddress/KumaPushUrl validate elsewhere in this
    /// app, and avoids AppSettings silently rewriting a hand-edited settings.json value.
    /// </summary>
    public int LatencyWarningThresholdMs { get; set; } = 300;

    /// <summary>Latency at or above this is Error. See <see cref="LatencyWarningThresholdMs"/>.</summary>
    public int LatencyErrorThresholdMs { get; set; } = 1000;

    private const int MinHistoryRetentionMinutes = 15;
    private const int MaxHistoryRetentionMinutes = 32 * 24 * 60;
    private int _historyRetentionMinutes = 30 * 24 * 60;

    /// <summary>
    /// How long persisted per-probe history (the Diagnostics screen's double-click history
    /// charts) is kept, in minutes. Clamped to 15 minutes - 32 days here (not just in the UI) so
    /// a hand-edited settings.json is also protected.
    /// </summary>
    public int HistoryRetentionMinutes
    {
        get => _historyRetentionMinutes;
        set => _historyRetentionMinutes = Math.Clamp(value, MinHistoryRetentionMinutes, MaxHistoryRetentionMinutes);
    }

    private const int MinimumKumaIntervalSeconds = 60;

    /// <summary>Empty = feature off. The user pastes in their own push URL to activate it.</summary>
    public string KumaPushUrl { get; set; } = string.Empty;

    private int _kumaIntervalSeconds = MinimumKumaIntervalSeconds;

    /// <summary>Clamped here (not in the pusher) so a hand-edited settings.json is also protected.</summary>
    public int KumaIntervalSeconds
    {
        get => _kumaIntervalSeconds;
        set => _kumaIntervalSeconds = Math.Max(MinimumKumaIntervalSeconds, value);
    }

    /// <summary>
    /// User-configured application endpoints to monitor. Empty by default - no organization's
    /// endpoints are hardcoded into the application; users add their own here.
    /// </summary>
    public List<EndpointConfig> ApplicationEndpoints { get; set; } = [];

    /// <summary>
    /// Which configured endpoint (by <see cref="EndpointConfig.Id"/>) represents "server" in the
    /// status popup's connectivity diagram. Null = fall back to the general connectivity check.
    /// </summary>
    public string? StatusDiagramEndpointId { get; set; }

    /// <summary>
    /// Classifications excluded from the Diagnostics screen's uptime/SLA report (Settings > SLA
    /// Report). Stored as *excluded* rather than *included* so a classification this app didn't
    /// have yet when you last opened Settings - or one you've simply never touched - defaults to
    /// counted, rather than silently missing from the report until you remember to opt it in.
    /// Enum-as-string here comes from the global converter in <see cref="JsonOptions"/> (a
    /// per-property <c>[JsonConverter]</c> attribute only applies to that exact property's type,
    /// not to element types nested inside a <c>List&lt;T&gt;</c>, unlike the single-enum
    /// <see cref="DiagnosticLogLevel"/> property below).
    /// </summary>
    public List<DiagnosisClassification> SlaExcludedClassifications { get; set; } = [];

    /// <summary>Configured endpoints (by <see cref="EndpointConfig.Id"/>) excluded from the uptime/SLA report. Same include-by-default reasoning as <see cref="SlaExcludedClassifications"/>.</summary>
    public List<string> SlaExcludedEndpointIds { get; set; } = [];

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DiagnosticLogLevel DiagnosticLogLevel { get; set; } = DiagnosticLogLevel.Off;

    public bool LogSuccessfulChecks { get; set; }
    public bool LogDetailedMeasurements { get; set; }

    private const int DefaultMaxLogFiles = 5;
    private const long DefaultMaxLogFileSizeBytes = 5 * 1024 * 1024;

    public int MaxDiagnosticLogFiles { get; set; } = DefaultMaxLogFiles;
    public long MaxDiagnosticLogFileSizeBytes { get; set; } = DefaultMaxLogFileSizeBytes;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "InternetMonitor",
        "settings.json");

    /// <summary>
    /// Shared by Load and Save: registers JsonStringEnumConverter globally (not just via the
    /// per-property attribute on DiagnosticLogLevel) so enum values nested inside a collection -
    /// see SlaExcludedClassifications - also serialize as readable strings, not raw ints.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        try
        {
            if (!File.Exists(SettingsPath))
            {
                settings = new AppSettings();
            }
            else
            {
                string json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }

        // One-time migration (also covers a brand-new settings.json): PingTargets replaced the
        // single PingTargetAddress field, but a pre-existing custom address should still carry
        // over rather than silently vanish the first time this runs after upgrading.
        if (settings.PingTargets.Count == 0)
        {
            settings.PingTargets.Add(new PingTargetConfig { Name = "Primary", Address = settings.PingTargetAddress });
        }

        return settings;
    }

    /// <summary>
    /// Writes settings.json via a temp-file-then-replace so a crash or AV lock mid-write can't
    /// leave a half-written (and therefore unreadable) settings file behind. Mirrors Load()'s
    /// exception policy - a save failure must not crash the app, since it's usually triggered
    /// from a UI event handler.
    /// </summary>
    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(this, JsonOptions);
            string tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed save should not crash the app.
        }
    }

    /// <summary>
    /// Writes this instance to an arbitrary user-chosen path (Settings' "Export" button), same
    /// format as <see cref="Save"/>. Unlike Save(), this is an explicit, occasional user action -
    /// it lets exceptions propagate so the UI can tell the user it failed, rather than swallowing
    /// them like the background-triggered Save() must.
    /// </summary>
    public void ExportTo(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Reads settings from an arbitrary user-chosen path (Settings' "Import" button). Returns null if the file didn't deserialize to anything; lets I/O and JSON exceptions propagate for the same reason as <see cref="ExportTo"/>.</summary>
    public static AppSettings? ImportFrom(string path)
    {
        AppSettings? imported = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
        if (imported is not null && imported.PingTargets.Count == 0)
        {
            // Same migration Load() applies - covers importing an export saved before PingTargets existed.
            imported.PingTargets.Add(new PingTargetConfig { Name = "Primary", Address = imported.PingTargetAddress });
        }

        return imported;
    }

    /// <summary>
    /// Copies every readable/writable property from <paramref name="source"/> onto this instance
    /// in place - used by Settings' "Import" so the many components already holding a reference
    /// to the original <see cref="AppSettings"/> instance (DiagnosticsCoordinator, the pushers,
    /// this form's own controls) see the imported values without needing that reference replaced
    /// everywhere. Reflection-based deliberately: a plain property-by-property copy would silently
    /// stop covering a newly-added setting until someone remembered to update it here too.
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
