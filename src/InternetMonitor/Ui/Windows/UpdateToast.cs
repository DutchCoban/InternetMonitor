using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Windows;

/// <summary>Which of the three update messages the toast shows.</summary>
public enum UpdateToastKind
{
    /// <summary>A newer release exists and auto-install is off - informational, links to the release page.</summary>
    Available,

    /// <summary>A newer release is downloaded and about to be installed - the restart happens regardless of whether this toast is clicked or dismissed.</summary>
    Installing,

    /// <summary>Manual "Check for updates" found nothing newer.</summary>
    UpToDate,

    /// <summary>Manual "Check for updates" could not complete (offline, GitHub unreachable, ...).</summary>
    CheckFailed,
}

/// <summary>
/// A brief, self-dismissing notice about the update check - same shape as <see cref="IncidentToast"/>
/// (custom-painted in the app's own theme, bottom-right corner, auto-closing), but covering four
/// outcomes instead of always the same incident styling: <see cref="UpdateToastKind.Available"/>
/// and <see cref="UpdateToastKind.Installing"/> come from the background check, while
/// <see cref="UpdateToastKind.UpToDate"/> and <see cref="UpdateToastKind.CheckFailed"/> only ever
/// come from a manual "Check for updates" - v2 has exactly one place that uses a stock
/// <see cref="MessageBox"/> (see <c>IncidentsPage.OnClearClicked</c>'s "the one place in this app
/// that does"), so these simple pass/fail outcomes belong here too, not in a second one.
/// </summary>
public sealed class UpdateToast : Form
{
    private const int AutoCloseMs = 8000;
    private const int MarginDp = 16;

    private static UpdateToast? _current;

    private readonly System.Windows.Forms.Timer _autoClose;
    private readonly UpdateToastKind _kind;
    private readonly string _title;
    private readonly string _body;
    private readonly string? _releaseUrl;

    private UpdateToast(UpdateToastKind kind, string title, string body, string? releaseUrl)
    {
        _kind = kind;
        _title = title;
        _body = body;
        _releaseUrl = releaseUrl;

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

        Click += (_, _) =>
        {
            if (_releaseUrl is { Length: > 0 } url)
            {
                // Nothing sensible to do if the shell can't open a browser - the toast just closes below regardless.
                ShellOpen.Open(url);
            }

            Close();
        };

        _autoClose = new System.Windows.Forms.Timer { Interval = AutoCloseMs };
        _autoClose.Tick += (_, _) => Close();
        _autoClose.Start();
    }

    /// <summary>
    /// Shows a toast for the given outcome, replacing any still-visible one rather than stacking -
    /// same reasoning as <see cref="IncidentToast.ShowFor"/>.
    /// </summary>
    public static void Show(UpdateToastKind kind, string title, string body, string? releaseUrl = null)
    {
        _current?.Close();

        var toast = new UpdateToast(kind, title, body, releaseUrl);
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

        (Glyph glyph, Color accent, Color accentSoft) = _kind switch
        {
            UpdateToastKind.Available or UpdateToastKind.Installing => (Glyph.Refresh, p.Accent, p.AccentSoft),
            UpdateToastKind.UpToDate => (Glyph.Check, p.Ok, p.OkSoft),
            UpdateToastKind.CheckFailed => (Glyph.Warning, p.Warn, p.WarnSoft),
            _ => (Glyph.Refresh, p.Accent, p.AccentSoft),
        };

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
            using var edge = new SolidBrush(accent);
            g.FillRectangle(edge, bounds.Left, bounds.Top, Dp(4), bounds.Height);
            g.Restore(state);
        }

        int left = (int)bounds.Left + Sp(4);
        int top = (int)bounds.Top + Sp(3);
        int iconSize = Dp(22);
        int iconBox = iconSize + Dp(10);

        g.FillRounded(new RectangleF(left, top, iconBox, iconBox), iconBox / 2f, accentSoft);
        Icons.Draw(g, glyph, new RectangleF(left + Dp(5), top + Dp(5), iconSize, iconSize), accent, 2.2f);

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
