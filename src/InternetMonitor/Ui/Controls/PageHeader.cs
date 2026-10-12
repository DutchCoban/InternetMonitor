using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The title block at the top of every page: one large heading and a muted line of context, with
/// page-level actions right-aligned beside them.
///
/// It measures its own height from the text it holds, so a page never has to know how tall its
/// header is - and a longer translation makes the header taller instead of clipping.
///
/// Actions sit in a right-docked flow panel rather than being positioned by hand. Hand-positioning
/// them meant guessing when the header first has a meaningful width: a page is built while still
/// invisible and before the window has been laid out, and actions placed at that moment stayed at
/// the left edge on top of the title. Docking hands that problem to the layout engine, which
/// already knows the answer.
///
/// WrapContents is off deliberately. With it on - the default - a second action wraps onto a
/// hidden second row inside an AutoSize panel instead of the panel growing wider, so the button
/// silently disappears rather than rendering. v1 hit exactly this on its diagnostics screen.
/// </summary>
public sealed class PageHeader : ThemedControl
{
    private readonly FlowLayoutPanel _actionBar;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;

    public PageHeader()
    {
        Dock = DockStyle.Top;
        Height = Dp(64);

        _actionBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        Controls.Add(_actionBar);
        ApplyActionBarColor();
    }

    /// <summary>
    /// Keeps the action bar's BackColor equal to the page background.
    ///
    /// It must not be Color.Transparent: owner-drawn children ask their parent what is behind them
    /// and fill with it, so a transparent parent means filling with alpha zero - which leaves
    /// whatever was in the buffer and renders as black. A themed button on the header looked like
    /// a solid black slab until this was set to a real colour.
    /// </summary>
    private void ApplyActionBarColor() => _actionBar.BackColor = Theme.Current.Bg;

    public string Title
    {
        get => _title;
        set
        {
            _title = value ?? string.Empty;
            UpdateHeight();
            Invalidate();
        }
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            _subtitle = value ?? string.Empty;
            UpdateHeight();
            Invalidate();
        }
    }

    /// <summary>
    /// Adds a page-level action. The first one added sits furthest right, matching the order
    /// buttons are usually read in.
    /// </summary>
    public void AddAction(Control action)
    {
        action.Margin = new Padding(Sp(2), 0, 0, 0);
        _actionBar.Controls.Add(action);
        Invalidate();
    }

    /// <summary>Re-measures after an action changed size - following a language change, typically.</summary>
    public void RefreshActions()
    {
        _actionBar.PerformLayout();
        Invalidate();
    }

    /// <summary>
    /// The right-hand edge the title may use. Taken from the action bar's actual width, so a long
    /// title stops before the buttons instead of running underneath them.
    /// </summary>
    private int TextRight => Math.Max(Dp(80), Width - _actionBar.Width - Sp(2));

    private void UpdateHeight()
    {
        int height = Sp(2);
        height += GdiExtensions.MeasureCrisp(_title.Length > 0 ? _title : "X", Theme.Font(FontRole.Display)).Height;
        if (_subtitle.Length > 0)
        {
            height += Dp(2) + GdiExtensions.MeasureCrisp(_subtitle, Theme.Font(FontRole.Body)).Height;
        }

        height += Sp(3);
        if (Height != height)
        {
            Height = height;
        }
    }

    protected override void OnThemeChanged()
    {
        UpdateHeight();
        ApplyActionBarColor();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        int y = Sp(2);
        int textWidth = TextRight;

        if (_title.Length > 0)
        {
            Size size = GdiExtensions.MeasureCrisp(_title, Theme.Font(FontRole.Display));
            g.DrawTextCrisp(_title, Theme.Font(FontRole.Display), p.TextPrimary, new Rectangle(0, y, textWidth, size.Height));
            y += size.Height + Dp(2);
        }

        if (_subtitle.Length > 0)
        {
            Size size = GdiExtensions.MeasureCrisp(_subtitle, Theme.Font(FontRole.Body));
            g.DrawTextCrisp(_subtitle, Theme.Font(FontRole.Body), p.TextMuted, new Rectangle(0, y, textWidth, size.Height));
        }
    }
}
