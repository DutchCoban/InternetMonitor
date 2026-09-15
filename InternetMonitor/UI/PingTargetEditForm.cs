using InternetMonitor.Configuration;
using InternetMonitor.Localization;

namespace InternetMonitor.UI;

/// <summary>Add/edit dialog for a single named continuous-ping target, mirroring <see cref="EndpointEditForm"/>'s shape but for the smaller field set a ping target needs.</summary>
public sealed class PingTargetEditForm : Form
{
    private readonly TextBox _nameTextBox;
    private readonly TextBox _addressTextBox;
    private readonly CheckBox _enabledCheckBox;
    private readonly Label _errorLabel;

    public PingTargetConfig Result { get; }

    public PingTargetEditForm(PingTargetConfig? existing)
    {
        Result = existing is null
            ? new PingTargetConfig()
            : new PingTargetConfig
            {
                Id = existing.Id,
                Name = existing.Name,
                Address = existing.Address,
                Enabled = existing.Enabled,
            };

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = LocalizationManager.Instance.Get("pingTarget.edit.title");
        Icon = TrayIconFactory.AppIcon.Value;
        ClientSize = new Size(340, 220);

        var nameLabel = new Label { Text = LocalizationManager.Instance.Get("pingTarget.edit.name"), Bounds = new Rectangle(16, 16, 308, 20) };
        _nameTextBox = new TextBox { Text = Result.Name, Bounds = new Rectangle(16, 38, 308, 24) };

        var addressLabel = new Label { Text = LocalizationManager.Instance.Get("pingTarget.edit.address"), Bounds = new Rectangle(16, 70, 308, 20) };
        _addressTextBox = new TextBox { Text = Result.Address, Bounds = new Rectangle(16, 92, 308, 24) };

        _enabledCheckBox = new CheckBox { Text = LocalizationManager.Instance.Get("pingTarget.edit.enabled"), Checked = Result.Enabled, AutoSize = true, Bounds = new Rectangle(16, 124, 300, 24) };

        _errorLabel = new Label { ForeColor = Color.Firebrick, AutoSize = false, Bounds = new Rectangle(16, 152, 308, 18) };

        var saveButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.edit.save"), Bounds = new Rectangle(164, 180, 80, 28) };
        saveButton.Click += OnSaveClicked;
        var cancelButton = new Button { Text = LocalizationManager.Instance.Get("settings.close"), Bounds = new Rectangle(248, 180, 76, 28), DialogResult = DialogResult.Cancel };

        Controls.Add(nameLabel);
        Controls.Add(_nameTextBox);
        Controls.Add(addressLabel);
        Controls.Add(_addressTextBox);
        Controls.Add(_enabledCheckBox);
        Controls.Add(_errorLabel);
        Controls.Add(saveButton);
        Controls.Add(cancelButton);
        CancelButton = cancelButton;
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        string name = _nameTextBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _errorLabel.Text = LocalizationManager.Instance.Get("pingTarget.edit.error.name");
            return;
        }

        string address = _addressTextBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(address, out _))
        {
            _errorLabel.Text = LocalizationManager.Instance.Get("settings.pingTarget.error.invalid");
            return;
        }

        Result.Name = name;
        Result.Address = address;
        Result.Enabled = _enabledCheckBox.Checked;

        DialogResult = DialogResult.OK;
        Close();
    }
}
