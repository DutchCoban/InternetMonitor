using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The raw list behind the Diagnostics page: all seven steps, one row each, in a fixed order
/// rather than reshuffled by status - support staff reading this want "what did step 4 measure",
/// not a list sorted by severity. Selecting a row is how the detail panel beside it knows what to
/// show; unlike <see cref="StepList"/> this never collapses or animates, because its whole purpose
/// is to sit still while someone reads it.
/// </summary>
public sealed class StepTable : ThemedControl
{
    private static readonly StepId[] Order =
        [StepId.Adapter, StepId.Link, StepId.IpAddress, StepId.Gateway, StepId.PublicIp, StepId.Dns, StepId.Target];

    private readonly Dictionary<StepId, StepResult> _results = [];
    private StepId _selected = StepId.Adapter;
    private StepId? _hovered;

    public StepTable()
    {
        Cursor = Cursors.Hand;
        foreach (StepId step in Order)
        {
            _results[step] = StepResult.Pending(step);
        }
    }

    public event Action<StepId>? SelectionChanged;

    protected override Color BehindColor => Theme.Current.Surface;

    public StepId Selected => _selected;

    private int RowHeight => Dp(44);

    /// <summary>
    /// Applies a step's new state. Only a genuinely resolved result (Ok/Warning/Failed/Skipped)
    /// ever changes what the row shows - the pipeline republishes every step on every cycle, and
    /// without this a perfectly healthy row would flash its status to "waiting"/"testing..." on
    /// every routine background refresh, several times a minute, even though nothing had changed.
    /// </summary>
    public void Apply(StepResult result)
    {
        if (result.State.IsResolved() || !_results.TryGetValue(result.Step, out StepResult? shown) || !shown.State.IsResolved())
        {
            _results[result.Step] = result;
            Invalidate();
        }
    }

    public StepResult ResultFor(StepId step) => _results[step];

    private int IndexAt(int y) => y / Math.Max(1, RowHeight);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Y);
        StepId? hovered = index >= 0 && index < Order.Length ? Order[index] : null;
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
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
        if (index < 0 || index >= Order.Length)
        {
            return;
        }

        StepId step = Order[index];
        if (step != _selected)
        {
            _selected = step;
            Invalidate();
        }

        SelectionChanged?.Invoke(step);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        int rowHeight = RowHeight;
        int icon = Dp(18);

        for (int i = 0; i < Order.Length; i++)
        {
            StepId step = Order[i];
            StepResult result = _results[step];
            int y = i * rowHeight;

            var row = new RectangleF(Sp(1), y + 1, Width - Sp(2), rowHeight - 2);
            if (step == _selected)
            {
                g.FillRounded(row, Theme.RadiusControl(DeviceDpi), p.AccentSoft);
            }
            else if (step == _hovered)
            {
                g.FillRounded(row, Theme.RadiusControl(DeviceDpi), p.SurfaceHover);
            }

            Severity severity = StepList.SeverityOf(result.State);
            Color iconColor = result.State is StepState.Pending or StepState.Running or StepState.Skipped
                ? p.Idle
                : SeverityColors.Strong(severity);

            Icons.Draw(g, StepList.Glyphs[step], new RectangleF(Sp(2), y + ((rowHeight - icon) / 2f), icon, icon), iconColor, 1.8f);

            int textLeft = Sp(2) + icon + Sp(2);
            // Wide enough for "Overgeslagen" / "Übersprungen" - the longest status words across
            // the four languages - at SmallStrong size, so the status never truncates with an
            // ellipsis on the one word in this row whose entire job is to be read correctly.
            int pillWidth = Dp(92);
            int textRight = Width - Sp(2) - pillWidth - Sp(1);

            g.DrawTextCrisp(
                loc.Get($"step.{step.Key()}.name"),
                step == _selected ? Theme.Font(FontRole.BodyStrong) : Theme.Font(FontRole.Body),
                p.TextPrimary,
                new Rectangle(textLeft, y, Math.Max(0, textRight - textLeft), rowHeight),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            string statusText = result.State switch
            {
                StepState.Pending => loc.Get("stepState.pending"),
                StepState.Running => loc.Get("stepState.running"),
                StepState.Ok => loc.Get("diagnostics.status.ok"),
                StepState.Warning => loc.Get("diagnostics.status.warning"),
                StepState.Failed => loc.Get("diagnostics.status.failed"),
                StepState.Skipped => loc.Get("diagnostics.status.skipped"),
                _ => string.Empty,
            };

            Color statusColor = result.State is StepState.Pending or StepState.Running or StepState.Skipped
                ? p.TextMuted
                : SeverityColors.Strong(severity);

            g.DrawTextCrisp(
                statusText,
                Theme.Font(FontRole.SmallStrong),
                statusColor,
                new Rectangle(Width - Sp(2) - pillWidth, y, pillWidth, rowHeight),
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (i < Order.Length - 1)
            {
                using var pen = new Pen(p.Border, 1f);
                g.DrawLine(pen, Sp(2), y + rowHeight, Width - Sp(2), y + rowHeight);
            }
        }
    }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, Order.Length * RowHeight);
}
