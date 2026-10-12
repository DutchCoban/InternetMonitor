using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Windows;

/// <summary>
/// The small modal for adding or editing one step-7 target: a name and an HTTPS URL. Kept as a
/// plain Form rather than a custom-drawn page - a two-field dialog with an OK/Cancel pair is
/// exactly what a native title bar and Enter/Escape handling are for, and building a custom modal
/// surface for this would be more code for a worse result.
/// </summary>
public sealed class EndpointEditWindow : Form
{
    private readonly TextBox _name;
    private readonly TextBox _url;
    private readonly Label _error;
    private readonly AppButton _save;

    public EndpointEditWindow(EndpointConfig? existing)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        Palette p = Theme.Current;

        Text = loc.Get(existing is null ? "settings.endpoint.add" : "settings.endpoint.edit");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(Theme.Dp(360, DeviceDpi), Theme.Dp(190, DeviceDpi));
        BackColor = p.Bg;
        Icon = InternetMonitor.Ui.TrayIconFactory.AppIcon.Value;

        int left = Theme.Space(4, DeviceDpi);
        int fieldWidth = ClientSize.Width - (left * 2);
        int y = Theme.Space(4, DeviceDpi);

        Label nameLabel = MakeLabel(loc.Get("settings.endpoint.name"), left, y, p);
        y += Theme.Dp(18, DeviceDpi);
        _name = MakeTextBox(existing?.Name ?? string.Empty, left, y, fieldWidth, p);
        y += Theme.Dp(34, DeviceDpi);

        Label urlLabel = MakeLabel(loc.Get("settings.endpoint.url"), left, y, p);
        y += Theme.Dp(18, DeviceDpi);
        _url = MakeTextBox(existing?.Url ?? "https://", left, y, fieldWidth, p);
        y += Theme.Dp(28, DeviceDpi);

        _error = new Label
        {
            AutoSize = false,
            Bounds = new Rectangle(left, y, fieldWidth, Theme.Dp(18, DeviceDpi)),
            ForeColor = p.Error,
            BackColor = p.Bg,
            Font = Theme.Font(FontRole.Small),
            Text = string.Empty,
        };

        var cancel = new AppButton { Variant = ButtonVariant.Secondary, Text = loc.Get("settings.cancel") };
        cancel.SizeToContent();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _save = new AppButton { Variant = ButtonVariant.Primary, Text = loc.Get("settings.save") };
        _save.SizeToContent();
        _save.Click += OnSaveClicked;

        int buttonsY = ClientSize.Height - Theme.Dp(44, DeviceDpi);
        _save.Location = new Point(ClientSize.Width - left - _save.Width, buttonsY);
        cancel.Location = new Point(_save.Left - Theme.Space(2, DeviceDpi) - cancel.Width, buttonsY);

        Controls.Add(nameLabel);
        Controls.Add(_name);
        Controls.Add(urlLabel);
        Controls.Add(_url);
        Controls.Add(_error);
        Controls.Add(cancel);
        Controls.Add(_save);

        AcceptButton = _save;
        CancelButton = cancel;
    }

    /// <summary>Set once the dialog closes with <see cref="DialogResult.OK"/>.</summary>
    public EndpointConfig? Result { get; private set; }

    private string? _editingId;

    public static EndpointEditWindow ForNew() => new(null);

    public static EndpointEditWindow ForEdit(EndpointConfig existing)
    {
        var window = new EndpointEditWindow(existing) { _editingId = existing.Id };
        return window;
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        LocalizationManager loc = LocalizationManager.Instance;
        string name = _name.Text.Trim();
        string url = _url.Text.Trim();

        if (name.Length == 0)
        {
            _error.Text = loc.Get("settings.endpoint.error.name");
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            _error.Text = loc.Get("settings.endpoint.error.url");
            return;
        }

        Result = _editingId is { } id
            ? new EndpointConfig(id, name, url)
            : EndpointConfig.Create(name, url);

        DialogResult = DialogResult.OK;
        Close();
    }

    private Label MakeLabel(string text, int x, int y, Palette p) => new()
    {
        AutoSize = false,
        Text = text,
        Bounds = new Rectangle(x, y, ClientSize.Width - (x * 2), Theme.Dp(16, DeviceDpi)),
        ForeColor = p.TextMuted,
        BackColor = p.Bg,
        Font = Theme.Font(FontRole.Small),
    };

    private TextBox MakeTextBox(string text, int x, int y, int width, Palette p) => new()
    {
        Text = text,
        Bounds = new Rectangle(x, y, width, Theme.Dp(24, DeviceDpi)),
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = p.SurfaceAlt,
        ForeColor = p.TextPrimary,
        Font = Theme.Font(FontRole.Body),
    };
}
