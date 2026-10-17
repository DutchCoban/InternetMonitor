using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Localization;
using InternetMonitor.Net;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Windows;

/// <summary>
/// A brief, self-dismissing notice in the bottom-right corner: a connection problem has just been
/// detected, or - the same shape in the OK colour - it is back. Custom-painted in the app's own
/// theme rather than a stock balloon, so it looks like the app and not like Windows; the Windows
/// notification is the other choice in Settings (see <see cref="App.TrayController.ShowBalloon"/>).
///
/// Bottom-right rather than next to the tray icon itself: the icon's position is not exposed to
/// a plain WinForms app, and the corner of the primary screen's working area is visually
/// indistinguishable from "near the tray" on the overwhelming majority of taskbar layouts.
/// </summary>
public sealed class IncidentToast : Form
{
    private const int AutoCloseMs = 7000;
    private const int MarginDp = 16;

    private static IncidentToast? _current;

    private readonly System.Windows.Forms.Timer _autoClose;
    private readonly string _title;
    private readonly string _body;
    private readonly bool _resolved;
    private readonly bool _opensLoginPage;

    private IncidentToast(string title, string body, bool resolved, bool opensLoginPage)
    {
        _title = title;
        _body = body;
        _resolved = resolved;
        _opensLoginPage = opensLoginPage;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Current.Bg;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        DoubleBuffered = true;

        ClientSize = new Size(Dp(340), Dp(92));

        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        int margin = Dp(MarginDp);
        Location = new Point(workingArea.Right - Width - margin, workingArea.Bottom - Height - margin);

        // For a login page the toast is the quickest way to the fix, so a click opens it; for
        // anything else a click just dismisses.
        Click += (_, _) =>
        {
            if (_opensLoginPage)
            {
                ShellOpen.OpenUrl(CaptivePortalProbe.LoginPageUrl);
            }

            Close();
        };

        _autoClose = new System.Windows.Forms.Timer { Interval = AutoCloseMs };
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();
    }

    /// <summary>
    /// Shows a toast for a just-opened incident, replacing any still-visible one rather than
    /// stacking - a second incident opening moments after the first is rare, and when it happens
    /// the newer problem is the more relevant thing to show.
    /// </summary>
    public static void ShowFor(Incident incident) =>
        Show(LocalizationManager.Instance.Get("incident.toast.title"), incident.Headline, resolved: false, incident.Cause == RootCause.CaptivePortal);

    /// <summary>The counterpart to <see cref="ShowFor"/>: the connection is back, and for how long it was gone.</summary>
    public static void ShowResolved(Incident incident)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        Show(loc.Get("incident.toast.resolved.title"), loc.Format("incident.toast.resolved.text", IncidentTracker.FormatDuration(incident.Duration)), resolved: true, opensLoginPage: false);
    }

    private static void Show(string title, string body, bool resolved, bool opensLoginPage)
    {
        _current?.Close();

        var toast = new IncidentToast(title, body, resolved, opensLoginPage);
        _current = toast;
        toast.Show();
    }

    private int Dp(int px) => Theme.Dp(px, DeviceDpi);

    private int Sp(int step) => Theme.Space(step, DeviceDpi);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        Color accent = _resolved ? p.Ok : p.Error;
        Color accentSoft = _resolved ? p.OkSoft : p.ErrorSoft;

        int gap = Dp(2);
        var bounds = new RectangleF(gap, gap, Width - (gap * 2), Height - (gap * 2));
        float radius = Theme.RadiusCard(DeviceDpi);

        g.DrawShadow(bounds, radius, p.Shadow, Dp(6));
        g.FillRounded(bounds, radius, p.Surface);
        g.DrawRounded(bounds, radius, GdiExtensions.Mix(p.Border, accent, 0.35f));

        using (System.Drawing.Drawing2D.GraphicsPath clip = GdiExtensions.RoundedPath(bounds, radius))
        {
            System.Drawing.Drawing2D.GraphicsState state = g.Save();
            g.SetClip(clip);
            g.FillRectangle(GdiExtensions.BrushFor(accent), bounds.Left, bounds.Top, Dp(4), bounds.Height);
            g.Restore(state);
        }

        int left = (int)bounds.Left + Sp(4);
        int top = (int)bounds.Top + Sp(3);
        int iconSize = Dp(22);
        int iconBox = iconSize + Dp(10);

        g.FillRounded(new RectangleF(left, top, iconBox, iconBox), iconBox / 2f, accentSoft);
        Icons.Draw(g, _resolved ? Glyph.Check : Glyph.Warning, new RectangleF(left + Dp(5), top + Dp(5), iconSize, iconSize), accent, 2.2f);

        int textLeft = left + iconBox + Sp(3);
        int right = (int)bounds.Right - Sp(4);

        g.DrawTextCrisp(
            _title,
            Theme.Font(FontRole.BodyStrong),
            p.TextPrimary,
            new Rectangle(textLeft, top, Math.Max(0, right - textLeft), Dp(18)));

        g.DrawTextCrisp(
            _body,
            Theme.Font(FontRole.Small),
            p.TextSecondary,
            new Rectangle(textLeft, top + Dp(20), Math.Max(0, right - textLeft), Dp(40)),
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _autoClose.Stop();
            _autoClose.Dispose();

            if (ReferenceEquals(_current, this))
            {
                _current = null;
            }
        }

        base.Dispose(disposing);
    }
}
