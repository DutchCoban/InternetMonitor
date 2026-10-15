using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The surface every page is built out of: a rounded panel with a soft shadow, an optional title
/// row, and a content area that child controls dock into.
///
/// <see cref="DisplayRectangle"/> is overridden rather than asking callers to position children
/// below the header themselves. That is what makes <c>card.Controls.Add(x)</c> with
/// <c>x.Dock = DockStyle.Fill</c> land in the right place: WinForms' layout engine asks a
/// container for its display rectangle, so the header and the insets are subtracted once, here,
/// instead of in every page that happens to use a card.
///
/// Insets come from <see cref="ThemedControl.Sp"/> rather than the inherited
/// <see cref="Control.Padding"/> property, because Padding is set at construction time - before
/// the control has a handle and therefore before its monitor's DPI is known. Computing them on
/// demand means a card dragged to a 150% monitor re-lays-out correctly instead of keeping 96-DPI
/// gaps.
/// </summary>
public class Card : ThemedControl
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private Glyph? _glyph;
    private bool _recessed;

    public Card()
    {
        ApplySurfaceColor();
    }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value ?? string.Empty;
            PerformLayout();
            Invalidate();
        }
    }

    /// <summary>A short line of context under the title; takes no space at all when empty.</summary>
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle == value)
            {
                return;
            }

            _subtitle = value ?? string.Empty;
            PerformLayout();
            Invalidate();
        }
    }

    public Glyph? HeaderGlyph
    {
        get => _glyph;
        set
        {
            _glyph = value;
            Invalidate();
        }
    }

    /// <summary>
    /// Set false for a card that is purely a grouping surface inside another card, where a second
    /// shadow would read as a smudge rather than as depth.
    /// </summary>
    public bool ShowShadow { get; set; } = true;

    /// <summary>Uses the slightly recessed surface tone - for a card nested inside a card.</summary>
    public bool Recessed
    {
        get => _recessed;
        set
        {
            if (_recessed == value)
            {
                return;
            }

            _recessed = value;
            ApplySurfaceColor();
            Invalidate();
        }
    }

    /// <summary>
    /// The gap between the card's edge and its content, in device pixels. Public rather than
    /// protected: code outside Card that manually positions children inside one (rather than
    /// relying on Dock/Anchor, which already read DisplayRectangle automatically) needs this same
    /// number to line up with it - SettingsPage's row layout is exactly that case.
    /// </summary>
    public int Inset => Sp(4);

    public override Rectangle DisplayRectangle
    {
        get
        {
            Rectangle r = ClientRectangle;
            return Rectangle.FromLTRB(
                r.Left + Inset,
                r.Top + HeaderHeight + Inset,
                Math.Max(r.Left + Inset, r.Right - Inset),
                Math.Max(r.Top + HeaderHeight + Inset, r.Bottom - Inset));
        }
    }

    /// <summary>
    /// Height of the title block, measured from the actual text rather than assumed, and zero when
    /// there is no title at all - a card with no header has no dead space at the top.
    /// </summary>
    protected int HeaderHeight
    {
        get
        {
            if (_title.Length == 0 && _subtitle.Length == 0)
            {
                return 0;
            }

            int height = Sp(3);
            if (_title.Length > 0)
            {
                height += GdiExtensions.MeasureCrisp(_title, Theme.Font(FontRole.Heading)).Height;
            }

            if (_subtitle.Length > 0)
            {
                height += Dp(2) + GdiExtensions.MeasureCrisp(_subtitle, Theme.Font(FontRole.Small)).Height;
            }

            return height;
        }
    }

    protected override void OnThemeChanged() => ApplySurfaceColor();

    /// <summary>
    /// BackColor is not what this control paints - <see cref="ThemedControl.OnPaintBackground"/>
    /// paints the page colour so the rounded corners show through. It is kept in step with the
    /// card's surface tone purely so that child controls asking "what is behind me?" get the right
    /// answer.
    /// </summary>
    private void ApplySurfaceColor() =>
        BackColor = Recessed ? Theme.Current.SurfaceAlt : Theme.Current.Surface;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        // Inset by the shadow depth so the shadow has somewhere to go inside our own bounds -
        // otherwise it is clipped away and the card reads as flat.
        int gap = ShowShadow ? Dp(4) : 0;
        var bounds = new RectangleF(
            gap,
            gap,
            Math.Max(0, Width - (gap * 2)),
            Math.Max(0, Height - (gap * 2)));
        float radius = Theme.RadiusCard(DeviceDpi);

        if (ShowShadow)
        {
            g.DrawShadow(bounds, radius, p.Shadow, Dp(4));
        }

        g.FillRounded(bounds, radius, Recessed ? p.SurfaceAlt : p.Surface);
        g.DrawRounded(bounds, radius, p.Border);

        if (_title.Length == 0 && _subtitle.Length == 0)
        {
            return;
        }

        int x = Inset;
        int y = Sp(3);
        int right = Width - Inset;

        if (_glyph is { } glyph)
        {
            int size = Dp(18);
            Icons.Draw(g, glyph, new RectangleF(x, y, size, size), p.Accent, 1.9f);
            x += size + Sp(2);
        }

        if (_title.Length > 0)
        {
            Size size = GdiExtensions.MeasureCrisp(_title, Theme.Font(FontRole.Heading));
            g.DrawTextCrisp(
                _title,
                Theme.Font(FontRole.Heading),
                p.TextPrimary,
                new Rectangle(x, y, Math.Max(0, right - x), size.Height));
            y += size.Height + Dp(2);
        }

        if (_subtitle.Length > 0)
        {
            Size size = GdiExtensions.MeasureCrisp(_subtitle, Theme.Font(FontRole.Small));
            g.DrawTextCrisp(
                _subtitle,
                Theme.Font(FontRole.Small),
                p.TextMuted,
                new Rectangle(Inset, y, Math.Max(0, right - Inset), size.Height));
        }
    }
}
