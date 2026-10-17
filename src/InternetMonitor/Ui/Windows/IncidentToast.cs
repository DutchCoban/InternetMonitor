using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Net;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Windows;

/// <summary>
/// A brief, self-dismissing notice shown when a new incident opens - the v2 equivalent of v1's
/// outage pop-up, deliberately simplified. v1 tracked a minimum-visible-time and a separate
/// recovered balloon tip; nothing here needs that precision, since the only requirement is a short
/// heads-up that something is wrong, not a guaranteed-visible-for-N-seconds notification or a
/// matching "it's back" message.
///
/// Custom-painted in the app's own theme (matching how <see cref="Controls.StatusHero"/> paints
/// itself) rather than styled as a plain WinForms form the way v1's was - everything else in this
/// app looks like this, and a notification popping in with stock chrome would look like an error
/// dialog from a different program.
///
/// Positioned at the screen's bottom-right corner rather than precisely next to the tray icon: v2
/// has no equivalent of v1's tray-icon-position lookup (an undocumented native call this app does
/// not otherwise need), and the bottom-right corner is where Windows' own toasts appear anyway -
/// visually indistinguishable from "near the tray" on the overwhelming majority of taskbar layouts.
/// </summary>
public sealed class IncidentToast : Form
{
    private const int AutoCloseMs = 7000;
    private const int MarginDp = 16;

    private static IncidentToast? _current;

    private readonly System.Windows.Forms.Timer _autoClose;
    private readonly string _headline;

    private IncidentToast(Incident incident)
    {
        _headline = incident.Headline;

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
        bool opensLoginPage = incident.Cause == RootCause.CaptivePortal;
        Click += (_, _) =>
        {
            if (opensLoginPage)
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
    public static void ShowFor(Incident incident)
    {
        _current?.Close();

        var toast = new IncidentToast(incident);
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
        LocalizationManager loc = LocalizationManager.Instance;

        int gap = Dp(2);
        var bounds = new RectangleF(gap, gap, Width - (gap * 2), Height - (gap * 2));
        float radius = Theme.RadiusCard(DeviceDpi);

        g.DrawShadow(bounds, radius, p.Shadow, Dp(6));
        g.FillRounded(bounds, radius, p.Surface);
        g.DrawRounded(bounds, radius, GdiExtensions.Mix(p.Border, p.Error, 0.35f));

        using (System.Drawing.Drawing2D.GraphicsPath clip = GdiExtensions.RoundedPath(bounds, radius))
        {
            System.Drawing.Drawing2D.GraphicsState state = g.Save();
            g.SetClip(clip);
            using var edge = new SolidBrush(p.Error);
            g.FillRectangle(edge, bounds.Left, bounds.Top, Dp(4), bounds.Height);
            g.Restore(state);
        }

        int left = (int)bounds.Left + Sp(4);
        int top = (int)bounds.Top + Sp(3);
        int iconSize = Dp(22);
        int iconBox = iconSize + Dp(10);

        g.FillRounded(new RectangleF(left, top, iconBox, iconBox), iconBox / 2f, p.ErrorSoft);
        Icons.Draw(g, Glyph.Warning, new RectangleF(left + Dp(5), top + Dp(5), iconSize, iconSize), p.Error, 2.2f);

        int textLeft = left + iconBox + Sp(3);
        int right = (int)bounds.Right - Sp(4);

        g.DrawTextCrisp(
            loc.Get("incident.toast.title"),
            Theme.Font(FontRole.BodyStrong),
            p.TextPrimary,
            new Rectangle(textLeft, top, Math.Max(0, right - textLeft), Dp(18)));

        g.DrawTextCrisp(
            _headline,
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
