using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// Everything one incident recorded, laid out to be read: headline and metadata, the three
/// plain-language paragraphs, what changed just before, the timeline, every step's result when it
/// started, the route that was traced when it opened, and the user's own note. The note is the one
/// editable thing on the page - a ticket number, "called the provider" - and is saved when the
/// field loses focus.
/// </summary>
public sealed class IncidentDetail : ThemedControl
{
    private readonly TextBox _note;
    private Incident? _incident;
    private IncidentThroughput? _throughput;
    private int _scrollOffset;
    private int _contentHeight;
    private bool _loadingNote;

    public IncidentDetail()
    {
        _note = new TextBox
        {
            Multiline = true,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Font(FontRole.Body),
            Visible = false,
            ScrollBars = ScrollBars.Vertical,
        };
        _note.Leave += (_, _) => CommitNote();
        Controls.Add(_note);
    }

    /// <summary>The user edited the note of the shown incident; the page persists it.</summary>
    public event Action<Incident>? NoteChanged;

    protected override Color BehindColor => Theme.Current.Surface;

    /// <param name="throughput">The traffic picture to show under the incident - resolved by the page, which has the history; null leaves the section out.</param>
    public void Show(Incident? incident, IncidentThroughput? throughput = null)
    {
        CommitNote();
        _incident = incident;
        _throughput = throughput;
        _scrollOffset = 0;

        _loadingNote = true;
        _note.Text = incident?.Note ?? string.Empty;
        _note.PlaceholderText = LocalizationManager.Instance.Get("incidents.note.placeholder");
        _note.Visible = incident is not null;
        _loadingNote = false;

        Invalidate();
    }

    private void CommitNote()
    {
        if (_loadingNote || _incident is not { } incident)
        {
            return;
        }

        string text = _note.Text.Trim();
        string? note = text.Length == 0 ? null : text;
        if (note != incident.Note)
        {
            incident.Note = note;
            NoteChanged?.Invoke(incident);
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

        y = DrawNote(g, p, loc, width, y);

        if (incident.RecentChanges.Count > 0)
        {
            y = DrawList(g, p, loc.Get("incidents.recentChanges"), incident.RecentChanges, width, y);
        }

        if (_throughput is { } traffic)
        {
            y = DrawList(g, p, loc.Get("incidents.traffic"), traffic.Describe(loc.Get, loc.Format), width, y);
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

        if (incident.Traceroute is { } route)
        {
            y = DrawList(g, p, loc.Format("incidents.route", route.Target), route.Describe(loc.Get("incidents.route.noAnswer")), width, y);
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

    /// <summary>The note's label, with the text box placed under it - moved along with the painted content as the page scrolls.</summary>
    private int DrawNote(Graphics g, Palette p, LocalizationManager loc, int width, int y)
    {
        g.DrawTextCrisp(loc.Get("incidents.note").ToUpperInvariant(), Theme.Font(FontRole.SmallStrong), p.TextMuted, new Rectangle(Sp(2), y, width, Dp(14)));
        y += Dp(17);

        var bounds = new Rectangle(Sp(2), y, width, Dp(60));
        if (_note.Bounds != bounds)
        {
            _note.Bounds = bounds;
        }

        if (_note.BackColor != p.SurfaceAlt)
        {
            _note.BackColor = p.SurfaceAlt;
            _note.ForeColor = p.TextPrimary;
        }

        return y + bounds.Height + Sp(3);
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
