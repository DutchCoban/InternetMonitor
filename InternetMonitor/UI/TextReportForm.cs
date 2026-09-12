using InternetMonitor.Localization;

namespace InternetMonitor.UI;

/// <summary>Simple read-only multi-line text viewer with a copy-to-clipboard button, reused for incident details.</summary>
public sealed class TextReportForm : Form
{
    private readonly Font _monospaceFont;

    public TextReportForm(string title, string content)
    {
        Text = title;
        Icon = TrayIconFactory.AppIcon.Value;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 420);
        MinimumSize = new Size(360, 260);

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = _monospaceFont = new Font(FontFamily.GenericMonospace, 9f),
            Text = content.Replace("\n", "\r\n"),
            Dock = DockStyle.Fill,
        };

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var copyButton = new Button
        {
            Text = LocalizationManager.Instance.Get("diag.copy"),
            Bounds = new Rectangle(8, 8, 140, 28),
        };
        copyButton.Click += (_, _) =>
        {
            try { Clipboard.SetText(content); } catch (System.Runtime.InteropServices.ExternalException) { }
        };
        var closeButton = new Button
        {
            Text = LocalizationManager.Instance.Get("settings.close"),
            Bounds = new Rectangle(156, 8, 90, 28),
        };
        closeButton.Click += (_, _) => Close();

        buttonPanel.Controls.Add(copyButton);
        buttonPanel.Controls.Add(closeButton);

        Controls.Add(textBox);
        Controls.Add(buttonPanel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // A new instance is created every time an incident detail view is opened, so this
            // custom monospace Font must be disposed explicitly or each view leaks a GDI handle.
            _monospaceFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
