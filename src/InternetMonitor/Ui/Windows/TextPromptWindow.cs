using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Windows;

/// <summary>
/// The smallest modal there is: one question, one text field, OK and Cancel. Used to name a device
/// in the network scan. A plain Form for the same reason <see cref="EndpointEditWindow"/> is one -
/// a native title bar and Enter/Escape handling are exactly what a short dialog needs.
/// </summary>
public sealed class TextPromptWindow : Form
{
    private readonly TextBox _field;

    private TextPromptWindow(string title, string label, string initial)
    {
        Palette palette = Theme.Current;
        LocalizationManager loc = LocalizationManager.Instance;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = palette.Bg;
        Icon = TrayIconFactory.AppIcon.Value;
        ClientSize = new Size(Dp(360), Dp(120));

        var caption = new Label
        {
            AutoSize = false,
            Text = label,
            ForeColor = palette.TextMuted,
            BackColor = palette.Bg,
            Font = Theme.Font(FontRole.Small),
            Bounds = new Rectangle(Sp(4), Sp(4), ClientSize.Width - Sp(8), Dp(16)),
        };

        _field = new TextBox
        {
            Text = initial,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = palette.SurfaceAlt,
            ForeColor = palette.TextPrimary,
            Font = Theme.Font(FontRole.Body),
            Bounds = new Rectangle(Sp(4), Sp(4) + Dp(18), ClientSize.Width - Sp(8), Dp(24)),
        };

        var ok = new AppButton { Variant = ButtonVariant.Primary, Text = loc.Get("settings.save") };
        ok.SizeToContent();
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        var cancel = new AppButton { Variant = ButtonVariant.Secondary, Text = loc.Get("settings.cancel") };
        cancel.SizeToContent();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        int buttonsY = ClientSize.Height - Sp(4) - ok.Height;
        ok.Location = new Point(ClientSize.Width - Sp(4) - ok.Width, buttonsY);
        cancel.Location = new Point(ok.Left - Sp(2) - cancel.Width, buttonsY);

        Controls.Add(caption);
        Controls.Add(_field);
        Controls.Add(cancel);
        Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>Asks; the entered text, or null when cancelled.</summary>
    public static string? Ask(IWin32Window? owner, string title, string label, string initial)
    {
        using var window = new TextPromptWindow(title, label, initial);
        return window.ShowDialog(owner) == DialogResult.OK ? window._field.Text : null;
    }

    private int Dp(int px) => Theme.Dp(px, DeviceDpi);

    private int Sp(int step) => Theme.Space(step, DeviceDpi);
}
