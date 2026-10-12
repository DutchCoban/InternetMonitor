using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// A compact rounded badge carrying a severity: tinted background, severity-coloured shape, and a
/// short label. Used for the step states, the incident list and the tray flyout.
///
/// The shape is not decoration. Status in this app is never communicated by colour alone, so the
/// glyph comes from <see cref="SeverityColors.Shape"/> and is drawn for every severity including
/// idle - a user who cannot distinguish the green from the red still sees a tick versus a cross.
/// </summary>
public sealed class StatusPill : ThemedControl
{
    private Severity _severity = Severity.Idle;
    private string _label = string.Empty;

    public StatusPill()
    {
        // A pill is a label, not a target: it must never swallow a click meant for the row it
        // sits on.
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

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value)
            {
                return;
            }

            _label = value ?? string.Empty;
            Invalidate();
        }
    }

    /// <summary>Hides the label and draws just the shape, for dense rows.</summary>
    public bool ShapeOnly { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int height = Dp(22);
        if (ShapeOnly || _label.Length == 0)
        {
            return new Size(height, height);
        }

        Size text = GdiExtensions.MeasureCrisp(_label, Theme.Font(FontRole.SmallStrong));
        return new Size(Dp(13) + Sp(1) + text.Width + (Sp(2) * 2), height);
    }

    public void SizeToContent() => Size = GetPreferredSize(Size.Empty);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();

        Color strong = SeverityColors.Strong(_severity);
        Color soft = SeverityColors.Soft(_severity);
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        // A radius larger than the height always clamps to a true pill in GdiExtensions.
        g.FillRounded(bounds, Height, soft);
        g.DrawRounded(bounds, Height, strong.WithAlpha(90));

        int shape = Dp(13);
        float shapeX = ShapeOnly || _label.Length == 0 ? (Width - shape) / 2f : Sp(2);
        Icons.Draw(g, SeverityColors.Shape(_severity), new RectangleF(shapeX, (Height - shape) / 2f, shape, shape), strong, 2.4f);

        if (ShapeOnly || _label.Length == 0)
        {
            return;
        }

        int textX = (int)(shapeX + shape + Sp(1));
        g.DrawTextCrisp(
            _label,
            Theme.Font(FontRole.SmallStrong),
            strong,
            new Rectangle(textX, 0, Math.Max(0, Width - textX - Sp(2)), Height));
    }
}
