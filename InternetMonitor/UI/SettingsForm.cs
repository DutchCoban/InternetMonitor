using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Network;
using Microsoft.Win32;

namespace InternetMonitor.UI;

public sealed class SettingsForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "InternetMonitor";

    private readonly AppSettings _settings;

    // General tab
    private readonly Label _languageLabel;
    private readonly ComboBox _languageCombo;
    private readonly CheckBox _autostartCheckBox;
    private readonly Label _autostartErrorLabel;
    private readonly CheckBox _showOutagePopupsCheckBox;
    private readonly Label _pingTargetLabel;
    private readonly TextBox _pingTargetTextBox;
    private readonly Label _pingTargetErrorLabel;

    // Endpoints tab
    private readonly ListView _endpointsListView;
    private readonly Button _addEndpointButton;
    private readonly Button _editEndpointButton;
    private readonly Button _removeEndpointButton;
    private readonly Label _statusDiagramEndpointLabel;
    private readonly ComboBox _statusDiagramEndpointCombo;
    private readonly List<string?> _statusDiagramEndpointIds = [];

    // Diagnostics tab
    private readonly RadioButton _logOffRadio;
    private readonly RadioButton _logBasicRadio;
    private readonly RadioButton _logExtendedRadio;
    private readonly RadioButton _logFullRadio;
    private readonly CheckBox _logSuccessCheckBox;
    private readonly CheckBox _logDetailedCheckBox;
    private readonly NumericUpDown _maxLogFilesNumeric;
    private readonly NumericUpDown _maxLogSizeMbNumeric;
    private readonly NumericUpDown _latencyWarningNumeric;
    private readonly NumericUpDown _latencyErrorNumeric;
    private readonly Label _latencyValidationLabel;
    private readonly NumericUpDown _historyRetentionNumeric;
    private readonly ComboBox _historyRetentionUnitCombo;

    // Uptime Kuma tab
    private readonly Label _kumaUrlLabel;
    private readonly TextBox _kumaUrlTextBox;
    private readonly Label _kumaIntervalLabel;
    private readonly NumericUpDown _kumaIntervalNumeric;
    private readonly Label _kumaUrlErrorLabel;
    private readonly Label _kumaHintLabel;
    private readonly LinkLabel _kumaProjectLink;

    private readonly Button _closeButton;

    /// <summary>Raised after a Kuma URL/interval edit has been saved, so the pusher can be reconfigured live.</summary>
    public event EventHandler? KumaSettingsChanged;

    /// <summary>Raised after the application-endpoints list changes, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? EndpointsChanged;

    /// <summary>Raised after the ping target address has been validated and saved, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? PingTargetChanged;

    /// <summary>Raised after the latency Warning/Error thresholds have been validated and saved, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? LatencyThresholdsChanged;

    /// <summary>Raised after the history retention period has been saved, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? HistoryRetentionChanged;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        Text = LocalizationManager.Instance.Get("settings.title");
        Icon = TrayIconFactory.AppIcon.Value;
        ClientSize = new Size(420, 550);

        var tabs = new TabControl { Dock = DockStyle.Top, Height = 490 };

        // ---------------- General tab ----------------
        var generalTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.general"));
        _languageLabel = new Label { Text = LocalizationManager.Instance.Get("settings.language"), Bounds = new Rectangle(16, 20, 100, 22) };
        _languageCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(140, 18, 160, 24) };
        _languageCombo.Items.Add("Nederlands");
        _languageCombo.Items.Add("English");
        _languageCombo.SelectedIndex = settings.Language == "en" ? 1 : 0;
        _languageCombo.SelectedIndexChanged += OnLanguageChanged;

        _autostartCheckBox = new CheckBox
        {
            Text = LocalizationManager.Instance.Get("settings.autostart"),
            AutoSize = true,
            Bounds = new Rectangle(16, 58, 300, 24),
            Checked = settings.AutoStartWithWindows,
        };
        _autostartCheckBox.CheckedChanged += OnAutostartChanged;
        _autostartErrorLabel = new Label { ForeColor = Color.Firebrick, AutoSize = false, Bounds = new Rectangle(16, 84, 372, 18) };

        _showOutagePopupsCheckBox = new CheckBox
        {
            Text = LocalizationManager.Instance.Get("settings.showOutagePopups"),
            AutoSize = true,
            Bounds = new Rectangle(16, 112, 372, 24),
            Checked = settings.ShowOutagePopups,
        };
        _showOutagePopupsCheckBox.CheckedChanged += OnShowOutagePopupsChanged;

        generalTab.Controls.Add(_languageLabel);
        generalTab.Controls.Add(_languageCombo);
        generalTab.Controls.Add(_autostartCheckBox);
        generalTab.Controls.Add(_autostartErrorLabel);
        generalTab.Controls.Add(_showOutagePopupsCheckBox);

        // ---------------- Ping tab ----------------
        var pingTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.ping"));
        _pingTargetLabel = new Label { Text = LocalizationManager.Instance.Get("settings.pingTarget"), Bounds = new Rectangle(16, 16, 340, 20) };
        _pingTargetTextBox = new TextBox { Text = settings.PingTargetAddress, Bounds = new Rectangle(16, 38, 160, 24) };
        _pingTargetTextBox.Leave += OnPingTargetChanged;
        _pingTargetErrorLabel = new Label { ForeColor = Color.Firebrick, AutoSize = false, Bounds = new Rectangle(16, 66, 372, 18) };
        pingTab.Controls.Add(_pingTargetLabel);
        pingTab.Controls.Add(_pingTargetTextBox);
        pingTab.Controls.Add(_pingTargetErrorLabel);

        // ---------------- Endpoints tab ----------------
        var endpointsTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.endpoints"));
        _endpointsListView = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, Bounds = new Rectangle(16, 16, 372, 220) };
        _endpointsListView.Columns.Add(LocalizationManager.Instance.Get("endpoint.name"), 150);
        _endpointsListView.Columns.Add(LocalizationManager.Instance.Get("endpoint.url"), 160);
        _endpointsListView.Columns.Add(LocalizationManager.Instance.Get("endpoint.enabled"), 50);
        _endpointsListView.DoubleClick += (_, _) => OnEditEndpointClicked(this, EventArgs.Empty);
        RefreshEndpointsList();

        _addEndpointButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.add"), Bounds = new Rectangle(16, 244, 118, 28) };
        _addEndpointButton.Click += OnAddEndpointClicked;
        _editEndpointButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.editButton"), Bounds = new Rectangle(142, 244, 118, 28) };
        _editEndpointButton.Click += OnEditEndpointClicked;
        _removeEndpointButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.remove"), Bounds = new Rectangle(268, 244, 118, 28) };
        _removeEndpointButton.Click += OnRemoveEndpointClicked;

        _statusDiagramEndpointLabel = new Label { Text = LocalizationManager.Instance.Get("settings.endpoint.statusDiagram"), Bounds = new Rectangle(16, 284, 340, 20) };
        _statusDiagramEndpointCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(16, 306, 250, 24) };
        _statusDiagramEndpointCombo.SelectedIndexChanged += OnStatusDiagramEndpointChanged;
        RefreshStatusDiagramEndpointCombo();

        endpointsTab.Controls.Add(_endpointsListView);
        endpointsTab.Controls.Add(_addEndpointButton);
        endpointsTab.Controls.Add(_editEndpointButton);
        endpointsTab.Controls.Add(_removeEndpointButton);
        endpointsTab.Controls.Add(_statusDiagramEndpointLabel);
        endpointsTab.Controls.Add(_statusDiagramEndpointCombo);

        // ---------------- Diagnostics (logging) tab ----------------
        var diagTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.diagnostics"));
        var levelGroup = new GroupBox { Text = LocalizationManager.Instance.Get("settings.logLevel.heading"), Bounds = new Rectangle(16, 12, 372, 140) };
        _logOffRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.off"), Bounds = new Rectangle(12, 24, 340, 22) };
        _logBasicRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.basic"), Bounds = new Rectangle(12, 50, 340, 22) };
        _logExtendedRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.extended"), Bounds = new Rectangle(12, 76, 340, 22) };
        _logFullRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.full"), Bounds = new Rectangle(12, 102, 340, 22) };
        SetLogLevelRadios(settings.DiagnosticLogLevel);
        foreach (var radio in new[] { _logOffRadio, _logBasicRadio, _logExtendedRadio, _logFullRadio })
        {
            radio.CheckedChanged += OnLogLevelChanged;
        }
        levelGroup.Controls.Add(_logOffRadio);
        levelGroup.Controls.Add(_logBasicRadio);
        levelGroup.Controls.Add(_logExtendedRadio);
        levelGroup.Controls.Add(_logFullRadio);

        _logSuccessCheckBox = new CheckBox { Text = LocalizationManager.Instance.Get("settings.logSuccessful"), AutoSize = true, Bounds = new Rectangle(16, 160, 372, 22), Checked = settings.LogSuccessfulChecks };
        _logSuccessCheckBox.CheckedChanged += OnLoggingOptionsChanged;
        _logDetailedCheckBox = new CheckBox { Text = LocalizationManager.Instance.Get("settings.logDetailed"), AutoSize = true, Bounds = new Rectangle(16, 186, 372, 22), Checked = settings.LogDetailedMeasurements };
        _logDetailedCheckBox.CheckedChanged += OnLoggingOptionsChanged;

        var maxFilesLabel = new Label { Text = LocalizationManager.Instance.Get("settings.maxLogFiles"), Bounds = new Rectangle(16, 220, 200, 22) };
        _maxLogFilesNumeric = new NumericUpDown { Minimum = 1, Maximum = 50, Value = settings.MaxDiagnosticLogFiles, Bounds = new Rectangle(220, 218, 80, 24) };
        // Committed on Leave (not ValueChanged) so clicking the spinner buttons repeatedly
        // doesn't write settings.json to disk on every single click.
        _maxLogFilesNumeric.Leave += OnLoggingOptionsChanged;

        var maxSizeLabel = new Label { Text = LocalizationManager.Instance.Get("settings.maxLogSize"), Bounds = new Rectangle(16, 250, 200, 22) };
        _maxLogSizeMbNumeric = new NumericUpDown { Minimum = 1, Maximum = 100, Value = Math.Max(1, settings.MaxDiagnosticLogFileSizeBytes / (1024 * 1024)), Bounds = new Rectangle(220, 248, 80, 24) };
        _maxLogSizeMbNumeric.Leave += OnLoggingOptionsChanged;

        var latencyGroup = new GroupBox { Text = LocalizationManager.Instance.Get("settings.latency.heading"), Bounds = new Rectangle(16, 280, 372, 100) };
        var latencyWarningLabel = new Label { Text = LocalizationManager.Instance.Get("settings.latency.warning"), Bounds = new Rectangle(12, 24, 200, 22) };
        _latencyWarningNumeric = new NumericUpDown { Minimum = 1, Maximum = 60000, Value = settings.LatencyWarningThresholdMs, Bounds = new Rectangle(216, 22, 80, 24) };
        _latencyWarningNumeric.Leave += OnLatencyThresholdsChanged;
        var latencyErrorLabel = new Label { Text = LocalizationManager.Instance.Get("settings.latency.error"), Bounds = new Rectangle(12, 52, 200, 22) };
        _latencyErrorNumeric = new NumericUpDown { Minimum = 1, Maximum = 60000, Value = settings.LatencyErrorThresholdMs, Bounds = new Rectangle(216, 50, 80, 24) };
        _latencyErrorNumeric.Leave += OnLatencyThresholdsChanged;
        _latencyValidationLabel = new Label { ForeColor = Color.Firebrick, AutoSize = false, Bounds = new Rectangle(12, 76, 348, 20) };
        latencyGroup.Controls.Add(latencyWarningLabel);
        latencyGroup.Controls.Add(_latencyWarningNumeric);
        latencyGroup.Controls.Add(latencyErrorLabel);
        latencyGroup.Controls.Add(_latencyErrorNumeric);
        latencyGroup.Controls.Add(_latencyValidationLabel);

        var historyGroup = new GroupBox { Text = LocalizationManager.Instance.Get("settings.historyRetention.heading"), Bounds = new Rectangle(16, 386, 372, 76) };
        var historyRetentionLabel = new Label { Text = LocalizationManager.Instance.Get("settings.historyRetention.label"), Bounds = new Rectangle(12, 28, 130, 22) };
        _historyRetentionNumeric = new NumericUpDown { Bounds = new Rectangle(148, 26, 70, 24) };
        _historyRetentionUnitCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(226, 26, 120, 24) };
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.minutes"));
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.hours"));
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.days"));
        InitializeHistoryRetentionControls(settings.HistoryRetentionMinutes);
        _historyRetentionUnitCombo.SelectedIndexChanged += OnHistoryRetentionUnitChanged;
        _historyRetentionNumeric.Leave += OnHistoryRetentionChanged;
        historyGroup.Controls.Add(historyRetentionLabel);
        historyGroup.Controls.Add(_historyRetentionNumeric);
        historyGroup.Controls.Add(_historyRetentionUnitCombo);

        diagTab.Controls.Add(levelGroup);
        diagTab.Controls.Add(_logSuccessCheckBox);
        diagTab.Controls.Add(_logDetailedCheckBox);
        diagTab.Controls.Add(maxFilesLabel);
        diagTab.Controls.Add(_maxLogFilesNumeric);
        diagTab.Controls.Add(maxSizeLabel);
        diagTab.Controls.Add(_maxLogSizeMbNumeric);
        diagTab.Controls.Add(latencyGroup);
        diagTab.Controls.Add(historyGroup);

        // ---------------- Uptime Kuma tab ----------------
        var kumaTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.kuma"));
        _kumaUrlLabel = new Label { Text = LocalizationManager.Instance.Get("settings.kuma.url"), Bounds = new Rectangle(16, 16, 340, 20) };
        _kumaUrlTextBox = new TextBox { Text = settings.KumaPushUrl, Bounds = new Rectangle(16, 38, 372, 24) };
        _kumaUrlTextBox.Leave += OnKumaSettingsChanged;
        _kumaUrlErrorLabel = new Label { ForeColor = Color.Firebrick, AutoSize = false, Bounds = new Rectangle(16, 64, 372, 18) };

        _kumaIntervalLabel = new Label { Text = LocalizationManager.Instance.Get("settings.kuma.interval"), Bounds = new Rectangle(16, 90, 160, 22) };
        _kumaIntervalNumeric = new NumericUpDown
        {
            Minimum = UptimeKumaPusher.MinimumIntervalSeconds,
            Maximum = 86400,
            Value = Math.Clamp(settings.KumaIntervalSeconds, UptimeKumaPusher.MinimumIntervalSeconds, 86400),
            Bounds = new Rectangle(180, 88, 100, 24),
        };
        _kumaIntervalNumeric.Leave += OnKumaSettingsChanged;

        _kumaHintLabel = new Label { Text = LocalizationManager.Instance.Get("settings.kuma.hint"), ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 8f), Bounds = new Rectangle(16, 118, 372, 18) };

        _kumaProjectLink = new LinkLabel { Text = LocalizationManager.Instance.Get("settings.kuma.projectLink"), AutoSize = true, Location = new Point(16, 142) };
        _kumaProjectLink.LinkClicked += (_, _) => OpenUptimeKumaProjectPage();

        kumaTab.Controls.Add(_kumaUrlLabel);
        kumaTab.Controls.Add(_kumaUrlTextBox);
        kumaTab.Controls.Add(_kumaUrlErrorLabel);
        kumaTab.Controls.Add(_kumaIntervalLabel);
        kumaTab.Controls.Add(_kumaIntervalNumeric);
        kumaTab.Controls.Add(_kumaHintLabel);
        kumaTab.Controls.Add(_kumaProjectLink);

        tabs.TabPages.Add(generalTab);
        tabs.TabPages.Add(endpointsTab);
        tabs.TabPages.Add(pingTab);
        tabs.TabPages.Add(diagTab);
        tabs.TabPages.Add(kumaTab);

        _closeButton = new Button
        {
            Text = LocalizationManager.Instance.Get("settings.close"),
            Bounds = new Rectangle(320, 506, 90, 28),
            DialogResult = DialogResult.OK,
        };
        _closeButton.Click += (_, _) => Close();

        Controls.Add(tabs);
        Controls.Add(_closeButton);
        AcceptButton = _closeButton;
    }

    private void SetLogLevelRadios(DiagnosticLogLevel level)
    {
        _logOffRadio.Checked = level == DiagnosticLogLevel.Off;
        _logBasicRadio.Checked = level == DiagnosticLogLevel.Basic;
        _logExtendedRadio.Checked = level == DiagnosticLogLevel.Extended;
        _logFullRadio.Checked = level == DiagnosticLogLevel.Full;
    }

    private void OnLogLevelChanged(object? sender, EventArgs e)
    {
        if (sender is not RadioButton { Checked: true })
        {
            return;
        }

        _settings.DiagnosticLogLevel = _logOffRadio.Checked ? DiagnosticLogLevel.Off
            : _logBasicRadio.Checked ? DiagnosticLogLevel.Basic
            : _logExtendedRadio.Checked ? DiagnosticLogLevel.Extended
            : DiagnosticLogLevel.Full;
        _settings.Save();
    }

    private void OnLoggingOptionsChanged(object? sender, EventArgs e)
    {
        _settings.LogSuccessfulChecks = _logSuccessCheckBox.Checked;
        _settings.LogDetailedMeasurements = _logDetailedCheckBox.Checked;
        _settings.MaxDiagnosticLogFiles = (int)_maxLogFilesNumeric.Value;
        _settings.MaxDiagnosticLogFileSizeBytes = (long)_maxLogSizeMbNumeric.Value * 1024 * 1024;
        _settings.Save();
    }

    private void RefreshEndpointsList()
    {
        _endpointsListView.Items.Clear();
        foreach (EndpointConfig endpoint in _settings.ApplicationEndpoints)
        {
            var item = new ListViewItem([endpoint.Name, endpoint.Url, endpoint.Enabled ? "✓" : ""]) { Tag = endpoint };
            _endpointsListView.Items.Add(item);
        }
    }

    /// <summary>
    /// Repopulates the "which endpoint represents 'server' in the status diagram" combo.
    /// Preserves the current selection by id if it still exists; falls back to the general
    /// check (and persists that fallback) if the previously chosen endpoint was removed.
    /// </summary>
    private void RefreshStatusDiagramEndpointCombo()
    {
        string? previousSelection = _statusDiagramEndpointIds.Count > 0 && _statusDiagramEndpointCombo.SelectedIndex >= 0
            ? _statusDiagramEndpointIds[_statusDiagramEndpointCombo.SelectedIndex]
            : _settings.StatusDiagramEndpointId;

        _statusDiagramEndpointCombo.SelectedIndexChanged -= OnStatusDiagramEndpointChanged;
        _statusDiagramEndpointCombo.Items.Clear();
        _statusDiagramEndpointIds.Clear();

        _statusDiagramEndpointCombo.Items.Add(LocalizationManager.Instance.Get("settings.endpoint.statusDiagram.general"));
        _statusDiagramEndpointIds.Add(null);

        foreach (EndpointConfig endpoint in _settings.ApplicationEndpoints)
        {
            _statusDiagramEndpointCombo.Items.Add(endpoint.Name);
            _statusDiagramEndpointIds.Add(endpoint.Id);
        }

        int index = _statusDiagramEndpointIds.IndexOf(previousSelection);
        _statusDiagramEndpointCombo.SelectedIndex = index >= 0 ? index : 0;
        if (index < 0 && previousSelection is not null)
        {
            _settings.StatusDiagramEndpointId = null;
            _settings.Save();
        }

        _statusDiagramEndpointCombo.SelectedIndexChanged += OnStatusDiagramEndpointChanged;
    }

    private void OnStatusDiagramEndpointChanged(object? sender, EventArgs e)
    {
        int index = _statusDiagramEndpointCombo.SelectedIndex;
        _settings.StatusDiagramEndpointId = index >= 0 && index < _statusDiagramEndpointIds.Count ? _statusDiagramEndpointIds[index] : null;
        _settings.Save();
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAddEndpointClicked(object? sender, EventArgs e)
    {
        using var editForm = new EndpointEditForm(null);
        if (editForm.ShowDialog(this) == DialogResult.OK)
        {
            _settings.ApplicationEndpoints.Add(editForm.Result);
            _settings.Save();
            RefreshEndpointsList();
            RefreshStatusDiagramEndpointCombo();
            EndpointsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEditEndpointClicked(object? sender, EventArgs e)
    {
        if (_endpointsListView.SelectedItems.Count == 0)
        {
            return;
        }

        var existing = (EndpointConfig)_endpointsListView.SelectedItems[0].Tag!;
        using var editForm = new EndpointEditForm(existing);
        if (editForm.ShowDialog(this) == DialogResult.OK)
        {
            int index = _settings.ApplicationEndpoints.FindIndex(x => x.Id == existing.Id);
            if (index >= 0)
            {
                _settings.ApplicationEndpoints[index] = editForm.Result;
            }

            _settings.Save();
            RefreshEndpointsList();
            RefreshStatusDiagramEndpointCombo();
            EndpointsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRemoveEndpointClicked(object? sender, EventArgs e)
    {
        if (_endpointsListView.SelectedItems.Count == 0)
        {
            return;
        }

        var existing = (EndpointConfig)_endpointsListView.SelectedItems[0].Tag!;
        _settings.ApplicationEndpoints.RemoveAll(x => x.Id == existing.Id);
        _settings.Save();
        RefreshEndpointsList();
        RefreshStatusDiagramEndpointCombo();
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        string code = _languageCombo.SelectedIndex == 1 ? "en" : "nl";
        _settings.Language = code;
        _settings.Save();
        LocalizationManager.Instance.SetLanguage(code);
        ApplyLocalizedText();
    }

    private void OnAutostartChanged(object? sender, EventArgs e)
    {
        _settings.AutoStartWithWindows = _autostartCheckBox.Checked;
        _settings.Save();
        bool applied = ApplyAutostartRegistryValue(_autostartCheckBox.Checked);
        _autostartErrorLabel.Text = applied ? string.Empty : LocalizationManager.Instance.Get("settings.autostart.error.registry");
    }

    private void OnShowOutagePopupsChanged(object? sender, EventArgs e)
    {
        _settings.ShowOutagePopups = _showOutagePopupsCheckBox.Checked;
        _settings.Save();
    }

    private void OnPingTargetChanged(object? sender, EventArgs e)
    {
        string address = _pingTargetTextBox.Text.Trim();
        if (!System.Net.IPAddress.TryParse(address, out _))
        {
            _pingTargetErrorLabel.Text = LocalizationManager.Instance.Get("settings.pingTarget.error.invalid");
            return;
        }

        _pingTargetErrorLabel.Text = string.Empty;
        _settings.PingTargetAddress = address;
        _settings.Save();
        PingTargetChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLatencyThresholdsChanged(object? sender, EventArgs e)
    {
        int warning = (int)_latencyWarningNumeric.Value;
        int error = (int)_latencyErrorNumeric.Value;
        if (warning >= error)
        {
            _latencyValidationLabel.Text = LocalizationManager.Instance.Get("settings.latency.invalidOrder");
            return;
        }

        _latencyValidationLabel.Text = string.Empty;
        _settings.LatencyWarningThresholdMs = warning;
        _settings.LatencyErrorThresholdMs = error;
        _settings.Save();
        LatencyThresholdsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Minutes-per-unit for the History retention value+unit picker, indexed to match
    // _historyRetentionUnitCombo's item order (Minutes, Hours, Days).
    private static readonly int[] HistoryRetentionUnitMinutes = [1, 60, 1440];

    private void InitializeHistoryRetentionControls(int totalMinutes)
    {
        int unitIndex = totalMinutes % 1440 == 0 ? 2 : totalMinutes % 60 == 0 ? 1 : 0;
        _historyRetentionUnitCombo.SelectedIndex = unitIndex;
        ApplyHistoryRetentionUnitRange();
        decimal value = (decimal)totalMinutes / HistoryRetentionUnitMinutes[unitIndex];
        _historyRetentionNumeric.Value = Math.Clamp(value, _historyRetentionNumeric.Minimum, _historyRetentionNumeric.Maximum);
    }

    /// <summary>Keeps the numeric field's own range in sync with the selected unit so the control can never represent a value outside AppSettings' 15-minute-to-32-day clamp - no separate validation label needed.</summary>
    private void ApplyHistoryRetentionUnitRange()
    {
        (decimal min, decimal max) = _historyRetentionUnitCombo.SelectedIndex switch
        {
            1 => (1m, 768m),    // Hours: 1 hour - 32 days
            2 => (1m, 32m),     // Days: 1 - 32 days
            _ => (15m, 46080m), // Minutes: 15 minutes - 32 days
        };
        _historyRetentionNumeric.Minimum = min;
        _historyRetentionNumeric.Maximum = max;
    }

    private void OnHistoryRetentionUnitChanged(object? sender, EventArgs e)
    {
        ApplyHistoryRetentionUnitRange();
        OnHistoryRetentionChanged(sender, e);
    }

    private void OnHistoryRetentionChanged(object? sender, EventArgs e)
    {
        int unitMinutes = HistoryRetentionUnitMinutes[_historyRetentionUnitCombo.SelectedIndex];
        _settings.HistoryRetentionMinutes = (int)(_historyRetentionNumeric.Value * unitMinutes); // setter clamps
        _settings.Save();
        HistoryRetentionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns false if the registry key couldn't be opened for writing, so the caller can surface that instead of silently pretending it worked.</summary>
    private static bool ApplyAutostartRegistryValue(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return false;
        }

        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        return true;
    }

    private void OnKumaSettingsChanged(object? sender, EventArgs e)
    {
        string url = _kumaUrlTextBox.Text.Trim();

        // Empty is valid - it means the feature is off. Otherwise require a well-formed https
        // URL, the same standard EndpointEditForm enforces for monitored endpoints, so a typo or
        // plaintext http:// URL doesn't silently reach UptimeKumaPusher.
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps))
        {
            _kumaUrlErrorLabel.Text = LocalizationManager.Instance.Get("settings.kuma.url.error.invalid");
            return;
        }

        _kumaUrlErrorLabel.Text = string.Empty;
        _settings.KumaPushUrl = url;
        _settings.KumaIntervalSeconds = (int)_kumaIntervalNumeric.Value; // AppSettings setter clamps
        _settings.Save();
        KumaSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void OpenUptimeKumaProjectPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/louislam/uptime-kuma") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No default browser/handler registered - nothing we can do here.
        }
    }

    private void ApplyLocalizedText()
    {
        Text = LocalizationManager.Instance.Get("settings.title");
        _languageLabel.Text = LocalizationManager.Instance.Get("settings.language");
        _autostartCheckBox.Text = LocalizationManager.Instance.Get("settings.autostart");
        _showOutagePopupsCheckBox.Text = LocalizationManager.Instance.Get("settings.showOutagePopups");
        _pingTargetLabel.Text = LocalizationManager.Instance.Get("settings.pingTarget");
        _kumaUrlLabel.Text = LocalizationManager.Instance.Get("settings.kuma.url");
        _kumaIntervalLabel.Text = LocalizationManager.Instance.Get("settings.kuma.interval");
        _kumaHintLabel.Text = LocalizationManager.Instance.Get("settings.kuma.hint");
        _kumaProjectLink.Text = LocalizationManager.Instance.Get("settings.kuma.projectLink");
        _closeButton.Text = LocalizationManager.Instance.Get("settings.close");
        // Tab headers and the endpoints/diagnostics tab labels are set once at construction -
        // a full re-localization of every tab would need the tabs rebuilt; language switching
        // while Settings happens to be open is a rare enough path that this is an acceptable gap.
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // This form is recreated fresh every time it's reopened from the tray menu, so the
            // custom Font assigned here (distinct from the default control Font, which Control
            // disposes on its own) must be disposed explicitly or it leaks a GDI handle per open.
            _kumaHintLabel.Font?.Dispose();
        }

        base.Dispose(disposing);
    }
}
