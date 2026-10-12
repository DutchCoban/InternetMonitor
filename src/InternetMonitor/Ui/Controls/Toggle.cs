using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// An on/off switch. Used instead of a stock <see cref="CheckBox"/> for every boolean setting:
/// a checkbox's tri-state glyph reads as "pick an option", while a switch reads as "currently on,
/// click to turn off" - the correct framing for settings like auto-start or the traceroute lookup,
/// which describe a live, already-in-effect state rather than a choice being made.
/// </summary>
public sealed class Toggle : ThemedControl
{
    private const int AnimationIntervalMs = 16;
    private const float AnimationStep = 0.25f;

    private readonly System.Windows.Forms.Timer _animation;
    private float _position;
    private bool _checked;
    private bool _hovered;

    public Toggle()
    {
        SetStyle(ControlStyles.Selectable | ControlStyles.StandardClick, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(Dp(40), Dp(22));

        _animation = new System.Windows.Forms.Timer { Interval = AnimationIntervalMs };
        _animation.Tick += OnAnimationTick;
    }

    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            _position = value ? 1f : 0f;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sets the value without raising <see cref="CheckedChanged"/> - for initialising the control from a loaded setting.</summary>
    public void SetWithoutNotify(bool value)
    {
        _checked = value;
        _position = value ? 1f : 0f;
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(Dp(40), Dp(22));

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        float target = _checked ? 1f : 0f;
        float delta = target - _position;

        if (Math.Abs(delta) <= AnimationStep)
        {
            _position = target;
            _animation.Stop();
        }
        else
        {
            _position += Math.Sign(delta) * AnimationStep;
        }

        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        if (_animation.Enabled == false && Math.Abs(_position - (_checked ? 1f : 0f)) > 0.001f)
        {
            _animation.Start();
        }

        var track = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        Color trackColor = GdiExtensions.Mix(p.SurfaceHover, p.Accent, _position);

        g.FillRounded(track, Height, trackColor);
        g.DrawRounded(track, Height, Enabled ? p.BorderStrong.WithAlpha(60) : p.Border);

        float knobSize = Height - Dp(6);
        float travel = Width - knobSize - Dp(6);
        float knobX = Dp(3) + (travel * _position);
        var knob = new RectangleF(knobX, (Height - knobSize) / 2f, knobSize, knobSize);

        if (_hovered && Enabled)
        {
            g.FillRounded(RectangleF.Inflate(knob, Dp(3), Dp(3)), knobSize, Color.FromArgb(28, Color.Black));
        }

        g.DrawShadow(knob, knobSize / 2f, p.Shadow, Dp(2));
        g.FillRounded(knob, knobSize, Enabled ? Color.White : p.SurfaceAlt);

        if (Focused)
        {
            g.DrawRounded(RectangleF.Inflate(track, DpF(1.5f), DpF(1.5f)), Height, p.Accent.WithAlpha(150), DpF(1.2f));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Tick -= OnAnimationTick;
            _animation.Dispose();
        }

        base.Dispose(disposing);
    }
}
