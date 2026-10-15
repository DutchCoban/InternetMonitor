using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// Everything one step's latest result carries, laid out to be read: headline, detail sentence,
/// then every raw measurement as a label/value row. This is the "why" behind
/// <see cref="StepTable"/>'s one-line summary - where a support conversation actually ends up,
/// once "DNS failed" needs to become "which hostname, which server, how long did it wait".
/// </summary>
public sealed class StepDetailPanel : ThemedControl
{
    private StepId _step = StepId.Adapter;
    private StepResult? _result;
    private int _scrollOffset;
    private int _contentHeight;
    private Rectangle _actionBounds = Rectangle.Empty;

    protected override Color BehindColor => Theme.Current.Surface;

    public void Show(StepId step, StepResult result)
    {
        _step = step;
        _result = result;
        _scrollOffset = 0;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _actionBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && _actionBounds.Contains(e.Location) && _result?.Action is { } action)
        {
            ShellOpen.Open(action.Target);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollOffset = Math.Clamp(_scrollOffset - e.Delta, 0, Math.Max(0, _contentHeight - Height));
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _contentHeight - Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        if (_result is not { } result)
        {
            return;
        }

        int width = Math.Max(Dp(120), Width - Sp(4));
        int y = Sp(1) - _scrollOffset;

        g.DrawTextCrisp(
            loc.Get($"step.{_step.Key()}.name"),
            Theme.Font(FontRole.Title),
            p.TextPrimary,
            new Rectangle(Sp(2), y, width, Dp(24)));
        y += Dp(26);

        Severity severity = StepList.SeverityOf(result.State);
        Color headlineColor = result.State is StepState.Pending or StepState.Running or StepState.Skipped
            ? p.TextSecondary
            : SeverityColors.Strong(severity);

        string headline = result.State switch
        {
            StepState.Pending => loc.Get("stepState.pending"),
            StepState.Running => loc.Get("stepState.running"),
            _ => result.Headline,
        };

        Size headlineSize = GdiExtensions.MeasureCrisp(headline, Theme.Font(FontRole.BodyStrong), width);
        g.DrawTextCrisp(
            headline,
            Theme.Font(FontRole.BodyStrong),
            headlineColor,
            new Rectangle(Sp(2), y, width, headlineSize.Height),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        y += headlineSize.Height + Dp(4);

        if (!string.IsNullOrEmpty(result.Detail))
        {
            Size detailSize = GdiExtensions.MeasureCrisp(result.Detail, Theme.Font(FontRole.Body), width);
            g.DrawTextCrisp(
                result.Detail,
                Theme.Font(FontRole.Body),
                p.TextSecondary,
                new Rectangle(Sp(2), y, width, detailSize.Height),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y += detailSize.Height + Sp(2);
        }

        // The action link's rectangle is kept for OnMouseMove/OnMouseClick, since it is painted
        // rather than a child control; it moves with the scroll offset like everything else here.
        _actionBounds = Rectangle.Empty;
        if (result.Action is { } action)
        {
            Font linkFont = new(Theme.Font(FontRole.Body), FontStyle.Underline);
            using (linkFont)
            {
                Size linkSize = GdiExtensions.MeasureCrisp(loc.Get(action.TextKey), linkFont);
                _actionBounds = new Rectangle(Sp(2), y, linkSize.Width, linkSize.Height);
                g.DrawTextCrisp(loc.Get(action.TextKey), linkFont, p.Accent, _actionBounds);
            }

            y += _actionBounds.Height + Sp(2);
        }

        if (result.Duration > TimeSpan.Zero && result.State.IsResolved())
        {
            string duration = loc.Format("diagnostics.duration", $"{result.Duration.TotalMilliseconds:F0}");
            g.DrawTextCrisp(duration, Theme.Font(FontRole.Small), p.TextMuted, new Rectangle(Sp(2), y, width, Dp(16)));
            y += Dp(20);
        }

        if (result.Measurements.Count > 0)
        {
            y += Sp(1);
            g.DrawTextCrisp(
                loc.Get("diagnostics.measurements").ToUpperInvariant(),
                Theme.Font(FontRole.SmallStrong),
                p.TextMuted,
                new Rectangle(Sp(2), y, width, Dp(14)));
            y += Dp(19);

            int labelWidth = Dp(140);
            foreach (Measurement measurement in result.Measurements)
            {
                Size valueSize = GdiExtensions.MeasureCrisp(
                    measurement.Value, Theme.Font(FontRole.Mono), Math.Max(Dp(80), width - labelWidth));
                int rowHeight = Math.Max(Dp(18), valueSize.Height);

                g.DrawTextCrisp(
                    measurement.Label,
                    Theme.Font(FontRole.Small),
                    p.TextMuted,
                    new Rectangle(Sp(2), y, labelWidth - Sp(1), rowHeight));

                g.DrawTextCrisp(
                    measurement.Value,
                    Theme.Font(FontRole.Mono),
                    p.TextSecondary,
                    new Rectangle(Sp(2) + labelWidth, y, Math.Max(0, width - labelWidth), rowHeight),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

                y += rowHeight + Dp(4);
            }
        }
        else if (result.State == StepState.Skipped)
        {
            g.DrawTextCrisp(
                loc.Get("diagnostics.skippedExplanation"),
                Theme.Font(FontRole.Small),
                p.TextMuted,
                new Rectangle(Sp(2), y, width, Dp(32)),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y += Dp(36);
        }

        _contentHeight = y + _scrollOffset + Sp(3);
    }
}
