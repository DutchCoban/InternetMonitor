using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// A row of selectable pills - the range picker on a chart, the step picker on the History page.
///
/// One owner-drawn control rather than a row of radio buttons or toggle buttons: the chips need to
/// size themselves to translated text, wrap when the window is narrow, and look like the rest of
/// the app rather than like Win32. Hit-testing a list of rectangles is less code than keeping a
/// dozen child controls aligned.
/// </summary>
public sealed class ChipGroup : ThemedControl
{
    public sealed record Chip(string Id, string Label);

    private readonly List<Chip> _chips = [];
    private readonly List<Rectangle> _bounds = [];
    private int _selectedIndex;
    private int _hoverIndex = -1;

    public ChipGroup()
    {
        Cursor = Cursors.Hand;
        Height = Dp(30);
    }

    public event Action<string>? SelectionChanged;

    protected override Color BehindColor => Theme.Current.Surface;

    public string? SelectedId => _selectedIndex >= 0 && _selectedIndex < _chips.Count ? _chips[_selectedIndex].Id : null;

    public void SetChips(IEnumerable<Chip> chips, string? selectedId = null)
    {
        _chips.Clear();
        _chips.AddRange(chips);
        _selectedIndex = Math.Max(0, _chips.FindIndex(c => c.Id == selectedId));
        Relayout();
        Invalidate();
    }

    public void Select(string id, bool raiseEvent = true)
    {
        int index = _chips.FindIndex(c => c.Id == id);
        if (index < 0 || index == _selectedIndex)
        {
            return;
        }

        _selectedIndex = index;
        Invalidate();

        if (raiseEvent)
        {
            SelectionChanged?.Invoke(id);
        }
    }

    /// <summary>
    /// Measures every chip and wraps onto further rows when the width runs out, growing the
    /// control's height to match. A fixed single row would push the last chips off the edge at
    /// narrow widths or in a language with longer words.
    /// </summary>
    private void Relayout()
    {
        _bounds.Clear();
        int chipHeight = Dp(26);
        int x = 0;
        int y = 0;

        foreach (Chip chip in _chips)
        {
            int width = GdiExtensions.MeasureCrisp(chip.Label, Theme.Font(FontRole.Small)).Width + (Sp(3) * 2);
            if (x > 0 && x + width > Width)
            {
                x = 0;
                y += chipHeight + Sp(1);
            }

            _bounds.Add(new Rectangle(x, y, width, chipHeight));
            x += width + Sp(1);
        }

        int needed = y + chipHeight;
        if (Height != needed)
        {
            Height = needed;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    protected override void OnThemeChanged() => Relayout();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = _bounds.FindIndex(b => b.Contains(e.Location));
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        int index = _bounds.FindIndex(b => b.Contains(e.Location));
        if (index >= 0)
        {
            Select(_chips[index].Id);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        for (int i = 0; i < _chips.Count && i < _bounds.Count; i++)
        {
            Rectangle bounds = _bounds[i];
            bool selected = i == _selectedIndex;
            bool hovered = i == _hoverIndex;

            var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1);

            g.FillRounded(rect, bounds.Height, selected ? p.Accent : hovered ? p.SurfaceHover : p.SurfaceAlt);

            // A resting chip leans on its fill alone rather than a border too - with a whole row
            // of them, a border on every single one is what reads as "busy". The border comes
            // back only on hover, as feedback that this one is about to be clicked.
            if (hovered && !selected)
            {
                g.DrawRounded(rect, bounds.Height, p.Border);
            }

            g.DrawTextCrisp(
                _chips[i].Label,
                selected ? Theme.Font(FontRole.SmallStrong) : Theme.Font(FontRole.Small),
                selected ? p.AccentText : p.TextSecondary,
                bounds,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
