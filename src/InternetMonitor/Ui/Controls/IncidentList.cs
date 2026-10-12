using InternetMonitor.Core.Incidents;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The list of recorded incidents: severity, id, when it started, how long it lasted, and the
/// headline.
///
/// Owner-drawn rather than a ListView so it matches the rest of the app in both themes and can
/// give an active incident a visibly different treatment - a ListView can colour text and nothing
/// else, which is how v1's incident list ended up relying on red text alone.
/// </summary>
public sealed class IncidentList : ThemedControl
{
    private IReadOnlyList<Incident> _incidents = [];
    private int _selectedIndex;
    private int _hoverIndex = -1;
    private int _scrollOffset;

    public IncidentList()
    {
        Cursor = Cursors.Hand;
    }

    public event Action<Incident>? SelectionChanged;

    protected override Color BehindColor => Theme.Current.Surface;

    public Incident? Selected =>
        _selectedIndex >= 0 && _selectedIndex < _incidents.Count ? _incidents[_selectedIndex] : null;

    private int RowHeight => Dp(54);

    public void SetIncidents(IReadOnlyList<Incident> incidents)
    {
        string? previousId = Selected?.Id;
        _incidents = incidents;

        // Keep the user's selection across a refresh. Resetting to the top every few seconds while
        // someone is reading an incident would be maddening.
        _selectedIndex = previousId is null ? 0 : Math.Max(0, _incidents.ToList().FindIndex(i => i.Id == previousId));
        ClampScroll();
        Invalidate();
    }

    public void SelectFirst()
    {
        if (_incidents.Count > 0)
        {
            _selectedIndex = 0;
            SelectionChanged?.Invoke(_incidents[0]);
            Invalidate();
        }
    }

    private int IndexAt(int y)
    {
        int index = (y + _scrollOffset) / Math.Max(1, RowHeight);
        return index >= 0 && index < _incidents.Count ? index : -1;
    }

    private void ClampScroll()
    {
        int contentHeight = _incidents.Count * RowHeight;
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, contentHeight - Height));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollOffset -= e.Delta;
        ClampScroll();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = IndexAt(e.Y);
        if (index != _hoverIndex)
        {
            _hoverIndex = index;
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
        int index = IndexAt(e.Y);
        if (e.Button != MouseButtons.Left || index < 0)
        {
            return;
        }

        _selectedIndex = index;
        Invalidate();
        SelectionChanged?.Invoke(_incidents[index]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        if (_incidents.Count == 0)
        {
            g.DrawTextCrisp(
                loc.Get("incidents.empty"),
                Theme.Font(FontRole.Body),
                p.TextMuted,
                ClientRectangle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int rowHeight = RowHeight;
        int first = Math.Max(0, _scrollOffset / rowHeight);
        int last = Math.Min(_incidents.Count - 1, (_scrollOffset + Height) / rowHeight);

        for (int i = first; i <= last; i++)
        {
            Incident incident = _incidents[i];
            int y = (i * rowHeight) - _scrollOffset;
            bool selected = i == _selectedIndex;
            bool active = incident.Status == IncidentStatus.Active;

            var row = new RectangleF(Sp(1), y + 1, Width - Sp(2), rowHeight - 2);
            if (selected)
            {
                g.FillRounded(row, Theme.RadiusControl(DeviceDpi), p.AccentSoft);
            }
            else if (i == _hoverIndex)
            {
                g.FillRounded(row, Theme.RadiusControl(DeviceDpi), p.SurfaceHover);
            }

            Severity severity = active ? Severity.Error : Severity.Idle;
            Color strong = SeverityColors.Strong(severity);

            // A solid bar on the leading edge rather than coloured text: an open incident has to
            // be distinguishable at a glance and without relying on colour perception alone, so
            // the bar is accompanied by the word "active" in the status line below.
            var marker = new RectangleF(Sp(1), y + Dp(10), DpF(3f), rowHeight - Dp(20));
            g.FillRounded(marker, DpF(2f), strong);

            int left = Sp(3) + Dp(4);
            int right = Width - Sp(2);

            g.DrawTextCrisp(
                incident.Headline,
                Theme.Font(FontRole.BodyStrong),
                p.TextPrimary,
                new Rectangle(left, y + Dp(8), Math.Max(0, right - left), Dp(18)));

            string meta = string.Join(
                "   ",
                incident.Id,
                incident.StartUtc.ToLocalTime().ToString("dd/MM HH:mm"),
                active
                    ? loc.Get("incidents.active")
                    : IncidentTracker.FormatDuration(incident.Duration));

            g.DrawTextCrisp(
                meta,
                Theme.Font(FontRole.Small),
                active ? strong : p.TextMuted,
                new Rectangle(left, y + Dp(28), Math.Max(0, right - left), Dp(16)));
        }
    }
}
