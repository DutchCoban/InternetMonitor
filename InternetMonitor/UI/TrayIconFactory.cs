using System.Drawing.Drawing2D;
using System.Reflection;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.UI;

internal static class TrayIconFactory
{
    private static readonly Lazy<Bitmap> BaseBitmap = new(LoadBaseBitmap);

    /// <summary>
    /// The app's own icon (all embedded resolutions, not just the 32x32 used for the tray badge
    /// composite), for use as every secondary window's title-bar icon. A Form that never sets
    /// its own Icon falls back to a generic default WinForms icon rather than this app's icon -
    /// every top-level Form in this app should set <c>Icon = TrayIconFactory.AppIcon.Value;</c>.
    /// Loaded once and never disposed (shared for the process lifetime, same pattern as
    /// <see cref="BaseBitmap"/>) - assigning the same Icon instance to multiple forms is safe
    /// since Form.Icon is just a reference, not ownership transfer.
    /// </summary>
    public static readonly Lazy<Icon> AppIcon = new(LoadAppIcon);

    public static ManagedIcon Build(ProbeStatus severity)
    {
        Color badgeColor = severity switch
        {
            ProbeStatus.Ok => Color.LimeGreen,
            ProbeStatus.Warning => Color.Orange,
            ProbeStatus.Error or ProbeStatus.Blocked => Color.Red,
            _ => Color.Gray, // Unknown/Checking - before the first diagnosis cycle completes
        };

        using var bmp = new Bitmap(BaseBitmap.Value);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var badgeRect = new Rectangle(bmp.Width - 14, bmp.Height - 14, 12, 12);
            using var brush = new SolidBrush(badgeColor);
            g.FillEllipse(brush, badgeRect);
            using var outline = new Pen(Color.White, 1.5f);
            g.DrawEllipse(outline, badgeRect);
        }

        IntPtr hIcon = bmp.GetHicon();
        return ManagedIcon.FromOwnedHandle(hIcon);
    }

    /// <summary>Plain logo bitmap (no state badge) for display inside popups/about screens.</summary>
    public static Bitmap LoadLogoBitmap(int size = 48) => new(BaseBitmap.Value, new Size(size, size));

    private static Bitmap LoadBaseBitmap()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("InternetMonitor.Assets.app.ico")
            ?? throw new FileNotFoundException("Embedded app.ico resource not found.");
        using var icon = new Icon(stream, 32, 32);
        return icon.ToBitmap();
    }

    private static Icon LoadAppIcon()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("InternetMonitor.Assets.app.ico")
            ?? throw new FileNotFoundException("Embedded app.ico resource not found.");
        return new Icon(stream);
    }
}
