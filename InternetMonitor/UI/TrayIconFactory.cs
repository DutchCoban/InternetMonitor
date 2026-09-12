using System.Drawing.Drawing2D;
using System.Reflection;
using InternetMonitor.Network;

namespace InternetMonitor.UI;

internal static class TrayIconFactory
{
    private static readonly Lazy<Bitmap> BaseBitmap = new(LoadBaseBitmap);

    public static ManagedIcon Build(ConnectivityState state)
    {
        Color badgeColor = state switch
        {
            ConnectivityState.Connected => Color.LimeGreen,
            ConnectivityState.Outage => Color.Red,
            ConnectivityState.SuspectedOutage => Color.Orange,
            _ => Color.Gray,
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
}
