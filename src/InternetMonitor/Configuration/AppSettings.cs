using System.Text.Json;
using System.Text.Json.Serialization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Configuration;

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

    // ---- Endpoints ---------------------------------------------------------------------------

    /// <summary>
    /// User-added step-7 targets, appended after the built-in generate_204 reachability check.
    /// Empty by default - this is a generic diagnostics tool with no organisation's endpoints
    /// baked in, exactly as v1 described itself.
    /// </summary>
    public List<EndpointConfig> ApplicationEndpoints { get; set; } = [];

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
}
