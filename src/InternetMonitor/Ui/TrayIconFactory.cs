using System.Reflection;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui;

/// <summary>
/// Owns an unmanaged HICON created from a <see cref="Bitmap"/>. <see cref="Bitmap.GetHicon"/>
/// hands back a handle the GC knows nothing about, so without this wrapper every tray-icon update
/// would leak one icon handle - and the tray icon changes on every state change, for days.
/// </summary>
public sealed class ManagedIcon : IDisposable
{
    private readonly IntPtr _handle;
    private bool _disposed;

    public ManagedIcon(Bitmap bitmap)
    {
        _handle = bitmap.GetHicon();
        Icon = Icon.FromHandle(_handle);
    }

    public Icon Icon { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Icon.Dispose();
        NativeMethods.DestroyIcon(_handle);
    }
}

/// <summary>
/// Builds the tray icon: the app's own artwork with a small status badge composited into its
/// bottom-right corner, which the source SVG deliberately leaves clear for exactly this.
///
/// The badge colour comes from the theme palette like everything else, so the tray icon and the
/// window agree on what "warning" looks like. Badge severity - not the window's theme - is the
/// only input: a tray icon sits on the taskbar, whose colour the app does not control.
/// </summary>
public static class TrayIconFactory
{
    private const string IconResourceName = "InternetMonitor.Assets.app.ico";

    private static readonly Lazy<Bitmap> BaseBitmap = new(() => LoadBitmap(32));
    public static readonly Lazy<Icon> AppIcon = new(LoadAppIcon);

    /// <summary>
    /// Composites a badge for <paramref name="severity"/> over the base artwork. The caller owns
    /// the returned <see cref="ManagedIcon"/> and must dispose the previous one after assigning
    /// the new one to <see cref="NotifyIcon.Icon"/> - never before, or the tray briefly renders a
    /// destroyed handle.
    /// </summary>
    public static ManagedIcon CreateTrayIcon(Severity severity)
    {
        var bitmap = new Bitmap(BaseBitmap.Value);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.UseHighQuality();

            int size = bitmap.Width * 12 / 32;
            var badge = new Rectangle(bitmap.Width - size - 2, bitmap.Height - size - 2, size, size);

            using var brush = new SolidBrush(SeverityColors.Strong(severity));
            g.FillEllipse(brush, badge);

            // A white ring so the badge stays legible against both a light and a dark taskbar.
            using var outline = new Pen(Color.White, bitmap.Width / 21f);
            g.DrawEllipse(outline, badge);
        }

        var icon = new ManagedIcon(bitmap);
        bitmap.Dispose();
        return icon;
    }

    /// <summary>A plain, un-badged bitmap of the app artwork, for the About screen and the outage toast.</summary>
    public static Bitmap LoadLogoBitmap(int size = 48) => LoadBitmap(size);

    private static Bitmap LoadBitmap(int size)
    {
        using Stream stream = OpenIconStream();
        using var icon = new Icon(stream, new Size(size, size));
        return icon.ToBitmap();
    }

    private static Icon LoadAppIcon()
    {
        using Stream stream = OpenIconStream();
        return new Icon(stream);
    }

    private static Stream OpenIconStream() =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResourceName)
        ?? throw new FileNotFoundException($"Embedded icon resource not found: {IconResourceName}");
}
