using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// One incident, laid out to be read top to bottom by someone who wants to know what happened:
/// the plain-language explanation first, then what changed just beforehand, then the timeline,
/// then the raw step states.
///
/// The ordering is the point. v1 showed the raw probe dump and nothing else, which answers "what
/// did the tool measure" but not "what went wrong" - and the second question is the one people
/// actually have. The measurements are still here, at the bottom, for when they are needed.
/// </summary>
public sealed class IncidentDetail : ThemedControl
{
    private Incident? _incident;
    private int _scrollOffset;
    private int _contentHeight;

    protected override Color BehindColor => Theme.Current.Surface;

    public void Show(Incident? incident)
    {
        _incident = incident;
        _scrollOffset = 0;
        Invalidate();
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

        if (_incident is not { } incident)
        {
            g.DrawTextCrisp(
                loc.Get("incidents.selectOne"),
                Theme.Font(FontRole.Body),
                p.TextMuted,
                ClientRectangle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int width = Math.Max(Dp(120), Width - Sp(4));
        int y = Sp(1) - _scrollOffset;

        y = DrawHeading(g, p, incident, width, y);

        if (incident.Explanation is { } explanation)
        {
            y = DrawParagraph(g, p, loc.Get("incidents.what"), explanation.What, width, y);
            y = DrawParagraph(g, p, loc.Get("incidents.likelyCause"), explanation.LikelyCause, width, y);
            y = DrawParagraph(g, p, loc.Get("incidents.whatToDo"), explanation.WhatToDo, width, y);
        }

        if (incident.RecentChanges.Count > 0)
        {
            y = DrawList(g, p, loc.Get("incidents.recentChanges"), incident.RecentChanges, width, y);
        }

        if (incident.Timeline.Count > 0)
        {
            y = DrawList(
                g,
                p,
                loc.Get("incidents.timeline"),
                [.. incident.Timeline.Select(t => $"{t.AtUtc.ToLocalTime():HH:mm:ss}  {t.Text}")],
                width,
                y);
        }

        if (incident.StepsAtStart.Count > 0)
        {
            y = DrawList(
                g,
                p,
                loc.Get("incidents.stepsAtStart"),
                [.. incident.StepsAtStart.Select(s => $"{loc.Get($"step.{s.Step.Key()}.name")}: {s.Headline}")],
                width,
                y);
        }

        _contentHeight = y + _scrollOffset + Sp(3);
    }

    private int DrawHeading(Graphics g, Palette p, Incident incident, int width, int y)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        bool active = incident.Status == IncidentStatus.Active;

        g.DrawTextCrisp(
            incident.Headline,
            Theme.Font(FontRole.Title),
            p.TextPrimary,
            new Rectangle(Sp(2), y, width, Dp(26)));
        y += Dp(28);

        string meta = string.Join(
            "   ·   ",
            incident.Id,
            incident.StartUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
            active ? loc.Get("incidents.active") : IncidentTracker.FormatDuration(incident.Duration),
            loc.Get($"cause.{incident.Cause.Key()}"));

        g.DrawTextCrisp(meta, Theme.Font(FontRole.Small), p.TextMuted, new Rectangle(Sp(2), y, width, Dp(16)));
        return y + Dp(24);
    }

    private int DrawParagraph(Graphics g, Palette p, string label, string body, int width, int y)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return y;
        }

        g.DrawTextCrisp(label.ToUpperInvariant(), Theme.Font(FontRole.SmallStrong), p.TextMuted, new Rectangle(Sp(2), y, width, Dp(14)));
        y += Dp(17);

        // Measured with word wrapping so the block grows to fit the text instead of clipping it,
        // which matters when the same sentence is a third longer in German.
        Size size = GdiExtensions.MeasureCrisp(body, Theme.Font(FontRole.Body), width);
        g.DrawTextCrisp(
            body,
            Theme.Font(FontRole.Body),
            p.TextSecondary,
            new Rectangle(Sp(2), y, width, size.Height),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        return y + size.Height + Sp(3);
    }

    private int DrawList(Graphics g, Palette p, string label, IReadOnlyList<string> lines, int width, int y)
    {
        g.DrawTextCrisp(label.ToUpperInvariant(), Theme.Font(FontRole.SmallStrong), p.TextMuted, new Rectangle(Sp(2), y, width, Dp(14)));
        y += Dp(17);

        foreach (string line in lines)
        {
            Size size = GdiExtensions.MeasureCrisp(line, Theme.Font(FontRole.Small), width - Sp(2));
            g.DrawTextCrisp(
                line,
                Theme.Font(FontRole.Small),
                p.TextSecondary,
                new Rectangle(Sp(3), y, width - Sp(2), size.Height),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y += size.Height + Dp(3);
        }

        return y + Sp(2);
    }
}
