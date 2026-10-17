using System.ComponentModel;
using System.Diagnostics;

namespace InternetMonitor.Ui;

/// <summary>
/// Hands a URL, file or folder to the Windows shell - the default browser, Explorer, or whatever
/// handler is registered for it.
///
/// Three entry points rather than one, because the shell will start whatever a string's scheme
/// is registered for: <see cref="OpenUrl"/> only ever opens a web address, so a link that came
/// from the network (a release page in GitHub's API answer, say) cannot smuggle in another
/// scheme; <see cref="OpenSettings"/> only opens a Windows settings page; <see cref="OpenPath"/>
/// opens a file or folder this app wrote or the user chose.
///
/// Every caller here opens something as a convenience the user could also open themselves, so a
/// failure (no default browser, a folder that does not exist yet) is reported back as false rather
/// than thrown: there is never anything better to do than carry on.
/// </summary>
internal static class ShellOpen
{
    private const string SettingsScheme = "ms-settings";

    /// <summary>Opens <paramref name="url"/> in the default browser; false unless it is an absolute http(s) address.</summary>
    public static bool OpenUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && Start(uri.AbsoluteUri);

    /// <summary>Opens a Windows settings page, such as <c>ms-settings:privacy-location</c>.</summary>
    public static bool OpenSettings(string uri) =>
        uri.StartsWith(SettingsScheme + ":", StringComparison.OrdinalIgnoreCase) && Start(uri);

    /// <summary>Opens a file with its registered application, or a folder in Explorer.</summary>
    public static bool OpenPath(string path) => Path.IsPathRooted(path) && Start(path);

    private static bool Start(string target)
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
