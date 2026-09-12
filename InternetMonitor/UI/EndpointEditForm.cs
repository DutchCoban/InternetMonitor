using InternetMonitor.Configuration;
using InternetMonitor.Localization;

namespace InternetMonitor.UI;

/// <summary>Add/edit dialog for a single application endpoint (HTTPS or generic TCP/UDP port), with validation on save.</summary>
public sealed class EndpointEditForm : Form
{
    private static readonly (string Label, int Port)[] CommonPorts =
    [
        ("HTTP (80)", 80),
        ("HTTPS (443)", 443),
        ("SSH (22)", 22),
        ("SMTP (25)", 25),
        ("SMTPS (465)", 465),
        ("DNS (53)", 53),
        ("RDP (3389)", 3389),
        ("MQTT (1883)", 1883),
        ("MQTTS (8883)", 8883),
        ("SIP (5060)", 5060),
    ];

    private readonly TextBox _nameTextBox;
    private readonly ComboBox _typeCombo;

    private readonly Label _urlLabel;
    private readonly TextBox _urlTextBox;

    private readonly Label _hostLabel;
    private readonly TextBox _hostTextBox;
    private readonly Label _portLabel;
    private readonly ComboBox _commonPortCombo;
    private readonly NumericUpDown _portNumeric;
    private readonly Label _transportLabel;
    private readonly ComboBox _transportCombo;

    private readonly NumericUpDown _timeoutNumeric;
    private readonly Label _timeoutHintLabel;
    private readonly CheckBox _enabledCheckBox;
    private readonly Label _errorLabel;

    public EndpointConfig Result { get; }

    public EndpointEditForm(EndpointConfig? existing)
    {
        Result = existing is null
            ? new EndpointConfig()
            : new EndpointConfig
            {
                Id = existing.Id,
                Name = existing.Name,
                Type = existing.Type,
                Url = existing.Url,
                Host = existing.Host,
                Port = existing.Port,
                PortProtocol = existing.PortProtocol,
                TimeoutMs = existing.TimeoutMs,
                Enabled = existing.Enabled,
            };

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = LocalizationManager.Instance.Get("endpoint.edit.title");
        ClientSize = new Size(380, 432);

        var nameLabel = new Label { Text = LocalizationManager.Instance.Get("endpoint.edit.name"), Bounds = new Rectangle(16, 16, 340, 20) };
        _nameTextBox = new TextBox { Text = Result.Name, Bounds = new Rectangle(16, 38, 348, 24) };

        var typeLabel = new Label { Text = LocalizationManager.Instance.Get("endpoint.edit.type"), Bounds = new Rectangle(16, 70, 340, 20) };
        _typeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(16, 92, 200, 24) };
        _typeCombo.Items.Add(LocalizationManager.Instance.Get("endpoint.type.https"));
        _typeCombo.Items.Add(LocalizationManager.Instance.Get("endpoint.type.port"));
        _typeCombo.SelectedIndex = Result.Type == EndpointType.Port ? 1 : 0;
        _typeCombo.SelectedIndexChanged += (_, _) => ApplyTypeVisibility();

        _urlLabel = new Label { Text = LocalizationManager.Instance.Get("endpoint.edit.url"), Bounds = new Rectangle(16, 124, 340, 20) };
        _urlTextBox = new TextBox { Text = Result.Url, Bounds = new Rectangle(16, 146, 348, 24) };

        _hostLabel = new Label { Text = LocalizationManager.Instance.Get("endpoint.edit.host"), Bounds = new Rectangle(16, 124, 340, 20) };
        _hostTextBox = new TextBox { Text = Result.Host, Bounds = new Rectangle(16, 146, 348, 24) };

        _portLabel = new Label { Text = LocalizationManager.Instance.Get("endpoint.edit.port"), Bounds = new Rectangle(16, 174, 340, 20) };
        _portNumeric = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = Result.Port, Bounds = new Rectangle(204, 196, 80, 24) };
        _commonPortCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(16, 196, 180, 24) };
        foreach ((string label, _) in CommonPorts)
        {
            _commonPortCombo.Items.Add(label);
        }
        _commonPortCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_commonPortCombo.SelectedIndex >= 0)
            {
                _portNumeric.Value = CommonPorts[_commonPortCombo.SelectedIndex].Port;
            }
        };

        _transportLabel = new Label { Text = LocalizationManager.Instance.Get("endpoint.edit.transport"), Bounds = new Rectangle(16, 228, 340, 20) };
        _transportCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(16, 250, 180, 24) };
        _transportCombo.Items.Add(LocalizationManager.Instance.Get("endpoint.transport.tcp"));
        _transportCombo.Items.Add(LocalizationManager.Instance.Get("endpoint.transport.udp"));
        _transportCombo.Items.Add(LocalizationManager.Instance.Get("endpoint.transport.both"));
        _transportCombo.SelectedIndex = (int)Result.PortProtocol;

        var timeoutLabel = new Label { Text = LocalizationManager.Instance.Get("endpoint.edit.timeout"), Bounds = new Rectangle(16, 284, 200, 22) };
        _timeoutNumeric = new NumericUpDown { Minimum = 500, Maximum = 60000, Increment = 500, Value = Result.TimeoutMs, Bounds = new Rectangle(16, 306, 100, 24) };
        _timeoutHintLabel = new Label
        {
            Text = LocalizationManager.Instance.Get("endpoint.edit.timeoutHint"),
            ForeColor = Color.Gray,
            Font = new Font(Font.FontFamily, 8f),
            AutoSize = false,
            Bounds = new Rectangle(124, 310, 240, 18),
        };

        _enabledCheckBox = new CheckBox { Text = LocalizationManager.Instance.Get("endpoint.edit.enabled"), Checked = Result.Enabled, AutoSize = true, Bounds = new Rectangle(16, 336, 300, 24) };

        _errorLabel = new Label { ForeColor = Color.Firebrick, AutoSize = false, Bounds = new Rectangle(16, 362, 348, 18) };

        var saveButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.edit.save"), Bounds = new Rectangle(204, 388, 80, 28) };
        saveButton.Click += OnSaveClicked;
        var cancelButton = new Button { Text = LocalizationManager.Instance.Get("settings.close"), Bounds = new Rectangle(288, 388, 76, 28), DialogResult = DialogResult.Cancel };

        Controls.Add(nameLabel);
        Controls.Add(_nameTextBox);
        Controls.Add(typeLabel);
        Controls.Add(_typeCombo);
        Controls.Add(_urlLabel);
        Controls.Add(_urlTextBox);
        Controls.Add(_hostLabel);
        Controls.Add(_hostTextBox);
        Controls.Add(_portLabel);
        Controls.Add(_commonPortCombo);
        Controls.Add(_portNumeric);
        Controls.Add(_transportLabel);
        Controls.Add(_transportCombo);
        Controls.Add(timeoutLabel);
        Controls.Add(_timeoutNumeric);
        Controls.Add(_timeoutHintLabel);
        Controls.Add(_enabledCheckBox);
        Controls.Add(_errorLabel);
        Controls.Add(saveButton);
        Controls.Add(cancelButton);
        CancelButton = cancelButton;

        ApplyTypeVisibility();
    }

    private void ApplyTypeVisibility()
    {
        bool isPort = _typeCombo.SelectedIndex == 1;

        _urlLabel.Visible = !isPort;
        _urlTextBox.Visible = !isPort;

        _hostLabel.Visible = isPort;
        _hostTextBox.Visible = isPort;
        _portLabel.Visible = isPort;
        _commonPortCombo.Visible = isPort;
        _portNumeric.Visible = isPort;
        _transportLabel.Visible = isPort;
        _transportCombo.Visible = isPort;
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        string name = _nameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _errorLabel.Text = LocalizationManager.Instance.Get("endpoint.edit.error.name");
            return;
        }

        bool isPort = _typeCombo.SelectedIndex == 1;
        if (isPort)
        {
            string host = _hostTextBox.Text.Trim();
            if (string.IsNullOrEmpty(host))
            {
                _errorLabel.Text = LocalizationManager.Instance.Get("endpoint.edit.error.host");
                return;
            }

            int port = (int)_portNumeric.Value;
            if (port is < 1 or > 65535)
            {
                _errorLabel.Text = LocalizationManager.Instance.Get("endpoint.edit.error.port");
                return;
            }

            Result.Type = EndpointType.Port;
            Result.Host = host;
            Result.Port = port;
            Result.PortProtocol = (PortProtocol)_transportCombo.SelectedIndex;
            Result.Url = string.Empty;
        }
        else
        {
            string url = _urlTextBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                _errorLabel.Text = LocalizationManager.Instance.Get("endpoint.edit.error.url");
                return;
            }

            Result.Type = EndpointType.Https;
            Result.Url = url;
            Result.Host = string.Empty;
        }

        Result.Name = name;
        Result.TimeoutMs = (int)_timeoutNumeric.Value;
        Result.Enabled = _enabledCheckBox.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }
}
