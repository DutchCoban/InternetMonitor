namespace InternetMonitor.UI;

internal static class ScreenPositioning
{
    private const int MarginPixels = 12;

    /// <summary>
    /// Positions a form in the bottom-right corner of the working area (i.e. above the
    /// taskbar) of the monitor the mouse is currently on. This is a pragmatic approximation
    /// of "the monitor with the tray" - precise tray-icon-rect detection would need
    /// SHAppBarData/Shell_NotifyIconGetRect P/Invoke, which isn't justified for this app.
    /// </summary>
    public static void PlaceNearTray(Form form)
    {
        Screen target = Screen.FromPoint(Cursor.Position);
        Rectangle workingArea = target.WorkingArea;
        int x = workingArea.Right - form.Width - MarginPixels;
        int y = workingArea.Bottom - form.Height - MarginPixels;
        form.Location = new Point(x, y);
    }
}
