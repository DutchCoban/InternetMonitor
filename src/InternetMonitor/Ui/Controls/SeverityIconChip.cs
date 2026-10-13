using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// A severity-tinted circular badge with a glyph centered inside it - the same "icon in a filled
/// chip" treatment <see cref="StatusHero"/> paints inline, pulled out as its own control so other
/// banners (the Diagnostics page's, so far) can use it without duplicating the drawing code or
/// being stuck with <see cref="StatusPill"/>'s fixed small glyph size.
///
/// Unlike <see cref="StatusPill"/>, the glyph scales with the control's own size (10dp of total
/// padding, split evenly - the same ratio <see cref="StatusHero"/> uses for its 36dp chip around a
/// 26dp glyph) rather than a fixed 13dp regardless of how big the badge is drawn.
/// </summary>
public sealed class SeverityIconChip : ThemedControl
{
    private Severity _severity = Severity.Idle;
    private Glyph? _glyph;

    public SeverityIconChip()
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
    }

    public Severity Severity
    {
        get => _severity;
        set
        {
            if (_severity == value)
            {
                return;
            }

            _severity = value;
            Invalidate();
        }
    }

    /// <summary>Overrides the severity's own shape - for a "checking" spinner glyph while a severity is already known.</summary>
    public Glyph? Glyph
    {
        get => _glyph;
        set
        {
            if (_glyph == value)
            {
                return;
            }

            _glyph = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();

        Color soft = SeverityColors.Soft(_severity);
        Color strong = SeverityColors.Strong(_severity);
        var bounds = new RectangleF(0, 0, Width, Height);
        g.FillRounded(bounds, Math.Min(Width, Height) / 2f, soft);

        int padding = Dp(5);
        int shape = Math.Max(Dp(8), Math.Min(Width, Height) - (padding * 2));
        float x = (Width - shape) / 2f;
        float y = (Height - shape) / 2f;
        Icons.Draw(g, _glyph ?? SeverityColors.Shape(_severity), new RectangleF(x, y, shape, shape), strong, 2.2f);
    }
}
