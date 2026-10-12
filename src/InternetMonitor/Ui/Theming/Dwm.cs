using System.Runtime.InteropServices;

namespace InternetMonitor.Ui.Theming;

/// <summary>
/// The handful of Desktop Window Manager attributes that give a plain WinForms window the
/// Windows 11 look: a title bar that follows our dark/light theme instead of staying stubbornly
/// white, a Mica backdrop, and properly rounded corners.
///
/// This is deliberately *not* custom window chrome. Drawing our own title bar would mean
/// reimplementing hit-testing, snap layouts, Aero Snap, double-click-to-maximise, the system menu
/// and per-monitor DPI transitions - a large amount of fiddly code and a long tail of bugs, in
/// exchange for a look these three attributes get us for about thirty lines.
///
/// Every call is best-effort: these attribute ids were added across different Windows 10/11
/// builds, and DwmSetWindowAttribute simply returns a failure HRESULT on a build that does not
/// know one. Ignoring that leaves the window looking slightly more ordinary on older Windows,
/// which is the correct degradation.
/// </summary>
internal static class Dwm
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerPreferenceRound = 2;
    private const int BackdropTypeMainWindow = 2; // Mica

    [DllImport("dwmapi.dll", SetLastError = false)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies the current theme to a window's title bar. Must be re-applied whenever the theme
    /// flips: the attribute is per-window state, not a live binding to the system setting.
    /// </summary>
    public static void ApplyTitleBarTheme(IWin32Window window, bool dark)
    {
        int value = dark ? 1 : 0;
        TrySet(window.Handle, DwmwaUseImmersiveDarkMode, ref value);
    }

    /// <summary>Rounded corners plus a Mica backdrop - the Windows 11 app-window idiom.</summary>
    public static void ApplyWindowStyling(IWin32Window window, bool mica)
    {
        int corner = CornerPreferenceRound;
        TrySet(window.Handle, DwmwaWindowCornerPreference, ref corner);

        if (mica)
        {
            int backdrop = BackdropTypeMainWindow;
            TrySet(window.Handle, DwmwaSystemBackdropType, ref backdrop);
        }
    }

    private static void TrySet(IntPtr hwnd, int attribute, ref int value)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No dwmapi.dll at all - nothing to degrade to, and nothing worth reporting.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
