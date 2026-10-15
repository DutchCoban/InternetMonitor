using System.Drawing.Drawing2D;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The seven steps as a live vertical timeline: what each one concluded, which ones are running
/// right now, and - the thing this control exists for - an arrow on the step currently being
/// tested.
///
/// Drawn as one control rather than seven child controls. The focus arrow, the connector line and
/// the hover highlight all need to be positioned relative to each other, and one animation timer
/// serving every row beats seven controls each owning one.
///
/// The arrow sits on the *focus step*: the lowest-numbered step that is currently running. Steps
/// 4 to 7 genuinely run at the same time, so any other rule would be a lie - which is why the
/// other in-flight steps get their own spinner rather than sharing the arrow. The user sees one
/// clear "here is where we are" marker and an honest indication that work is happening in
/// parallel behind it.
///
/// The connector line running through the icons is coloured up to the first failure and muted
/// after it, so the shape of the problem is readable at a glance without reading a word: green
/// chain, then a break, then grey.
/// </summary>
public sealed class StepList : ThemedControl
{
    private static readonly StepId[] Order =
        [StepId.Adapter, StepId.Link, StepId.IpAddress, StepId.Gateway, StepId.PublicIp, StepId.Dns, StepId.Target];

    /// <summary>
    /// Internal rather than private so DiagnosticsPage's StepTable can use the same icon per step
    /// instead of keeping a second copy of this mapping in sync with it.
    /// </summary>
    internal static readonly Dictionary<StepId, Glyph> Glyphs = new()
    {
        [StepId.Adapter] = Glyph.Adapter,
        [StepId.Link] = Glyph.Cable,
        [StepId.IpAddress] = Glyph.Ip,
        [StepId.Gateway] = Glyph.Gateway,
        [StepId.PublicIp] = Glyph.Globe,
        [StepId.Dns] = Glyph.Dns,
        [StepId.Target] = Glyph.Target,
    };

    private const int AnimationIntervalMs = 33;

    // Two dictionaries, deliberately not one. _results is what gets drawn as text - the last
    // genuinely RESOLVED result for each step - and it is only ever replaced by another resolved
    // result. _liveState is the actual current state including the transient Pending/Running the
    // pipeline republishes on every cycle, used purely to decide where the arrow and spinners go.
    //
    // Without this split, a step that has been healthy for an hour would still flash its text to
    // "waiting"/"testing..." every time the background cycle re-ran it - which on a 3s foreground
    // interval is several times a minute, forever, even though nothing had actually changed. The
    // arrow and spinner already say "this is being re-checked right now"; the row's own text has
    // no business also announcing that by briefly going blank.
    private readonly Dictionary<StepId, StepResult> _results = [];
    private readonly Dictionary<StepId, StepState> _liveState = [];
    private readonly HashSet<StepId> _expanded = [];
    private readonly System.Windows.Forms.Timer _animation;
    private double _phase;
    private int _hoverIndex = -1;

    public StepList()
    {
        _animation = new System.Windows.Forms.Timer { Interval = AnimationIntervalMs };
        _animation.Tick += OnAnimationTick;

        foreach (StepId step in Order)
        {
            _results[step] = StepResult.Pending(step);
            _liveState[step] = StepState.Pending;
        }
    }

    /// <summary>Raised when a row is expanded or collapsed, so the page can re-measure the card.</summary>
    public event Action? HeightChanged;

    protected override Color BehindColor => Theme.Current.Surface;

    private int RowHeight => Dp(46);

    private int NameColumnWidth => Dp(150);

    /// <summary>
    /// Height needed for all seven rows plus whatever is currently expanded. The page asks for
    /// this rather than assuming, so a longer translation or an expanded row grows the card
    /// instead of being clipped.
    /// </summary>
    public int PreferredHeight
    {
        get
        {
            int height = Sp(1);
            foreach (StepId step in Order)
            {
                height += RowHeight + ExpandedHeight(step);
            }

            return height + Sp(1);
        }
    }

    /// <summary>
    /// Applies one step's new state. Called for every transition, including Running - but only a
    /// genuinely resolved result (Ok/Warning/Failed/Skipped) ever changes what the row displays.
    /// A Pending or Running update still moves the arrow/spinner; it just does not touch the text
    /// underneath it.
    /// </summary>
    public void Apply(StepResult result)
    {
        _liveState[result.Step] = result.State;

        if (result.State.IsResolved() || !_results.TryGetValue(result.Step, out StepResult? shown) || !shown.State.IsResolved())
        {
            _results[result.Step] = result;
        }

        UpdateAnimationState();
        Invalidate();
    }

    public void Reset()
    {
        foreach (StepId step in Order)
        {
            _results[step] = StepResult.Pending(step);
            _liveState[step] = StepState.Pending;
        }

        UpdateAnimationState();
        Invalidate();
    }

    /// <summary>
    /// The step the arrow is on: the lowest-numbered one still running. Null when nothing is in
    /// flight. Reads live state, not the displayed result - the displayed result for a step
    /// currently running is still last cycle's, by design.
    /// </summary>
    private StepId? FocusStep => Order
        .Where(s => _liveState.GetValueOrDefault(s, StepState.Pending) == StepState.Running)
        .Cast<StepId?>()
        .FirstOrDefault();

    private void UpdateAnimationState()
    {
        bool anyRunning = Order.Any(s => _liveState.GetValueOrDefault(s, StepState.Pending) == StepState.Running);
        if (anyRunning && !_animation.Enabled)
        {
            _animation.Start();
        }
        else if (!anyRunning && _animation.Enabled)
        {
            // Nothing is moving, so nothing needs repainting. An idle window must cost no CPU -
            // this app runs for days.
            _animation.Stop();
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        _phase = (_phase + 0.055) % 1.0;
        Invalidate();
    }

    private int ExpandedHeight(StepId step)
    {
        if (!_expanded.Contains(step))
        {
            return 0;
        }

        StepResult result = _results[step];
        int lines = result.Measurements.Count + (string.IsNullOrEmpty(result.Detail) ? 0 : 1);
        return lines == 0 ? 0 : Sp(2) + (lines * Dp(18)) + Sp(2);
    }

    private int IndexAt(int y)
    {
        int top = Sp(1);
        for (int i = 0; i < Order.Length; i++)
        {
            int height = RowHeight + ExpandedHeight(Order[i]);
            if (y >= top && y < top + height)
            {
                return i;
            }

            top += height;
        }

        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Y);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
            Cursor = index >= 0 && HasExpandableContent(Order[index]) ? Cursors.Hand : Cursors.Default;
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

        int index = IndexAt(e.Y);
        if (index < 0)
        {
            return;
        }

        StepId step = Order[index];
        if (!HasExpandableContent(step))
        {
            return;
        }

        if (!_expanded.Remove(step))
        {
            _expanded.Add(step);
        }

        HeightChanged?.Invoke();
        Invalidate();
    }

    private bool HasExpandableContent(StepId step)
    {
        StepResult result = _results[step];
        return result.Measurements.Count > 0 || !string.IsNullOrEmpty(result.Detail);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        StepId? focus = FocusStep;
        StepId? firstFailure = Order
            .Where(s => _results[s].State == StepState.Failed)
            .Cast<StepId?>()
            .FirstOrDefault();

        int arrowColumn = Sp(2);
        int iconColumn = arrowColumn + Dp(16) + Sp(2);
        int iconSize = Dp(20);
        int nameColumn = iconColumn + iconSize + Sp(3);

        int y = Sp(1);
        for (int i = 0; i < Order.Length; i++)
        {
            StepId step = Order[i];
            StepResult result = _results[step];
            int expanded = ExpandedHeight(step);
            var row = new Rectangle(0, y, Width, RowHeight);

            if (i == _hoverIndex && HasExpandableContent(step))
            {
                g.FillRounded(
                    new RectangleF(Sp(1), y + 1, Width - Sp(2), RowHeight + expanded - 2),
                    Theme.RadiusControl(DeviceDpi),
                    p.SurfaceHover);
            }

            DrawConnector(g, p, i, step, firstFailure, iconColumn + (iconSize / 2f), y, expanded);

            Severity severity = SeverityOf(result.State);
            Color iconColor = result.State switch
            {
                StepState.Pending => p.Idle,
                StepState.Running => p.Accent,
                StepState.Skipped => p.Idle,
                _ => SeverityColors.Strong(severity),
            };

            // The icon disc: a filled soft circle behind the step's own glyph, so the chain of
            // icons reads as a sequence of stations rather than as loose symbols.
            var disc = new RectangleF(iconColumn, y + ((RowHeight - iconSize) / 2f) - Dp(2), iconSize + Dp(8), iconSize + Dp(8));
            disc.Offset(-Dp(4), 0);
            g.FillRounded(disc, disc.Width, result.State switch
            {
                StepState.Pending or StepState.Skipped => p.IdleSoft,
                StepState.Running => p.AccentSoft,
                _ => SeverityColors.Soft(severity),
            });

            Icons.Draw(
                g,
                Glyphs[step],
                new RectangleF(iconColumn, y + ((RowHeight - iconSize) / 2f), iconSize, iconSize),
                iconColor,
                1.8f);

            if (focus == step)
            {
                DrawFocusArrow(g, p, arrowColumn, y);
            }
            else if (_liveState.GetValueOrDefault(step, StepState.Pending) == StepState.Running)
            {
                // A second in-flight step: a spinner, not a second arrow. Both are true at once
                // and the display should say so. Live state, not the displayed result - the
                // result is still showing last cycle's value while this one is re-checked.
                DrawSpinner(g, p, arrowColumn, y);
            }

            bool muted = result.State is StepState.Pending or StepState.Skipped;
            g.DrawTextCrisp(
                loc.Get($"step.{step.Key()}.name"),
                Theme.Font(FontRole.BodyStrong),
                muted ? p.TextMuted : p.TextPrimary,
                new Rectangle(nameColumn, y, NameColumnWidth, RowHeight));

            DrawStatusText(g, p, loc, result, nameColumn + NameColumnWidth + Sp(2), y);

            y += RowHeight;

            if (expanded > 0)
            {
                DrawExpanded(g, p, result, nameColumn, y, expanded);
                y += expanded;
            }
        }
    }

    /// <summary>
    /// The vertical line joining one icon to the next. Coloured while the chain is intact and
    /// muted from the first failure onward.
    /// </summary>
    private void DrawConnector(Graphics g, Palette p, int index, StepId step, StepId? firstFailure, float centreX, int y, int expandedHeight)
    {
        if (index == Order.Length - 1)
        {
            return;
        }

        bool beforeBreak = firstFailure is null || (int)step < (int)firstFailure;
        Color color = beforeBreak ? p.Ok.WithAlpha(90) : p.Border;

        using var pen = new Pen(color, DpF(2f));
        float top = y + ((RowHeight + Dp(20)) / 2f);
        float bottom = y + RowHeight + expandedHeight + ((RowHeight - Dp(20)) / 2f);
        g.DrawLine(pen, centreX, top, centreX, bottom);
    }

    private void DrawFocusArrow(Graphics g, Palette p, int x, int y)
    {
        // Pulses between a half and a full step of travel, so the eye is drawn to it without the
        // row appearing to jitter.
        float travel = (float)(Math.Sin(_phase * Math.PI * 2) * DpF(1.5f));
        int size = Dp(14);
        Icons.Draw(
            g,
            Glyph.Arrow,
            new RectangleF(x + travel, y + ((RowHeight - size) / 2f), size, size),
            p.Accent,
            2f);
    }

    private void DrawSpinner(Graphics g, Palette p, int x, int y)
    {
        int size = Dp(13);
        var bounds = new RectangleF(x + Dp(1), y + ((RowHeight - size) / 2f), size, size);

        using var track = new Pen(p.Accent.WithAlpha(45), DpF(2f));
        g.DrawEllipse(track, bounds);

        using var arc = new Pen(p.Accent.WithAlpha(190), DpF(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, bounds, (float)(_phase * 360), 90f);
    }

    /// <summary>
    /// A row's right-hand side: the headline (or pending/running) in the state's colour, with the
    /// step's duration right-aligned once it has finished.
    /// </summary>
    private void DrawStatusText(Graphics g, Palette p, LocalizationManager loc, StepResult result, int x, int y)
    {
        int right = Width - Sp(3);
        string duration = result.State.IsResolved() && result.Duration > TimeSpan.Zero
            ? $"{result.Duration.TotalMilliseconds:F0} ms"
            : string.Empty;

        int durationWidth = 0;
        if (duration.Length > 0)
        {
            Size size = GdiExtensions.MeasureCrisp(duration, Theme.Font(FontRole.Small));
            durationWidth = size.Width + Sp(2);
            g.DrawTextCrisp(
                duration,
                Theme.Font(FontRole.Small),
                p.TextMuted,
                new Rectangle(right - size.Width, y, size.Width, RowHeight));
        }

        string text = result.State switch
        {
            StepState.Pending => loc.Get("stepState.pending"),
            StepState.Running => loc.Get("stepState.running"),
            _ => result.Headline,
        };

        Color color = result.State switch
        {
            StepState.Failed => p.Error,
            StepState.Warning => p.Warn,
            StepState.Pending or StepState.Running or StepState.Skipped => p.TextMuted,
            _ => p.TextSecondary,
        };

        int available = Math.Max(0, right - durationWidth - x);
        g.DrawTextCrisp(text, Theme.Font(FontRole.Body), color, new Rectangle(x, y, available, RowHeight));
    }

    /// <summary>An expanded row: the detail sentence, then every measurement as a label/value line.</summary>
    private void DrawExpanded(Graphics g, Palette p, StepResult result, int x, int y, int height)
    {
        int lineHeight = Dp(18);
        int cursor = y + Sp(1);

        if (!string.IsNullOrEmpty(result.Detail))
        {
            g.DrawTextCrisp(
                result.Detail,
                Theme.Font(FontRole.Small),
                p.TextSecondary,
                new Rectangle(x, cursor, Math.Max(0, Width - x - Sp(3)), lineHeight));
            cursor += lineHeight;
        }

        foreach (Measurement measurement in result.Measurements)
        {
            g.DrawTextCrisp(
                measurement.Label,
                Theme.Font(FontRole.Small),
                p.TextMuted,
                new Rectangle(x, cursor, Dp(130), lineHeight));

            g.DrawTextCrisp(
                measurement.Value,
                Theme.Font(FontRole.Mono),
                p.TextSecondary,
                new Rectangle(x + Dp(134), cursor, Math.Max(0, Width - x - Dp(134) - Sp(3)), lineHeight));

            cursor += lineHeight;
        }
    }

    public static Severity SeverityOf(StepState state) => state switch
    {
        StepState.Ok => Severity.Ok,
        StepState.Warning => Severity.Warning,
        StepState.Failed => Severity.Error,
        _ => Severity.Idle,
    };

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
