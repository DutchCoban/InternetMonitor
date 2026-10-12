using System.Diagnostics.CodeAnalysis;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

public enum ButtonVariant
{
    /// <summary>The one call to action on a page - filled with the accent colour.</summary>
    Primary,

    /// <summary>Outlined. The default for everything else.</summary>
    Secondary,

    /// <summary>No border or fill until hovered - for toolbar-density actions.</summary>
    Ghost,

    /// <summary>Outlined in the error colour, for destructive actions like clearing history.</summary>
    Danger,
}

/// <summary>
/// A themed push button with an optional icon, three visual weights, and a hover/press transition
/// that is animated rather than snapped.
///
/// The animation is a single short timer that interpolates one float; that is enough to make the
/// UI feel responsive, and it stops itself the moment it reaches its target so an idle window
/// burns no CPU. (v1's pulse control ran a 30Hz timer whenever it was visible.)
/// </summary>
public sealed class AppButton : ThemedControl, IButtonControl
{
    private const int AnimationIntervalMs = 16;
    private const float AnimationStep = 0.22f;

    private readonly System.Windows.Forms.Timer _animation;
    private float _hoverAmount;
    private bool _hovered;
    private bool _pressed;
    private Glyph? _glyph;
    private string _text = string.Empty;

    public AppButton()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        _animation = new System.Windows.Forms.Timer { Interval = AnimationIntervalMs };
        _animation.Tick += OnAnimationTick;
    }

    public ButtonVariant Variant { get; set; } = ButtonVariant.Secondary;

    // ---- IButtonControl ------------------------------------------------------------------------
    // Lets a plain AppButton be used as a Form's AcceptButton/CancelButton (Enter/Escape in a
    // small dialog), which otherwise only a stock Button can be. DialogResult is left at None and
    // unused: callers that need the dialog to close on click already do so explicitly in their own
    // Click handler, so Button's usual "set DialogResult, which closes the form" side effect is
    // deliberately not replicated here - one close path, not two.

    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value)
    {
        // No distinct "this is the default button" visual treatment (yet) - implemented only so
        // AppButton satisfies the interface Form.AcceptButton/CancelButton require.
    }

    public void PerformClick()
    {
        if (Enabled)
        {
            OnClick(EventArgs.Empty);
        }
    }

    public Glyph? Glyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
        }
    }

    // AllowNull mirrors the base declaration: Control.Text accepts null and normalises it, and
    // without matching that the override is a nullability mismatch warning.
    [AllowNull]
    public override string Text
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            base.Text = _text;
            Invalidate();
        }
    }

    /// <summary>
    /// The button's natural size for the current text, icon and DPI. Pages size buttons from this
    /// instead of hardcoding widths - hardcoding is what let v1's three diagnostics buttons at
    /// x=470/610/750 overlap once their labels were translated into German.
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = GdiExtensions.MeasureCrisp(_text, Theme.Font(FontRole.Body));
        int width = (Sp(4) * 2) + text.Width;
        if (_glyph is not null)
        {
            width += Dp(16) + Sp(2);
        }

        return new Size(width, Math.Max(Dp(32), text.Height + (Sp(2) * 2)));
    }

    /// <summary>Resizes the button to exactly fit its current content.</summary>
    public void SizeToContent() => Size = GetPreferredSize(Size.Empty);

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        StartAnimation();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        StartAnimation();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Focus();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    private void StartAnimation()
    {
        if (!_animation.Enabled)
        {
            _animation.Start();
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        float target = _hovered && Enabled ? 1f : 0f;
        float delta = target - _hoverAmount;

        if (Math.Abs(delta) <= AnimationStep)
        {
            _hoverAmount = target;
            _animation.Stop();
        }
        else
        {
            _hoverAmount += Math.Sign(delta) * AnimationStep;
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Theme.RadiusControl(DeviceDpi);
        bool enabled = Enabled;

        (Color fill, Color border, Color fore) = Resolve(p, enabled);

        if (fill.A > 0)
        {
            g.FillRounded(bounds, radius, fill);
        }

        if (border.A > 0)
        {
            g.DrawRounded(bounds, radius, border, DpF(1.2f));
        }

        if (_pressed && enabled)
        {
            // A translucent scrim rather than a second palette entry: it darkens a filled button
            // and tints an unfilled one, which is the right feedback for both.
            g.FillRounded(bounds, radius, Color.FromArgb(28, Color.Black));
        }

        if (Focused && enabled)
        {
            g.DrawRounded(RectangleF.Inflate(bounds, -DpF(2.5f), -DpF(2.5f)), radius, p.Accent.WithAlpha(150), DpF(1.2f));
        }

        // Content is centred as one block (icon + gap + text) so the pair stays optically centred
        // at any width, instead of the text being centred and the icon pushing it off-axis.
        Size textSize = GdiExtensions.MeasureCrisp(_text, Theme.Font(FontRole.Body));
        int iconSize = _glyph is null ? 0 : Dp(16);
        int gap = _glyph is null || _text.Length == 0 ? 0 : Sp(2);
        int contentWidth = iconSize + gap + textSize.Width;
        int x = Math.Max(Sp(3), (Width - contentWidth) / 2);

        if (_glyph is { } glyph)
        {
            Icons.Draw(g, glyph, new RectangleF(x, (Height - iconSize) / 2f, iconSize, iconSize), fore, 1.9f);
            x += iconSize + gap;
        }

        if (_text.Length > 0)
        {
            g.DrawTextCrisp(
                _text,
                Theme.Font(FontRole.Body),
                fore,
                new Rectangle(x, 0, Math.Max(0, Width - x - Sp(1)), Height),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private (Color Fill, Color Border, Color Fore) Resolve(Palette p, bool enabled)
    {
        if (!enabled)
        {
            return Variant == ButtonVariant.Primary
                ? (p.IdleSoft, p.Border, p.TextMuted)
                : (Color.Transparent, p.Border, p.TextMuted);
        }

        return Variant switch
        {
            ButtonVariant.Primary => (
                GdiExtensions.Mix(p.Accent, p.AccentHover, _hoverAmount),
                Color.Transparent,
                p.AccentText),

            ButtonVariant.Ghost => (
                p.SurfaceHover.WithAlpha((int)(255 * _hoverAmount)),
                Color.Transparent,
                GdiExtensions.Mix(p.TextSecondary, p.TextPrimary, _hoverAmount)),

            ButtonVariant.Danger => (
                p.ErrorSoft.WithAlpha((int)(255 * _hoverAmount)),
                p.Error.WithAlpha(170),
                p.Error),

            _ => (
                GdiExtensions.Mix(p.Surface, p.SurfaceHover, _hoverAmount),
                GdiExtensions.Mix(p.Border, p.BorderStrong, _hoverAmount),
                p.TextPrimary),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Tick -= OnAnimationTick;
            _animation.Dispose();
        }

        base.Dispose(disposing);
    }
}
