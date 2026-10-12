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

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string existing && existing.Equals(QuotedExePath(), StringComparison.OrdinalIgnoreCase);
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
                key.SetValue(ValueName, QuotedExePath());
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
    /// The running executable's own path, quoted so a path containing spaces - the common case
    /// for a single-file publish under %LocalAppData% or Program Files - survives being split into
    /// arguments if Windows ever treats the Run value that way.
    /// </summary>
    private static string QuotedExePath() => $"\"{Environment.ProcessPath}\"";
}
