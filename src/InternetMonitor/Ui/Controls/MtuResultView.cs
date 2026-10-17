using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Controls;

/// <summary>
/// The MTU tool's result: one status line and a few lines of explanation underneath. Nothing more
/// is needed - the result is a number and what it means.
/// </summary>
public sealed class MtuResultView : ThemedControl
{
    private string _status = string.Empty;
    private IReadOnlyList<string> _lines = [];

    protected override Color BehindColor => Theme.Current.Surface;

    public string Status
    {
        get => _status;
        set
        {
            _status = value;
            Invalidate();
        }
    }

    public void Show(string status, IReadOnlyList<string> lines)
    {
        _status = status;
        _lines = lines;
        Invalidate();
    }

    public void Clear()
    {
        _lines = [];
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.UseHighQuality();
        Palette p = Theme.Current;

        int width = Math.Max(Dp(120), Width - Sp(4));
        int y = Sp(2);

        g.DrawTextCrisp(_status, Theme.Font(FontRole.BodyStrong), p.TextPrimary, new Rectangle(Sp(2), y, width, Dp(20)));
        y += Dp(26);

        foreach (string line in _lines)
        {
            Size size = GdiExtensions.MeasureCrisp(line, Theme.Font(FontRole.Body), width);
            g.DrawTextCrisp(line, Theme.Font(FontRole.Body), p.TextSecondary, new Rectangle(Sp(2), y, width, size.Height), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            y += size.Height + Sp(2);
        }
    }
}
