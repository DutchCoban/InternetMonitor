using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The list of user-added step-7 targets: name, URL, an enable toggle, and a remove button per
/// row. Enabling/disabling and removing happen right in the row - this is a short, infrequently
/// touched list, not large enough to need a separate selection-plus-detail layout the way
/// incidents or steps do.
/// </summary>
public sealed class EndpointList : ThemedControl
{
    private readonly List<EndpointConfig> _endpoints = [];
    private readonly List<Toggle> _toggles = [];
    private readonly List<AppButton> _removeButtons = [];
    private int _hoveredRow = -1;

    public event Action<EndpointConfig>? EditRequested;

    public event Action<EndpointConfig>? ToggleChanged;

    public event Action<EndpointConfig>? RemoveRequested;

    protected override Color BehindColor => Theme.Current.Surface;

    private int RowHeight => Dp(40);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(proposedSize.Width, Math.Max(RowHeight, _endpoints.Count * RowHeight));

    public void SetEndpoints(IReadOnlyList<EndpointConfig> endpoints)
    {
        foreach (Toggle toggle in _toggles)
        {
            toggle.CheckedChanged -= OnToggleChanged;
            Controls.Remove(toggle);
            toggle.Dispose();
        }

        foreach (AppButton button in _removeButtons)
        {
            Controls.Remove(button);
            button.Dispose();
        }

        _toggles.Clear();
        _removeButtons.Clear();
        _endpoints.Clear();
        _endpoints.AddRange(endpoints);

        foreach (EndpointConfig endpoint in _endpoints)
        {
            var toggle = new Toggle { Tag = endpoint.Id };
            toggle.SetWithoutNotify(endpoint.Enabled);
            toggle.CheckedChanged += OnToggleChanged;
            Controls.Add(toggle);
            _toggles.Add(toggle);

            var remove = new AppButton { Variant = ButtonVariant.Ghost, Glyph = Glyph.Cross, Tag = endpoint.Id };
            remove.Click += OnRemoveClicked;
            Controls.Add(remove);
            _removeButtons.Add(remove);
        }

        LayoutRows();
        Invalidate();
    }

    private void OnToggleChanged(object? sender, EventArgs e)
    {
        if (sender is not Toggle { Tag: string id } toggle)
        {
            return;
        }

        EndpointConfig? endpoint = _endpoints.FirstOrDefault(x => x.Id == id);
        if (endpoint is not null)
        {
            ToggleChanged?.Invoke(endpoint with { Enabled = toggle.Checked });
        }
    }

    private void OnRemoveClicked(object? sender, EventArgs e)
    {
        if (sender is not AppButton { Tag: string id })
        {
            return;
        }

        EndpointConfig? endpoint = _endpoints.FirstOrDefault(x => x.Id == id);
        if (endpoint is not null)
        {
            RemoveRequested?.Invoke(endpoint);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutRows();
    }

    private void LayoutRows()
    {
        int toggleSize = Dp(22);
        int removeSize = Dp(26);

        for (int i = 0; i < _endpoints.Count; i++)
        {
            int y = i * RowHeight;
            _toggles[i].Bounds = new Rectangle(Sp(2), y + ((RowHeight - toggleSize) / 2), toggleSize * 2, toggleSize);
            _removeButtons[i].Bounds = new Rectangle(Width - Sp(2) - removeSize, y + ((RowHeight - removeSize) / 2), removeSize, removeSize);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int row = IndexAt(e.Y);
        if (row != _hoveredRow)
        {
            _hoveredRow = row;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredRow = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // Clicking the name/URL text (not the toggle or remove button, which handle their own
        // clicks) opens the edit dialog.
        int row = IndexAt(e.Y);
        if (row < 0 || row >= _endpoints.Count || e.X < Dp(56) || e.X > Width - Sp(2) - Dp(30))
        {
            return;
        }

        EditRequested?.Invoke(_endpoints[row]);
    }

    private int IndexAt(int y) => y / Math.Max(1, RowHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        if (_endpoints.Count == 0)
        {
            g.DrawTextCrisp(
                loc.Get("settings.endpoints.empty"),
                Theme.Font(FontRole.Small),
                p.TextMuted,
                ClientRectangle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        int textLeft = Dp(56);
        int textRight = Width - Sp(2) - Dp(34);

        for (int i = 0; i < _endpoints.Count; i++)
        {
            EndpointConfig endpoint = _endpoints[i];
            int y = i * RowHeight;

            if (i == _hoveredRow)
            {
                g.FillRounded(
                    new RectangleF(Sp(1), y + 1, Width - Sp(2), RowHeight - 2),
                    Theme.RadiusControl(DeviceDpi),
                    p.SurfaceHover);
            }

            Color textColor = endpoint.Enabled ? p.TextPrimary : p.TextMuted;
            g.DrawTextCrisp(
                endpoint.Name,
                Theme.Font(FontRole.BodyStrong),
                textColor,
                new Rectangle(textLeft, y + Dp(4), Math.Max(0, textRight - textLeft), Dp(16)));

            string subtitle = endpoint.Kind == EndpointKind.TcpPort ? $"{endpoint.Host}:{endpoint.Port}" : endpoint.Url;
            g.DrawTextCrisp(
                subtitle,
                Theme.Font(FontRole.Small),
                p.TextMuted,
                new Rectangle(textLeft, y + Dp(20), Math.Max(0, textRight - textLeft), Dp(14)));

            if (i < _endpoints.Count - 1)
            {
                using var pen = new Pen(p.Border, 1f);
                g.DrawLine(pen, Sp(2), y + RowHeight, Width - Sp(2), y + RowHeight);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (Toggle toggle in _toggles)
            {
                toggle.CheckedChanged -= OnToggleChanged;
            }

            foreach (AppButton button in _removeButtons)
            {
                button.Click -= OnRemoveClicked;
            }
        }

        base.Dispose(disposing);
    }
}
