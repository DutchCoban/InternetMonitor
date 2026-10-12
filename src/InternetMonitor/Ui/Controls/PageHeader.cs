using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The title block at the top of every page: one large heading and a muted line of context, with
/// room on the right for page-level actions.
///
/// It measures its own height from the text it holds, so a page never has to know how tall its
/// header is - and a longer translation makes the header taller instead of clipping.
/// </summary>
public sealed class PageHeader : ThemedControl
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;

    public PageHeader()
    {
        Dock = DockStyle.Top;
        Height = Dp(64);
    }

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
    /// Lays out a row of action controls right-aligned on the title line. Called by the page after
    /// it adds them, and again on resize, so the buttons stay pinned to the right edge.
    /// </summary>
    public void LayoutActions(params Control[] actions)
    {
        int right = Width - Sp(1);
        foreach (Control action in actions.Reverse())
        {
            if (!action.Visible)
            {
                continue;
            }

            action.Left = right - action.Width;
            action.Top = (Height - action.Height) / 2;
            right = action.Left - Sp(2);
        }
    }

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

    protected override void OnThemeChanged() => UpdateHeight();

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        int y = Sp(2);
        if (_title.Length > 0)
        {
            Size size = GdiExtensions.MeasureCrisp(_title, Theme.Font(FontRole.Display));
            g.DrawTextCrisp(_title, Theme.Font(FontRole.Display), p.TextPrimary, new Rectangle(0, y, Width, size.Height));
            y += size.Height + Dp(2);
        }

        if (_subtitle.Length > 0)
        {
            Size size = GdiExtensions.MeasureCrisp(_subtitle, Theme.Font(FontRole.Body));
            g.DrawTextCrisp(_subtitle, Theme.Font(FontRole.Body), p.TextMuted, new Rectangle(0, y, Width, size.Height));
        }
    }
}
