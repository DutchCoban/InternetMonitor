using InternetMonitor.Native;

namespace InternetMonitor.UI;

/// <summary>
/// Wraps an <see cref="Icon"/> created from a GDI HICON handle (e.g. via Bitmap.GetHicon()).
/// Icon.FromHandle does not take ownership of the handle, so it must be destroyed explicitly
/// via DestroyIcon or it leaks a GDI object. Since the tray icon is replaced on every
/// connectivity state transition, an unmanaged leak here would grow without bound over a
/// long-running session.
/// </summary>
public sealed class ManagedIcon : IDisposable
{
    public Icon Icon { get; }

    private readonly IntPtr _hIcon;
    private bool _disposed;

    private ManagedIcon(Icon icon, IntPtr hIcon)
    {
        Icon = icon;
        _hIcon = hIcon;
    }

    public static ManagedIcon FromOwnedHandle(IntPtr hIcon) => new(Icon.FromHandle(hIcon), hIcon);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Icon.Dispose();
        NativeMethods.DestroyIcon(_hIcon);
        _disposed = true;
    }
}
