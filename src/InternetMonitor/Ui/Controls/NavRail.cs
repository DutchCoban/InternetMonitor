using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;


/// <summary>
/// The left-hand navigation rail: the single piece of chrome that turns thirteen separate windows
/// into one application. Owner-drawn as one control rather than a stack of buttons, because the
/// selection indicator, hover highlight and badges all need to be positioned relative to each
/// other, and because hit-testing a list of rectangles is less code than keeping ten child
/// controls in sync.
///
/// Collapses to icons only below a width threshold the shell decides, so a narrow window keeps
/// its content area usable instead of giving a third of it to labels.
/// </summary>
public sealed class NavRail : ThemedControl
{
    /// <summary>
    /// One entry in the rail: the page id it switches to and its icon; the label comes from
    /// <c>nav.{Id}</c>.
    /// </summary>
    public sealed record Item(string Id, Glyph Glyph)
    {
        public string Label { get; set; } = string.Empty;

        /// <summary>A count drawn as a small accent badge; 0 hides it. Used for open incidents.</summary>
        public int Badge { get; set; }
    }

    private readonly List<Item> _items = [];
    private int _selectedIndex;
    private int _hoverIndex = -1;
    private bool _collapsed;
    private Bitmap? _logo;

    public NavRail()
    {
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public event Action<string>? SelectionChanged;

    public int ExpandedWidth => Dp(196);

    public int CollapsedWidth => Dp(60);

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value)
            {
                return;
            }

            _collapsed = value;
            Width = value ? CollapsedWidth : ExpandedWidth;
            Invalidate();
        }
    }

    public string? SelectedId => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex].Id : null;

    public void AddItem(Item item)
    {
        _items.Add(item);
        Invalidate();
    }

    public void SetBadge(string id, int count)
    {
        Item? item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null || item.Badge == count)
        {
            return;
        }

        item.Badge = count;
        Invalidate();
    }

    public void Select(string id)
    {
        int index = _items.FindIndex(i => i.Id == id);
        if (index < 0 || index == _selectedIndex)
        {
            return;
        }

        _selectedIndex = index;
        Invalidate();
        SelectionChanged?.Invoke(id);
    }

    /// <summary>Re-reads every label after a language change; the shell supplies the new text.</summary>
    public void RefreshLabels(Func<string, string> labelFor)
    {
        foreach (Item item in _items)
        {
            item.Label = labelFor(item.Id);
        }

        Invalidate();
    }

    /// <summary>The app name and mark above the navigation items; hidden when collapsed.</summary>
    public string BrandText { get; set; } = string.Empty;

    private int ItemHeight => Dp(42);

    private int BrandHeight => Dp(56);

    private int TopOffset => BrandHeight + Sp(2);

    private int IndexAt(Point location)
    {
        if (location.X < 0 || location.X > Width)
        {
            return -1;
        }

        int index = (location.Y - TopOffset) / Math.Max(1, ItemHeight);
        return index >= 0 && index < _items.Count ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Location);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1)
        {
            _hoverIndex = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();
        int index = IndexAt(e.Location);
        if (index >= 0)
        {
            Select(_items[index].Id);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    /// <summary>Up and down arrows move between pages, as in any list; the ends do not wrap around.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0)
        {
            return;
        }

        int next = e.KeyCode switch
        {
            Keys.Up => _selectedIndex - 1,
            Keys.Down => _selectedIndex + 1,
            _ => _selectedIndex,
        };

        if (next != _selectedIndex && next >= 0 && next < _items.Count)
        {
            Select(_items[next].Id);
            e.Handled = true;
        }
    }

    protected override Color BehindColor => Theme.Current.SurfaceAlt;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        // A single hairline on the inner edge, which is what separates the rail from the content
        // area without the weight of a full border.
        using (var pen = new Pen(p.Border, DpF(1f)))
        {
            g.DrawLine(pen, Width - 0.5f, 0, Width - 0.5f, Height);
        }

        DrawBrand(g, p);

        int itemHeight = ItemHeight;
        int y = TopOffset;

        for (int i = 0; i < _items.Count; i++)
        {
            Item item = _items[i];
            bool selected = i == _selectedIndex;
            bool hovered = i == _hoverIndex;

            var row = new RectangleF(Sp(2), y, Width - Sp(2) - Sp(2), itemHeight - Dp(3));
            float radius = Theme.RadiusControl(DeviceDpi);

            if (selected)
            {
                g.FillRounded(row, radius, p.AccentSoft);

                // The selection marker: a short accent bar on the leading edge, rounded, vertically
                // inset so it reads as an indicator rather than as the row's own border.
                var marker = new RectangleF(Sp(2) - DpF(1f), y + (itemHeight * 0.22f), DpF(3f), itemHeight * 0.52f);
                g.FillRounded(marker, DpF(2f), p.Accent);
            }
            else if (hovered)
            {
                g.FillRounded(row, radius, p.SurfaceHover);
            }

            Color fore = selected ? p.Accent : hovered ? p.TextPrimary : p.TextSecondary;

            int icon = Dp(20);
            float iconX = _collapsed ? (Width - icon) / 2f : Sp(2) + Sp(3);
            Icons.Draw(g, item.Glyph, new RectangleF(iconX, y + ((itemHeight - Dp(3) - icon) / 2f), icon, icon), fore, 1.9f);

            if (!_collapsed && item.Label.Length > 0)
            {
                int textX = (int)(iconX + icon + Sp(3));
                g.DrawTextCrisp(
                    item.Label,
                    selected ? Theme.Font(FontRole.BodyStrong) : Theme.Font(FontRole.Body),
                    fore,
                    new Rectangle(textX, y, Math.Max(0, Width - textX - Sp(3)), itemHeight - Dp(3)));
            }

            if (item.Badge > 0)
            {
                DrawBadge(g, p, item.Badge, y, itemHeight, icon, iconX);
            }

            y += itemHeight;
        }
    }

    /// <summary>
    /// The product mark at the top of the rail: the app's own artwork plus its name. The bitmap is
    /// cached per size because this repaints on every hover, and decoding an icon each time would
    /// be a visible cost for something that never changes.
    /// </summary>
    private void DrawBrand(Graphics g, Palette p)
    {
        int mark = Dp(26);
        float markX = _collapsed ? (Width - mark) / 2f : Sp(2) + Sp(3);
        float markY = (BrandHeight - mark) / 2f;

        Bitmap logo = GetLogo(mark);
        g.DrawImage(logo, markX, markY, mark, mark);

        if (_collapsed || BrandText.Length == 0)
        {
            return;
        }

        int textX = (int)(markX + mark + Sp(2));
        g.DrawTextCrisp(
            BrandText,
            Theme.Font(FontRole.BodyStrong),
            p.TextPrimary,
            new Rectangle(textX, 0, Math.Max(0, Width - textX - Sp(2)), BrandHeight));
    }

    private Bitmap GetLogo(int size)
    {
        if (_logo is null || _logo.Width != size)
        {
            _logo?.Dispose();
            _logo = TrayIconFactory.LoadLogoBitmap(size);
        }

        return _logo;
    }

    private void DrawBadge(Graphics g, Palette p, int count, int y, int itemHeight, int icon, float iconX)
    {
        string text = count > 99 ? "99+" : count.ToString();
        Size size = GdiExtensions.MeasureCrisp(text, Theme.Font(FontRole.SmallStrong));
        int height = Dp(16);
        int width = Math.Max(height, size.Width + Sp(2));

        // Collapsed, the badge has nowhere to sit beside the label, so it rides the icon's top
        // right corner the way a notification dot does.
        RectangleF bounds = _collapsed
            ? new RectangleF(iconX + icon - DpF(5f), y + ((itemHeight - Dp(3) - icon) / 2f) - DpF(3f), width, height)
            : new RectangleF(Width - Sp(3) - width, y + ((itemHeight - Dp(3) - height) / 2f), width, height);

        g.FillRounded(bounds, height, p.Error);
        g.DrawTextCrisp(
            text,
            Theme.Font(FontRole.SmallStrong),
            Color.White,
            Rectangle.Round(bounds),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logo?.Dispose();
            _logo = null;
        }

        base.Dispose(disposing);
    }
}
