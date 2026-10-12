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
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetMonitor");

    /// <summary>
    /// v1's settings file. Read once by the phase-8 migration and never written by v2.
    /// </summary>
    public static string LegacySettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// Where v2 writes settings during the v1 -> v2 transition.
    ///
    /// Deliberately NOT settings.json yet: the v2 model is still being filled in phase by phase,
    /// and serialising it over the shared file would drop every v1 property it does not know about
    /// (application endpoints, ping targets, quiet hours, logging levels) from a real
    /// installation. Phase 8 completes the model, migrates v1's file in one pass, and makes
    /// settings.json v2's own again.
    /// </summary>
    public static string SettingsFile => Path.Combine(Root, "settings.v2.json");

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
