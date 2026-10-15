using Microsoft.Win32;

namespace InternetMonitor.App;

/// <summary>
/// Whether Windows starts this app at sign-in, via the per-user Run key - no installer, no
/// scheduled task, exactly what v1's "Start automatically with Windows" setting did.
///
/// The registry is treated as the source of truth rather than a mirror of a persisted bool: if a
/// user removes the entry by hand (Task Manager's Startup tab, say), the Settings toggle should
/// show that honestly on its next read, not keep claiming it is still enabled because that is
/// what was last saved.
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "InternetMonitor";

    /// <summary>
    /// What Windows passes at sign-in: start in the tray without opening the window. A launch the
    /// user makes themselves opens it - see <see cref="Program"/>.
    /// </summary>
    public const string TrayArgument = "--tray";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string existing
                && (existing.Equals(Command(), StringComparison.OrdinalIgnoreCase) || existing.Equals(QuotedExePath(), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (enabled)
            {
                key.SetValue(ValueName, Command());
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A locked-down machine (group policy, AV blocking Run-key writes) must not crash the
            // settings page over a toggle - the caller reads IsEnabled() back to show reality.
            return false;
        }
    }

    /// <summary>
    /// Rewrites an entry made by an older version - the bare path, from when every launch started
    /// in the tray - to include <see cref="TrayArgument"/>, so signing in keeps starting quietly now
    /// that an ordinary launch opens the window. Leaves anything else alone.
    /// </summary>
    public static void UpgradeLegacyEntry()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is string existing && existing.Equals(QuotedExePath(), StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command());
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            // Left as it was: the app then opens its window at sign-in, which is harmless.
        }
    }

    private static string Command() => $"{QuotedExePath()} {TrayArgument}";

    /// <summary>
    /// The running executable's own path, quoted so a path containing spaces - the common case
    /// for a single-file publish under %LocalAppData% or Program Files - survives being split into
    /// arguments if Windows ever treats the Run value that way.
    /// </summary>
    private static string QuotedExePath() => $"\"{Environment.ProcessPath}\"";
}
