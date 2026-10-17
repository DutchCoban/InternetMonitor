using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Windows;

/// <summary>
/// The small modal for adding or editing one step-7 target. Kept as a plain Form rather than a
/// custom-drawn page - a short dialog with an OK/Cancel pair is exactly what a native title bar
/// and Enter/Escape handling are for, and building a custom modal surface for this would be more
/// code for a worse result.
///
/// <see cref="_kind"/> switches which fields are visible: a URL target (a website or an API) shows
/// the URL box and an optional expected-status-code field; a TCP-port target (a database, a mail
/// server, anything that does not speak HTTP) shows a host and a port instead. The window resizes
/// around whichever set is showing rather than reserving space for both at once.
/// </summary>
public sealed class EndpointEditWindow : Form
{
    private readonly LocalizationManager _loc = LocalizationManager.Instance;
    private readonly Palette _palette = Theme.Current;

    private readonly Label _nameLabel;
    private readonly TextBox _name;
    private readonly Label _kindLabel;
    private readonly ComboBox _kind;
    private readonly Label _urlLabel;
    private readonly TextBox _url;
    private readonly Label _expectedStatusLabel;
    private readonly NumericUpDown _expectedStatus;
    private readonly Label _headersLabel;
    private readonly List<(TextBox Name, TextBox Value, AppButton Remove)> _headerRows = [];
    private readonly AppButton _addHeader;
    private readonly Label _hostLabel;
    private readonly TextBox _host;
    private readonly Label _portLabel;
    private readonly NumericUpDown _port;
    private readonly Label _warning;
    private readonly Label _error;
    private readonly AppButton _save;
    private readonly AppButton _cancel;

    private string? _editingId;
    private bool _existingEnabled = true;

    public EndpointEditWindow(EndpointConfig? existing)
    {
        Text = _loc.Get(existing is null ? "settings.endpoint.add" : "settings.endpoint.edit");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(Dp(380), Dp(230));
        BackColor = _palette.Bg;
        Icon = InternetMonitor.Ui.TrayIconFactory.AppIcon.Value;

        // Fields for both kinds are created up front; LayoutFields shows the ones that belong to
        // the selected kind, so switching kind never loses what was typed in the other.
        _nameLabel = MakeLabel(_loc.Get("settings.endpoint.name"));
        _name = MakeTextBox(existing?.Name ?? string.Empty);

        _kindLabel = MakeLabel(_loc.Get("settings.endpoint.kind"));
        _kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Font(FontRole.Body) };
        _kind.Items.Add(new KindItem(EndpointKind.Url, _loc.Get("settings.endpoint.kind.url")));
        _kind.Items.Add(new KindItem(EndpointKind.TcpPort, _loc.Get("settings.endpoint.kind.tcpPort")));
        _kind.SelectedIndex = existing?.Kind == EndpointKind.TcpPort ? 1 : 0;
        _kind.SelectedIndexChanged += (_, _) => LayoutFields();

        _urlLabel = MakeLabel(_loc.Get("settings.endpoint.url"));
        _url = MakeTextBox(existing is { Kind: EndpointKind.Url } ? existing.Url : "https://");
        _url.TextChanged += (_, _) => LayoutFields();

        // 0 means "any response counts" - see OnSaveClicked.
        _expectedStatusLabel = MakeLabel(_loc.Get("settings.endpoint.expectedStatus"));
        _expectedStatus = MakeNumeric(0, 599, existing?.ExpectedStatusCode ?? 0);

        _headersLabel = MakeLabel(_loc.Get("settings.endpoint.headers"));
        _addHeader = new AppButton { Variant = ButtonVariant.Secondary, Text = _loc.Get("settings.endpoint.headers.add") };
        _addHeader.SizeToContent();
        _addHeader.Click += (_, _) => { AddHeaderRow(string.Empty, string.Empty); LayoutFields(); };

        _hostLabel = MakeLabel(_loc.Get("settings.endpoint.host"));
        _host = MakeTextBox(existing is { Kind: EndpointKind.TcpPort } ? existing.Host : string.Empty);

        _portLabel = MakeLabel(_loc.Get("settings.endpoint.port"));
        _port = MakeNumeric(1, 65535, existing is { Kind: EndpointKind.TcpPort } ? existing.Port : 443);

        // Shown only for a plain-http URL with headers: the header values are usually an API key
        // or a token, and over http they travel in the clear. A warning, not a block - a check
        // against an internal service that only speaks http is a legitimate thing to set up.
        _warning = new Label
        {
            AutoSize = false,
            ForeColor = _palette.TextMuted,
            BackColor = _palette.Bg,
            Font = Theme.Font(FontRole.Small),
            Text = _loc.Get("settings.endpoint.warn.httpHeaders"),
            Visible = false,
        };

        _error = new Label
        {
            AutoSize = false,
            ForeColor = _palette.Error,
            BackColor = _palette.Bg,
            Font = Theme.Font(FontRole.Small),
            Text = string.Empty,
        };

        _cancel = new AppButton { Variant = ButtonVariant.Secondary, Text = _loc.Get("settings.cancel") };
        _cancel.SizeToContent();
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _save = new AppButton { Variant = ButtonVariant.Primary, Text = _loc.Get("settings.save") };
        _save.SizeToContent();
        _save.Click += OnSaveClicked;

        Controls.Add(_nameLabel);
        Controls.Add(_name);
        Controls.Add(_kindLabel);
        Controls.Add(_kind);
        Controls.Add(_urlLabel);
        Controls.Add(_url);
        Controls.Add(_expectedStatusLabel);
        Controls.Add(_expectedStatus);
        Controls.Add(_headersLabel);
        Controls.Add(_addHeader);
        Controls.Add(_hostLabel);
        Controls.Add(_host);
        Controls.Add(_portLabel);
        Controls.Add(_port);
        Controls.Add(_warning);
        Controls.Add(_error);
        Controls.Add(_cancel);
        Controls.Add(_save);

        AcceptButton = _save;
        CancelButton = _cancel;

        if (existing is { Kind: EndpointKind.Url })
        {
            foreach (EndpointHeader header in existing.Headers)
            {
                AddHeaderRow(header.Name, header.Value);
            }
        }

        LayoutFields();
    }

    /// <summary>Set once the dialog closes with <see cref="DialogResult.OK"/>.</summary>
    public EndpointConfig? Result { get; private set; }

    public static EndpointEditWindow ForNew() => new(null);

    public static EndpointEditWindow ForEdit(EndpointConfig existing)
    {
        var window = new EndpointEditWindow(existing) { _editingId = existing.Id, _existingEnabled = existing.Enabled };
        return window;
    }

    private EndpointKind SelectedKind => (_kind.SelectedItem as KindItem)?.Kind ?? EndpointKind.Url;

    /// <summary>
    /// Positions every field and shows only the ones the selected kind actually uses. Called once
    /// at construction and again whenever the kind changes, so switching never leaves a stray field
    /// from the other kind visible underneath.
    /// </summary>
    private void LayoutFields()
    {
        bool isUrl = SelectedKind == EndpointKind.Url;
        _urlLabel.Visible = isUrl;
        _url.Visible = isUrl;
        _expectedStatusLabel.Visible = isUrl;
        _expectedStatus.Visible = isUrl;
        _headersLabel.Visible = isUrl;
        _addHeader.Visible = isUrl;
        foreach ((TextBox name, TextBox value, AppButton remove) in _headerRows)
        {
            name.Visible = isUrl;
            value.Visible = isUrl;
            remove.Visible = isUrl;
        }

        _hostLabel.Visible = !isUrl;
        _host.Visible = !isUrl;
        _portLabel.Visible = !isUrl;
        _port.Visible = !isUrl;

        int left = Sp(4);
        int fieldWidth = ClientSize.Width - (left * 2);
        int y = Sp(4);

        void Row(Label label, Control field, int fieldHeight)
        {
            label.Bounds = new Rectangle(left, y, fieldWidth, Dp(16));
            y += Dp(18);
            field.Bounds = new Rectangle(left, y, fieldWidth, fieldHeight);
            y += fieldHeight + Sp(2);
        }

        Row(_nameLabel, _name, Dp(24));
        Row(_kindLabel, _kind, Dp(24));

        if (isUrl)
        {
            Row(_urlLabel, _url, Dp(24));
            Row(_expectedStatusLabel, _expectedStatus, Dp(24));

            _headersLabel.Bounds = new Rectangle(left, y, fieldWidth, Dp(16));
            y += Dp(18);

            int removeWidth = Dp(24);
            int gap = Sp(1);
            int nameWidth = (int)((fieldWidth - removeWidth - (gap * 2)) * 0.35);
            int valueWidth = fieldWidth - removeWidth - (gap * 2) - nameWidth;

            foreach ((TextBox name, TextBox value, AppButton remove) in _headerRows)
            {
                name.Bounds = new Rectangle(left, y, nameWidth, Dp(24));
                value.Bounds = new Rectangle(left + nameWidth + gap, y, valueWidth, Dp(24));
                remove.Bounds = new Rectangle(left + nameWidth + gap + valueWidth + gap, y, removeWidth, Dp(24));
                y += Dp(24) + Sp(1);
            }

            _addHeader.Location = new Point(left, y);
            y += _addHeader.Height + Sp(2);

            _warning.Visible = SendsHeadersOverPlainHttp();
            if (_warning.Visible)
            {
                _warning.Bounds = new Rectangle(left, y, fieldWidth, Dp(32));
                y += Dp(34);
            }
        }
        else
        {
            _warning.Visible = false;
            Row(_hostLabel, _host, Dp(24));
            Row(_portLabel, _port, Dp(24));
        }

        _error.Bounds = new Rectangle(left, y, fieldWidth, Dp(18));
        y += Dp(22);

        int buttonsY = y + Sp(2);
        ClientSize = new Size(ClientSize.Width, buttonsY + Dp(44));
        _save.Location = new Point(ClientSize.Width - left - _save.Width, buttonsY);
        _cancel.Location = new Point(_save.Left - Sp(2) - _cancel.Width, buttonsY);
    }

    private bool SendsHeadersOverPlainHttp() =>
        Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttp
        && _headerRows.Any(r => r.Name.Text.Trim().Length > 0);

    /// <summary>
    /// Adds one empty or pre-filled header row (name, value, a small remove button) and relies on
    /// the caller to re-run <see cref="LayoutFields"/> afterward - called both while building the
    /// window (to restore an existing endpoint's headers) and from the "add header" button.
    /// </summary>
    private void AddHeaderRow(string name, string value)
    {
        TextBox nameBox = MakeTextBox(name);
        TextBox valueBox = MakeTextBox(value);
        var remove = new AppButton { Variant = ButtonVariant.Ghost, Glyph = Glyph.Cross };

        Controls.Add(nameBox);
        Controls.Add(valueBox);
        Controls.Add(remove);

        var row = (nameBox, valueBox, remove);
        _headerRows.Add(row);
        nameBox.TextChanged += (_, _) => LayoutFields();

        remove.Click += (_, _) =>
        {
            _headerRows.Remove(row);
            Controls.Remove(nameBox);
            Controls.Remove(valueBox);
            Controls.Remove(remove);
            nameBox.Dispose();
            valueBox.Dispose();
            remove.Dispose();
            LayoutFields();
        };
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            _error.Text = _loc.Get("settings.endpoint.error.name");
            return;
        }

        EndpointConfig result;
        if (SelectedKind == EndpointKind.Url)
        {
            string url = _url.Text.Trim();
            bool validScheme = Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
                && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);
            if (!validScheme)
            {
                _error.Text = _loc.Get("settings.endpoint.error.url");
                return;
            }

            int? expected = _expectedStatus.Value == 0 ? null : (int)_expectedStatus.Value;

            // Rows with no name are dropped rather than rejected - a blank row the user added and
            // left empty is not a mistake worth an error message, it is just not a header yet.
            List<EndpointHeader> headers = [.. _headerRows
                .Where(r => r.Name.Text.Trim().Length > 0)
                .Select(r => new EndpointHeader(r.Name.Text.Trim(), r.Value.Text))];

            result = new EndpointConfig(_editingId ?? Guid.NewGuid().ToString("N"), name, EndpointKind.Url, Url: url, ExpectedStatusCode: expected, Enabled: _existingEnabled, Headers: headers);
        }
        else
        {
            string host = _host.Text.Trim();
            if (host.Length == 0)
            {
                _error.Text = _loc.Get("settings.endpoint.error.host");
                return;
            }

            result = new EndpointConfig(_editingId ?? Guid.NewGuid().ToString("N"), name, EndpointKind.TcpPort, Host: host, Port: (int)_port.Value, Enabled: _existingEnabled);
        }

        Result = result;
        DialogResult = DialogResult.OK;
        Close();
    }

    private int Dp(int px) => Theme.Dp(px, DeviceDpi);

    private int Sp(int step) => Theme.Space(step, DeviceDpi);

    private Label MakeLabel(string text) => new()
    {
        AutoSize = false,
        Text = text,
        ForeColor = _palette.TextMuted,
        BackColor = _palette.Bg,
        Font = Theme.Font(FontRole.Small),
    };

    private TextBox MakeTextBox(string text) => new()
    {
        Text = text,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = _palette.SurfaceAlt,
        ForeColor = _palette.TextPrimary,
        Font = Theme.Font(FontRole.Body),
    };

    private NumericUpDown MakeNumeric(int min, int max, int value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = Math.Clamp(value, min, max),
        BackColor = _palette.SurfaceAlt,
        ForeColor = _palette.TextPrimary,
        Font = Theme.Font(FontRole.Body),
        TextAlign = HorizontalAlignment.Right,
    };

    /// <summary>A combo-box entry carrying both the stored kind and the text shown to the user.</summary>
    private sealed record KindItem(EndpointKind Kind, string Display)
    {
        public override string ToString() => Display;
    }
}
