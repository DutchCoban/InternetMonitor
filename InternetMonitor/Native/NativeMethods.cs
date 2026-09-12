using System.Runtime.InteropServices;

namespace InternetMonitor.Native;

internal static class NativeMethods
{
    /// <summary>
    /// Frees a GDI icon handle (HICON). WinForms' <see cref="Icon.FromHandle"/> does not take
    /// ownership of the handle it wraps, so a handle created from a Bitmap (e.g. via
    /// Bitmap.GetHicon()) must be destroyed explicitly via this P/Invoke or it leaks - see
    /// <see cref="InternetMonitor.UI.ManagedIcon"/>, the sole caller, for the full rationale.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr handle);
}
