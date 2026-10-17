namespace InternetMonitor.Ui.Theming;

/// <summary>
/// The help balloons: one <see cref="ToolTip"/> per page, shown after the mouse has rested on a
/// setting or a button for a second, drawn in the app's own theme rather than Windows' yellow box.
/// A control without a text gets no balloon, so help can be added item by item.
///
/// The balloon is placed by this class rather than by Windows: Windows hangs a tooltip off the
/// mouse pointer, and a long help text on a setting near the bottom of the window then runs on
/// past the window's edge, over whatever is behind the app. Here it goes under the control when
/// that fits inside the window and above it otherwise, never past the window's right edge.
/// </summary>
public sealed class HelpTips : IDisposable
{
    public const int DelayMs = 1000;

    /// <summary>Moving from one helped control straight to the next shows the next balloon sooner, as Windows does.</summary>
    private const int ReshowMs = 300;
    private const int MaxWidthDp = 320;
    private const int PaddingDp = 8;
    private const int GapDp = 4;
    private const int ShowForMs = 20_000;

    private readonly ToolTip _tip = new()
    {
        ShowAlways = true,
        OwnerDraw = true,
        UseAnimation = false,
        UseFading = false,
    };

    private readonly Dictionary<Control, string> _texts = [];
    private readonly System.Windows.Forms.Timer _delay = new() { Interval = DelayMs };
    private Control? _hovered;
    private string _showing = string.Empty;
    private long _hiddenAt = long.MinValue;

    public HelpTips()
    {
        _tip.Popup += OnPopup;
        _tip.Draw += OnDraw;
        _delay.Tick += OnDelayElapsed;
    }

    /// <summary>Gives every control the same help text; null or empty removes it.</summary>
    public void Set(string? text, params Control[] controls)
    {
        foreach (Control control in controls)
        {
            if (string.IsNullOrEmpty(text))
            {
                if (_texts.Remove(control))
                {
                    Detach(control);
                }

                continue;
            }

            if (_texts.TryAdd(control, text))
            {
                Attach(control);
            }
            else
            {
                _texts[control] = text;
            }
        }
    }

    /// <summary>Shows a balloon at a point inside <paramref name="control"/> - for painted items such as a chip, which are not controls of their own.</summary>
    public void Show(string text, Control control, Point at)
    {
        _showing = text;
        _tip.Show(text, control, at, ShowForMs);
    }

    public void Hide(Control control)
    {
        _tip.Hide(control);
        _hiddenAt = Environment.TickCount64;
    }

    /// <summary>
    /// Where the balloon's top-left goes, relative to the control's top-left: under the control
    /// when that stays inside the window, above it when only that does, and pulled left when it
    /// would run past the window's right edge. All rectangles in one coordinate space.
    /// </summary>
    internal static Point Place(Rectangle control, Rectangle window, Size balloon, int gap)
    {
        int x = control.Left;
        if (x + balloon.Width > window.Right)
        {
            x = Math.Max(window.Left, window.Right - balloon.Width);
        }

        int y = control.Bottom + gap;
        bool fitsBelow = y + balloon.Height <= window.Bottom;
        bool fitsAbove = control.Top - gap - balloon.Height >= window.Top;
        if (!fitsBelow && fitsAbove)
        {
            y = control.Top - gap - balloon.Height;
        }

        return new Point(x - control.Left, y - control.Top);
    }

    private void Attach(Control control)
    {
        control.MouseEnter += OnMouseEnter;
        control.MouseLeave += OnMouseLeave;
        control.MouseDown += OnMouseLeave;
        control.Disposed += OnControlDisposed;
    }

    private void Detach(Control control)
    {
        control.MouseEnter -= OnMouseEnter;
        control.MouseLeave -= OnMouseLeave;
        control.MouseDown -= OnMouseLeave;
        control.Disposed -= OnControlDisposed;
        if (_hovered == control)
        {
            _delay.Stop();
            _hovered = null;
        }
    }

    private void OnControlDisposed(object? sender, EventArgs e)
    {
        if (sender is Control control && _texts.Remove(control))
        {
            Detach(control);
        }
    }

    private void OnMouseEnter(object? sender, EventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        _hovered = control;
        _delay.Stop();
        _delay.Interval = Environment.TickCount64 - _hiddenAt < ReshowMs ? ReshowMs : DelayMs;
        _delay.Start();
    }

    private void OnMouseLeave(object? sender, EventArgs e)
    {
        if (sender is not Control control)
        {
            return;
        }

        _delay.Stop();
        if (_hovered == control)
        {
            _hovered = null;
        }

        Hide(control);
    }

    private void OnDelayElapsed(object? sender, EventArgs e)
    {
        _delay.Stop();
        if (_hovered is not { IsDisposed: false, IsHandleCreated: true } control || !_texts.TryGetValue(control, out string? text))
        {
            return;
        }

        int dpi = control.DeviceDpi;
        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
        Rectangle window = control.FindForm() is { IsHandleCreated: true } form
            ? form.RectangleToScreen(form.ClientRectangle)
            : Screen.FromControl(control).WorkingArea;
        Show(text, control, Place(bounds, window, BalloonSize(text, dpi), Theme.Dp(GapDp, dpi)));
    }

    private static Size BalloonSize(string text, int dpi)
    {
        int padding = Theme.Dp(PaddingDp, dpi);
        Size size = GdiExtensions.MeasureCrisp(text, Theme.Font(FontRole.Small), Theme.Dp(MaxWidthDp, dpi));
        return new Size(size.Width + (padding * 2), size.Height + (padding * 2));
    }

    private void OnPopup(object? sender, PopupEventArgs e)
    {
        e.ToolTipSize = BalloonSize(_showing, e.AssociatedControl?.DeviceDpi ?? 96);
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
        _delay.Stop();
        _delay.Tick -= OnDelayElapsed;
        _delay.Dispose();
        foreach (Control control in _texts.Keys.ToList())
        {
            Detach(control);
        }

        _texts.Clear();
        _tip.Popup -= OnPopup;
        _tip.Draw -= OnDraw;
        _tip.Dispose();
    }
}
