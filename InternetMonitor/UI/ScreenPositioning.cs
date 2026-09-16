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

        // Clamped to the working area's top-left origin: if the form is taller/wider than the
        // working area (a small/scaled screen combined with a form whose height grows with
        // user data, e.g. StatusPopupForm with several configured endpoints), the unclamped
        // formula below would go negative and push the form's *top* - its title bar and topmost
        // content - above the visible screen with no way to reach it. Starting flush at the
        // working area's edge instead means an oversized form runs past the bottom/right edge
        // (behind/near the taskbar) rather than disappearing off the top/left entirely.
        int x = Math.Max(workingArea.Left, workingArea.Right - form.Width - MarginPixels);
        int y = Math.Max(workingArea.Top, workingArea.Bottom - form.Height - MarginPixels);
        form.Location = new Point(x, y);
    }
}
