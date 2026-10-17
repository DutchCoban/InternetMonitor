namespace InternetMonitor.Ui.Theming;

/// <summary>
/// The help balloons: one <see cref="ToolTip"/> per page, shown after the mouse has rested on a
/// setting or a button for a second, drawn in the app's own theme rather than Windows' yellow box.
/// A control without a text gets no balloon, so help can be added item by item.
/// </summary>
public sealed class HelpTips : IDisposable
{
    public const int DelayMs = 1000;
    private const int MaxWidthDp = 320;
    private const int PaddingDp = 8;

    private readonly ToolTip _tip = new()
    {
        InitialDelay = DelayMs,
        AutoPopDelay = 20_000,
        ReshowDelay = 300,
        ShowAlways = true,
        OwnerDraw = true,
        UseAnimation = false,
        UseFading = false,
    };

    public HelpTips()
    {
        _tip.Popup += OnPopup;
        _tip.Draw += OnDraw;
    }

    /// <summary>Gives every control the same help text; null or empty removes it.</summary>
    public void Set(string? text, params Control[] controls)
    {
        foreach (Control control in controls)
        {
            _tip.SetToolTip(control, text ?? string.Empty);
        }
    }

    /// <summary>Shows a balloon at a point inside <paramref name="control"/> - for painted items such as a chip, which are not controls of their own.</summary>
    public void Show(string text, Control control, Point at) => _tip.Show(text, control, at, 20_000);

    public void Hide(Control control) => _tip.Hide(control);

    private void OnPopup(object? sender, PopupEventArgs e)
    {
        string text = e.AssociatedControl is { } control ? _tip.GetToolTip(control) ?? string.Empty : string.Empty;
        int dpi = e.AssociatedControl?.DeviceDpi ?? 96;
        int padding = Theme.Dp(PaddingDp, dpi);
        Size size = GdiExtensions.MeasureCrisp(text, Theme.Font(FontRole.Small), Theme.Dp(MaxWidthDp, dpi));
        e.ToolTipSize = new Size(size.Width + (padding * 2), size.Height + (padding * 2));
    }

    private void OnDraw(object? sender, DrawToolTipEventArgs e)
    {
        Palette p = Theme.Current;
        int dpi = e.AssociatedControl?.DeviceDpi ?? 96;
        int padding = Theme.Dp(PaddingDp, dpi);

        e.Graphics.FillRectangle(GdiExtensions.BrushFor(p.Surface), e.Bounds);
        e.Graphics.DrawRectangle(GdiExtensions.PenFor(p.BorderStrong), e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);

        var inner = new Rectangle(e.Bounds.X + padding, e.Bounds.Y + padding, Math.Max(0, e.Bounds.Width - (padding * 2)), Math.Max(0, e.Bounds.Height - (padding * 2)));
        e.Graphics.DrawTextCrisp(e.ToolTipText ?? string.Empty, Theme.Font(FontRole.Small), p.TextPrimary, inner, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak);
    }

    public void Dispose()
    {
        _tip.Popup -= OnPopup;
        _tip.Draw -= OnDraw;
        _tip.Dispose();
    }
}
