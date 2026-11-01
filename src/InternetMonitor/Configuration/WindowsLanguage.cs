using System.Globalization;
using System.Runtime.InteropServices;

namespace InternetMonitor.Configuration;

/// <summary>
/// The language Windows displays itself in, as a BCP-47 tag such as "nl-NL".
///
/// Asked of Windows directly rather than through <see cref="CultureInfo.CurrentUICulture"/>: this
/// app is published with InvariantGlobalization, under which every culture - the current UI
/// culture included - is the invariant one, so the .NET answer would always be "English".
/// </summary>
internal static class WindowsLanguage
{
    private const uint MuiLanguageName = 0x8;

    /// <summary>The user's preferred display language, or the .NET culture's name when Windows cannot say.</summary>
    public static string PreferredTag()
    {
        try
        {
            uint count = 0;
            uint length = 0;
            if (GetUserPreferredUILanguages(MuiLanguageName, out count, null, ref length) && length > 1)
            {
                char[] buffer = new char[length];
                if (GetUserPreferredUILanguages(MuiLanguageName, out count, buffer, ref length) && count > 0)
                {
                    // A double-null-terminated list; the first entry is the one Windows shows.
                    int end = Array.IndexOf(buffer, '\0');
                    if (end > 0)
                    {
                        return new string(buffer, 0, end);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Not Windows, or an ancient one - fall through to what .NET knows.
        }

        return CultureInfo.CurrentUICulture.Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint numberOfLanguages, char[]? languagesBuffer, ref uint bufferLength);
}
