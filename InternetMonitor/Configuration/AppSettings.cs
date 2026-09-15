using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Target address for the continuous latency ping shown on the Diagnostics screen.</summary>
    public string PingTargetAddress { get; set; } = "9.9.9.9";

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

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
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

            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed save should not crash the app.
        }
    }
}
