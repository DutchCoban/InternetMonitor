using System.Globalization;
using InternetMonitor.Core.Diagnosis;
using Severity = InternetMonitor.Ui.Theming.Severity;
using InternetMonitor.Core.Reports;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The last 24 hours as a row of quarter-hour blocks, and the last seven days underneath with their
/// availability - "was it restless today?" answered without opening History.
///
/// Colour carries the state, and it is never the only carrier: every block and day has a tooltip
/// that says it in words, the days print their percentage, and a legend names the colours. A block
/// with an incident behind it opens that incident when clicked.
/// </summary>
public sealed class UptimeStrip : ThemedControl
{
    private readonly ToolTip _toolTip = new() { InitialDelay = 150, ReshowDelay = 50 };
    private readonly List<(Rectangle Bounds, string Tip, string? IncidentId)> _hits = [];
    private UptimeOverview? _overview;
    private int _hoverIndex = -1;

    public event Action<string>? IncidentClicked;

    protected override Color BehindColor => Theme.Current.Surface;

    // The block row, the axis-and-legend line under it, and the row of days - see OnPaint.
    public int PreferredHeight => Dp(20) + Dp(2) + Dp(20) + Dp(26) + Dp(2);

    public void SetOverview(UptimeOverview overview)
    {
        _overview = overview;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;
        _hits.Clear();

        if (_overview is null || Width <= 0)
        {
            return;
        }

        // ---- 24 hours, quarter-hour blocks ----
        int blockTop = 0;
        int blockHeight = Dp(20);
        int gap = Math.Max(1, Dp(1));
        IReadOnlyList<UptimeBlock> blocks = _overview.Blocks;
        float slot = (Width + gap) / (float)Math.Max(1, blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            UptimeBlock block = blocks[i];
            int x = (int)Math.Round(i * slot);
            int nextX = (int)Math.Round((i + 1) * slot) - gap;
            var bounds = new Rectangle(x, blockTop, Math.Max(1, nextX - x), blockHeight);
            bool hovered = _hits.Count == _hoverIndex;

            // A grey block is drawn lower and thinner: "not measured" is an absence, and it should
            // not read as a fourth kind of state at a glance.
            Rectangle drawn = block.State == UptimeState.NotMeasured
                ? new Rectangle(bounds.X, bounds.Y + (bounds.Height / 3), bounds.Width, bounds.Height / 3)
                : bounds;
            g.FillRounded(drawn, DpF(1.5f), hovered ? Darken(ColorOf(block.State)) : ColorOf(block.State));

            _hits.Add((bounds, BlockTip(block, loc), block.IncidentId));
        }

        // ---- Axis labels and legend ----
        int axisTop = blockTop + blockHeight + Dp(2);
        Font small = Theme.Font(FontRole.Small);
        g.DrawTextCrisp(loc.Get("uptime.axis.start"), small, p.TextMuted, new Rectangle(0, axisTop, Width / 3, Dp(16)));
        g.DrawTextCrisp(loc.Get("uptime.axis.now"), small, p.TextMuted, new Rectangle(Width - (Width / 3), axisTop, Width / 3, Dp(16)), TextFormatFlags.Right);
        DrawLegend(g, loc, small, axisTop);

        // ---- Seven days ----
        int dayTop = axisTop + Dp(20);
        int dayHeight = Dp(26);
        IReadOnlyList<UptimeDay> days = _overview.Days;
        int dayGap = Sp(1);
        float daySlot = (Width + dayGap) / (float)Math.Max(1, days.Count);
        for (int i = 0; i < days.Count; i++)
        {
            UptimeDay day = days[i];
            int x = (int)Math.Round(i * daySlot);
            int nextX = (int)Math.Round((i + 1) * daySlot) - dayGap;
            var bounds = new Rectangle(x, dayTop, Math.Max(1, nextX - x), dayHeight);
            bool hovered = _hits.Count == _hoverIndex;
            Severity severity = SeverityOf(day.State);

            g.FillRounded(bounds, Theme.RadiusControl(DeviceDpi), hovered ? Darken(SeverityColors.Soft(severity)) : SeverityColors.Soft(severity));
            string text = $"{loc.Get($"uptime.day.{day.Day.DayOfWeek.ToString().ToLowerInvariant()}")}  {(day.Availability is { } a && day.State != UptimeState.NotMeasured ? Percent(a) : "-")}";
            g.DrawTextCrisp(
                text,
                Theme.Font(FontRole.SmallStrong),
                day.State == UptimeState.NotMeasured ? p.TextMuted : SeverityColors.Strong(severity),
                bounds,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            _hits.Add((bounds, DayTip(day, loc), null));
        }
    }

    private void DrawLegend(Graphics g, LocalizationManager loc, Font font, int top)
    {
        (UptimeState State, string Key)[] items =
        [
            (UptimeState.Ok, "uptime.legend.ok"),
            (UptimeState.Warning, "uptime.legend.warning"),
            (UptimeState.Outage, "uptime.legend.outage"),
            (UptimeState.NotMeasured, "uptime.legend.notMeasured"),
        ];

        int swatch = Dp(9);
        int spacing = Sp(2);
        int[] widths = [.. items.Select(i => swatch + Dp(4) + GdiExtensions.MeasureCrisp(loc.Get(i.Key), font).Width)];
        int total = widths.Sum() + (spacing * (items.Length - 1));

        // Only when it fits between the two axis labels; a narrow window keeps the tooltips.
        if (total > Width / 2)
        {
            return;
        }

        int x = (Width - total) / 2;
        for (int i = 0; i < items.Length; i++)
        {
            g.FillRounded(new Rectangle(x, top + ((Dp(16) - swatch) / 2), swatch, swatch), DpF(2f), ColorOf(items[i].State));
            g.DrawTextCrisp(loc.Get(items[i].Key), font, Theme.Current.TextMuted, new Rectangle(x + swatch + Dp(4), top, widths[i], Dp(16)));
            x += widths[i] + spacing;
        }
    }

    private static Color ColorOf(UptimeState state) => state switch
    {
        UptimeState.Ok => Theme.Current.Ok,
        UptimeState.Warning => Theme.Current.Warn,
        UptimeState.Outage => Theme.Current.Error,
        _ => Theme.Current.Border,
    };

    private static Severity SeverityOf(UptimeState state) => state switch
    {
        UptimeState.Ok => Severity.Ok,
        UptimeState.Warning => Severity.Warning,
        UptimeState.Outage => Severity.Error,
        _ => Severity.Idle,
    };

    private static Color Darken(Color color) => Color.FromArgb(color.A, color.R * 85 / 100, color.G * 85 / 100, color.B * 85 / 100);

    private static string Percent(double fraction) =>
        (fraction * 100).ToString(fraction is > 0.999 and < 1 ? "F2" : fraction >= 1 ? "F0" : "F1", CultureInfo.InvariantCulture) + "%";

    private static string BlockTip(UptimeBlock block, LocalizationManager loc)
    {
        string span = $"{block.StartUtc.ToLocalTime():HH:mm}-{block.EndUtc.ToLocalTime():HH:mm}";
        string state = loc.Get($"uptime.state.{block.State.ToString().ToLowerInvariant()}");
        string tip = $"{span}  {state}";
        if (block.Cause is { } cause)
        {
            tip += $"\n{loc.Get($"cause.{cause.Key()}")}  ({block.IncidentId})\n{loc.Get("uptime.clickToOpen")}";
        }

        return tip;
    }

    private static string DayTip(UptimeDay day, LocalizationManager loc) =>
        day.State == UptimeState.NotMeasured
            ? $"{day.Day:dd-MM-yyyy}  {loc.Get("uptime.state.notmeasured")}"
            : $"{day.Day:dd-MM-yyyy}\n{loc.Format("uptime.day.tip", Percent(day.Availability ?? 0), day.Outages, Percent(day.Coverage))}";

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = _hits.FindIndex(h => h.Bounds.Contains(e.Location));
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        Cursor = index >= 0 && _hits[index].IncidentId is not null ? Cursors.Hand : Cursors.Default;
        _toolTip.SetToolTip(this, index >= 0 ? _hits[index].Tip : null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int index = _hits.FindIndex(h => h.Bounds.Contains(e.Location));
        if (e.Button == MouseButtons.Left && index >= 0 && _hits[index].IncidentId is { } id)
        {
            IncidentClicked?.Invoke(id);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
