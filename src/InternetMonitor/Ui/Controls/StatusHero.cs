using System.Drawing.Drawing2D;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Net;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The one card that answers the question people actually open this app to ask: does it work?
///
/// A large verdict, one line of explanation underneath, and a short row of the numbers worth
/// knowing - current latency, when the last check ran. Everything else on the page explains this
/// card; nothing on the page contradicts it.
///
/// The verdict is phrased from the first failed step, not from a count of failures, so an
/// unplugged cable says "no cable connected" and not "5 checks failed".
/// </summary>
public sealed class StatusHero : ThemedControl
{
    private Severity _severity = Severity.Idle;
    private string _headline = string.Empty;
    private string _explanation = string.Empty;
    private string _latency = string.Empty;
    private string _lastCheck = string.Empty;
    private bool _checking;

    private bool _networkHealthEnabled;
    private int _networkHealthOpenCount;

    // Shown only while a login page is in the way: the one state where the hero itself can offer
    // the fix, not just name the problem.
    private readonly AppButton _loginButton;

    public StatusHero()
    {
        _loginButton = new AppButton { Variant = ButtonVariant.Primary, Glyph = Glyph.Globe, Visible = false };
        _loginButton.Click += (_, _) => ShellOpen.OpenUrl(CaptivePortalProbe.LoginPageUrl);
        Controls.Add(_loginButton);
    }

    protected override Color BehindColor => Theme.Current.Bg;

    public int PreferredHeight => Dp(146);

    /// <summary>
    /// Derives the whole card from a snapshot. Keeping this mapping in one method means the hero
    /// can never disagree with the step list below it - they read the same object.
    /// </summary>
    public void Apply(StepSnapshot? snapshot, bool checking)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _checking = checking;

        if (snapshot is null)
        {
            _severity = Severity.Idle;
            _headline = loc.Get("hero.checking.title");
            _explanation = loc.Get("hero.checking.detail");
            _latency = string.Empty;
            _lastCheck = string.Empty;
            Invalidate();
            return;
        }

        StepResult? failure = snapshot.FirstFailure;
        bool loginPage = snapshot.CaptivePortal && snapshot.StateOf(StepId.Gateway) != StepState.Failed;
        if (loginPage)
        {
            // A login page fails DNS, pings and HTTPS together, but nothing is broken: the user
            // has to log in. Warning colour rather than error, and the step 7 headline rather
            // than whichever of those failures happened to come first.
            _severity = Severity.Warning;
            _headline = loc.Get("hero.portal.title");
            _explanation = snapshot[StepId.Target]?.Headline ?? string.Empty;
        }
        else if (failure is not null)
        {
            _severity = Severity.Error;
            _headline = loc.Get("hero.problem.title");
            _explanation = failure.Headline;
        }
        else if (snapshot.AnyWarning)
        {
            StepResult? warning = snapshot.All
                .Where(r => r.State == StepState.Warning)
                .OrderBy(r => (int)r.Step)
                .FirstOrDefault();

            _severity = Severity.Warning;
            _headline = loc.Get("hero.degraded.title");
            _explanation = warning?.Detail ?? warning?.Headline ?? string.Empty;
        }
        else
        {
            _severity = Severity.Ok;
            _headline = loc.Get("hero.ok.title");
            _explanation = loc.Get("hero.ok.detail");
        }

        // Latency comes from the public-internet step rather than the gateway: it is the number
        // that reflects what the user experiences, not how fast their own router answers.
        double? latency = snapshot[StepId.PublicIp]?.ChartValue ?? snapshot[StepId.Target]?.ChartValue;
        _latency = latency is { } ms ? $"{ms:F0} ms" : "-";
        _lastCheck = snapshot.TakenUtc.ToLocalTime().ToString("HH:mm:ss");

        _loginButton.Text = loc.Get("hero.portal.open");
        _loginButton.SizeToContent();
        _loginButton.Visible = loginPage;
        PositionLoginButton();

        Invalidate();
    }

    /// <summary>Replaces the verdict with "paused" until the next cycle's <see cref="Apply"/> - there is none while paused.</summary>
    public void ShowPaused(DateTimeOffset? until)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _checking = false;
        _severity = Severity.Idle;
        _headline = loc.Get("hero.paused.title");
        _explanation = until is { } at
            ? loc.Format("hero.paused.detail", at.ToLocalTime().ToString("HH:mm"))
            : loc.Get("hero.paused.detail.indefinite");
        _loginButton.Visible = false;
        Invalidate();
    }

    private void PositionLoginButton()
    {
        int gap = Dp(4);
        _loginButton.Location = new Point(Width - gap - Sp(5) - _loginButton.Width, gap + Sp(4));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        PositionLoginButton();
    }

    /// <summary>
    /// Sets the brief network-health stat, separately from <see cref="Apply"/> - it comes from
    /// <see cref="Core.Monitors.NetworkHealthMonitor"/>, not the step snapshot, and updates on its
    /// own schedule. <paramref name="enabled"/> hides the stat entirely rather than showing an
    /// always-OK one when the feature is off, matching how the Diagnostics page's own summary card
    /// collapses to one quiet line instead of empty rows.
    /// </summary>
    public void SetNetworkHealth(bool enabled, int openCount)
    {
        if (_networkHealthEnabled == enabled && _networkHealthOpenCount == openCount)
        {
            return;
        }

        _networkHealthEnabled = enabled;
        _networkHealthOpenCount = openCount;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        int gap = Dp(4);
        var bounds = new RectangleF(gap, gap, Math.Max(0, Width - (gap * 2)), Math.Max(0, Height - (gap * 2)));
        float radius = Theme.RadiusCard(DeviceDpi);

        Color strong = SeverityColors.Strong(_severity);

        g.DrawShadow(bounds, radius, p.Shadow, Dp(4));

        // A whisper of the severity colour across the card rather than a flat fill: enough to
        // read the state from across the room, not so much that the text fights the background.
        g.FillRoundedGradient(bounds, radius, GdiExtensions.Mix(p.Surface, SeverityColors.Soft(_severity), 0.85f), p.Surface);
        g.DrawRounded(bounds, radius, GdiExtensions.Mix(p.Border, strong, 0.35f));

        // The severity bar down the leading edge, clipped to the card's own rounded corners.
        using (GraphicsPath clip = GdiExtensions.RoundedPath(bounds, radius))
        {
            GraphicsState state = g.Save();
            g.SetClip(clip);
            using var brush = new SolidBrush(strong);
            g.FillRectangle(brush, bounds.Left, bounds.Top, DpF(4f), bounds.Height);
            g.Restore(state);
        }

        int left = (int)bounds.Left + Sp(5);
        int top = (int)bounds.Top + Sp(4);
        int shape = Dp(26);

        g.FillRounded(new RectangleF(left, top, shape + Dp(10), shape + Dp(10)), (shape + Dp(10)) / 2f, SeverityColors.Soft(_severity));
        Icons.Draw(
            g,
            _checking ? Glyph.Refresh : SeverityColors.Shape(_severity),
            new RectangleF(left + Dp(5), top + Dp(5), shape, shape),
            strong,
            2.2f);

        int textLeft = left + shape + Dp(10) + Sp(3);
        int right = (int)bounds.Right - Sp(5);

        Size headlineSize = GdiExtensions.MeasureCrisp(_headline, Theme.Font(FontRole.Display));
        g.DrawTextCrisp(
            _headline,
            Theme.Font(FontRole.Display),
            p.TextPrimary,
            new Rectangle(textLeft, top, Math.Max(0, right - textLeft), headlineSize.Height));

        g.DrawTextCrisp(
            _explanation,
            Theme.Font(FontRole.Body),
            p.TextSecondary,
            new Rectangle(textLeft, top + headlineSize.Height + Dp(2), Math.Max(0, right - textLeft), Dp(20)));

        if (_latency.Length == 0)
        {
            return;
        }

        int statsTop = (int)bounds.Bottom - Sp(5) - Dp(28);
        DrawStat(g, p, loc.Get("hero.stat.latency"), _latency, p.TextPrimary, textLeft, statsTop);
        DrawStat(g, p, loc.Get("hero.stat.lastCheck"), _lastCheck, p.TextPrimary, textLeft + Dp(120), statsTop);

        if (_networkHealthEnabled)
        {
            string value = _networkHealthOpenCount == 0
                ? loc.Get("hero.stat.networkHealth.ok")
                : loc.Format("hero.stat.networkHealth.open", _networkHealthOpenCount);
            Color color = SeverityColors.Strong(_networkHealthOpenCount == 0 ? Severity.Ok : Severity.Error);
            DrawStat(g, p, loc.Get("hero.stat.networkHealth"), value, color, textLeft + Dp(240), statsTop);
        }
    }

    private void DrawStat(Graphics g, Palette p, string label, string value, Color valueColor, int x, int y)
    {
        g.DrawTextCrisp(label, Theme.Font(FontRole.Small), p.TextMuted, new Rectangle(x, y, Dp(116), Dp(14)));
        g.DrawTextCrisp(value, Theme.Font(FontRole.BodyStrong), valueColor, new Rectangle(x, y + Dp(14), Dp(116), Dp(18)));
    }
}
