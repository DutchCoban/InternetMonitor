namespace InternetMonitor.Configuration;

/// <summary>
/// Every location the app reads or writes, in one place.
///
/// These paths are deliberately identical to v1's, so an existing installation's settings,
/// incidents and per-check history are picked up by v2 rather than abandoned. Nothing lives inside
/// the application directory, so the executable can be replaced without touching user data.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Overrides where everything is stored. Set it to a scratch directory to exercise the app
    /// against throwaway settings, history and incidents instead of the real ones - which matters
    /// because testing a fault means recording an incident, and a simulated outage has no business
    /// appearing in somebody's actual incident log.
    /// </summary>
    private const string DataDirectoryVariable = "INTERNETMONITOR_DATA_DIR";

    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetMonitor")
            : overridden;
    }

    /// <summary>
    /// The same settings.json v1 used. v2's model is now complete, and
    /// <see cref="AppSettings"/>'s deserialiser already ignores properties it doesn't know (v1-only
    /// settings v2 dropped) and defaults ones it doesn't find (v2-only settings an existing v1 file
    /// predates), so reading v1's file here directly - with no separate migration step or
    /// intermediate filename - is enough for an existing installation's settings to carry over.
    /// </summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string IncidentsFile => Path.Combine(Root, "incidents.json");

    public static string HistoryDir => Path.Combine(Root, "history");

    public static string LogsDir => Path.Combine(Root, "logs");

    public static string CrashLog => Path.Combine(LogsDir, "crash.log");

    /// <summary>Cached IP-to-owner lookups, so a repeated traceroute does not re-query Cymru/RDAP.</summary>
    public static string CacheDir => Path.Combine(Root, "cache");

    /// <summary>Saved traceroute runs, including the ones captured automatically when an incident opens.</summary>
    public static string TraceroutesDir => Path.Combine(Root, "traceroutes");

    /// <summary>
    /// Creates a directory if it is missing, swallowing failure. Callers are all best-effort
    /// persistence paths: losing a cache write must never interrupt monitoring.
    /// </summary>
    public static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
