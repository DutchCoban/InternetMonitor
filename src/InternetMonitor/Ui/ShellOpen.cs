using System.ComponentModel;
using System.Diagnostics;

namespace InternetMonitor.Ui;

/// <summary>
/// Hands a URL, file or folder to the Windows shell - the default browser, Explorer, or whatever
/// handler is registered for it.
///
/// Every caller here opens something as a convenience the user could also open themselves, so a
/// failure (no default browser, a folder that does not exist yet) is reported back as false rather
/// than thrown: there is never anything better to do than carry on.
/// </summary>
internal static class ShellOpen
{
    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
