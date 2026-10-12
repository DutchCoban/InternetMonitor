using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// Base class for every custom-drawn v2 control. It exists to stop three things from being
/// repeated (and eventually forgotten) in a dozen places:
///
/// 1. The double-buffering style flags. Without OptimizedDoubleBuffer plus AllPaintingInWmPaint,
///    a control that paints its own background flickers visibly on every resize and repaint.
/// 2. The <see cref="Theme.Changed"/> subscribe/unsubscribe pair. A control that subscribes but
///    never detaches keeps itself alive through the static event for the life of the process -
///    a real leak, since pages and rows are created and destroyed as the user navigates.
/// 3. Background fill. WinForms has no usable transparency for owner-drawn controls, so instead
///    of faking it we paint the colour of whatever is behind us and then draw on top. Nesting a
///    card inside a page inside the shell then composites correctly with no transparency at all.
/// </summary>
public abstract class ThemedControl : Control
{
    private bool _subscribed;

    protected ThemedControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        // The control tree is themed as a whole, so a control never needs the designer's own
        // BackColor inheritance; BehindColor is the single answer to "what is under me".
        DoubleBuffered = true;
    }

    /// <summary>
    /// The colour painted before <see cref="OnPaint"/> runs: whatever surface this control sits
    /// on. Overridden by controls that sit on a card rather than directly on a page.
    /// </summary>
    protected virtual Color BehindColor => Parent?.BackColor ?? Theme.Current.Bg;

    /// <summary>Design-pixel to device-pixel helper, bound to this control's own monitor DPI.</summary>
    protected int Dp(int px) => Theme.Dp(px, DeviceDpi);

    protected float DpF(float px) => Theme.DpF(px, DeviceDpi);

    protected int Sp(int step) => Theme.Space(step, DeviceDpi);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_subscribed)
        {
            Theme.Changed += OnThemeChangedInternal;
            _subscribed = true;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(BehindColor);
        e.Graphics.FillRectangle(brush, e.ClipRectangle);
    }

    private void OnThemeChangedInternal()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        OnThemeChanged();
        Invalidate();
    }

    /// <summary>
    /// Hook for controls that cache theme-derived state (child control colours, a prepared
    /// gradient). The invalidate already happens for you; override only to update such state.
    /// </summary>
    protected virtual void OnThemeChanged()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _subscribed)
        {
            Theme.Changed -= OnThemeChangedInternal;
            _subscribed = false;
        }

        base.Dispose(disposing);
    }
}
