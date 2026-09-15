using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Network;
using InternetMonitor.Network.Diagnosis;
using Microsoft.Win32;

namespace InternetMonitor.UI;

public sealed class SettingsForm : Form
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "InternetMonitor";

    private readonly AppSettings _settings;

    // Tab pages - kept as fields (not just locals) so ApplyLocalizedText can re-set their
    // headers on a live language switch.
    private readonly TabPage _generalTab;
    private readonly TabPage _endpointsTab;
    private readonly TabPage _pingTab;
    private readonly TabPage _diagTab;
    private readonly TabPage _slaTab;
    private readonly TabPage _speedTestTab;
    private readonly TabPage _kumaTab;

    // General tab
    private readonly Label _languageLabel;
    private readonly ComboBox _languageCombo;
    private readonly CheckBox _autostartCheckBox;
    private readonly Label _autostartErrorLabel;
    private readonly CheckBox _showOutagePopupsCheckBox;
    private readonly CheckBox _quietHoursCheckBox;
    private readonly DateTimePicker _quietHoursStartPicker;
    private readonly Label _quietHoursToLabel;
    private readonly DateTimePicker _quietHoursEndPicker;
    private readonly CheckBox _checkForUpdatesCheckBox;
    private readonly CheckBox _timeSyncCheckEnabledCheckBox;
    private readonly ListView _pingTargetsListView;
    private readonly Button _addPingTargetButton;
    private readonly Button _editPingTargetButton;
    private readonly Button _removePingTargetButton;
    private readonly Label _pingTargetsHintLabel;

    // Speed Test tab
    private readonly CheckBox _speedTestAutoRunCheckBox;
    private readonly Label _speedTestIntervalLabel;
    private readonly ComboBox _speedTestIntervalCombo;
    private readonly Label _speedTestHintLabel;

    // Endpoints tab
    private readonly ListView _endpointsListView;
    private readonly Button _addEndpointButton;
    private readonly Button _editEndpointButton;
    private readonly Button _removeEndpointButton;
    private readonly Label _statusDiagramEndpointLabel;
    private readonly ComboBox _statusDiagramEndpointCombo;
    private readonly List<string?> _statusDiagramEndpointIds = [];

    // Diagnostics tab
    private readonly GroupBox _levelGroup;
    private readonly RadioButton _logOffRadio;
    private readonly RadioButton _logBasicRadio;
    private readonly RadioButton _logExtendedRadio;
    private readonly RadioButton _logFullRadio;
    private readonly CheckBox _logSuccessCheckBox;
    private readonly CheckBox _logDetailedCheckBox;
    private readonly Label _maxFilesLabel;
    private readonly NumericUpDown _maxLogFilesNumeric;
    private readonly Label _maxSizeLabel;
    private readonly NumericUpDown _maxLogSizeMbNumeric;
    private readonly GroupBox _latencyGroup;
    private readonly Label _latencyWarningLabel;
    private readonly NumericUpDown _latencyWarningNumeric;
    private readonly Label _latencyErrorLabel;
    private readonly NumericUpDown _latencyErrorNumeric;
    private readonly Label _latencyValidationLabel;
    private readonly GroupBox _historyGroup;
    private readonly Label _historyRetentionLabel;
    private readonly NumericUpDown _historyRetentionNumeric;
    private readonly ComboBox _historyRetentionUnitCombo;

    // SLA Report tab
    private readonly Label _slaHintLabel;
    private readonly CheckedListBox _slaChecklist;

    // Uptime Kuma tab
    private readonly Label _kumaUrlLabel;
    private readonly TextBox _kumaUrlTextBox;
    private readonly Label _kumaIntervalLabel;
    private readonly NumericUpDown _kumaIntervalNumeric;
    private readonly Label _kumaUrlErrorLabel;
    private readonly Label _kumaHintLabel;
    private readonly LinkLabel _kumaProjectLink;

    private readonly Button _exportButton;
    private readonly Button _importButton;
    private readonly Button _closeButton;

    /// <summary>Raised after a Kuma URL/interval edit has been saved, so the pusher can be reconfigured live.</summary>
    public event EventHandler? KumaSettingsChanged;

    /// <summary>Raised after the application-endpoints list changes, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? EndpointsChanged;

    /// <summary>Raised after the ping targets list changes, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? PingTargetsChanged;

    /// <summary>Raised after the latency Warning/Error thresholds have been validated and saved, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? LatencyThresholdsChanged;

    /// <summary>Raised after the history retention period has been saved, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? HistoryRetentionChanged;

    /// <summary>Raised after the "automatically check for updates" toggle has been saved, so the update checker can be started/stopped live.</summary>
    public event EventHandler? CheckForUpdatesChanged;

    /// <summary>Raised after the time-sync check on/off toggle has been saved, so the coordinator can be reconfigured live.</summary>
    public event EventHandler? TimeSyncCheckEnabledChanged;

    /// <summary>Raised after any Speed Test setting changes (enabled, auto-run, or interval), so the coordinator's automatic-run loop can be reconfigured live.</summary>
    public event EventHandler? SpeedTestSettingsChanged;

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
        ClientSize = new Size(440, 550);

        var tabs = new TabControl { Dock = DockStyle.Top, Height = 490 };

        // ---------------- General tab ----------------
        _generalTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.general"));
        _languageLabel = new Label { Text = LocalizationManager.Instance.Get("settings.language"), Bounds = new Rectangle(16, 20, 100, 22) };
        _languageCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(140, 18, 160, 24) };
        foreach ((string _, string displayName) in LocalizationManager.SupportedLanguages)
        {
            _languageCombo.Items.Add(displayName);
        }

        int languageIndex = LocalizationManager.SupportedLanguages.ToList().FindIndex(l => l.Code == settings.Language);
        _languageCombo.SelectedIndex = Math.Max(0, languageIndex);
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

        _quietHoursCheckBox = new CheckBox
        {
            Text = LocalizationManager.Instance.Get("settings.quietHours"),
            AutoSize = true,
            Bounds = new Rectangle(16, 142, 372, 24),
            Checked = settings.QuietHoursEnabled,
        };
        _quietHoursCheckBox.CheckedChanged += OnQuietHoursChanged;
        _quietHoursStartPicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            ShowUpDown = true,
            Value = DateTime.Today.Add(settings.QuietHoursStart.ToTimeSpan()),
            Bounds = new Rectangle(32, 170, 90, 24),
        };
        _quietHoursStartPicker.ValueChanged += OnQuietHoursChanged;
        _quietHoursToLabel = new Label { Text = LocalizationManager.Instance.Get("settings.quietHours.to"), AutoSize = true, Location = new Point(128, 174) };
        _quietHoursEndPicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Time,
            ShowUpDown = true,
            Value = DateTime.Today.Add(settings.QuietHoursEnd.ToTimeSpan()),
            Bounds = new Rectangle(160, 170, 90, 24),
        };
        _quietHoursEndPicker.ValueChanged += OnQuietHoursChanged;

        _checkForUpdatesCheckBox = new CheckBox
        {
            Text = LocalizationManager.Instance.Get("settings.checkForUpdates"),
            AutoSize = true,
            Bounds = new Rectangle(16, 206, 372, 24),
            Checked = settings.CheckForUpdates,
        };
        _checkForUpdatesCheckBox.CheckedChanged += OnCheckForUpdatesChanged;

        _timeSyncCheckEnabledCheckBox = new CheckBox
        {
            Text = LocalizationManager.Instance.Get("settings.timeSyncCheckEnabled"),
            AutoSize = true,
            Bounds = new Rectangle(16, 236, 372, 24),
            Checked = settings.TimeSyncCheckEnabled,
        };
        _timeSyncCheckEnabledCheckBox.CheckedChanged += OnTimeSyncCheckEnabledChanged;

        _generalTab.Controls.Add(_languageLabel);
        _generalTab.Controls.Add(_languageCombo);
        _generalTab.Controls.Add(_autostartCheckBox);
        _generalTab.Controls.Add(_autostartErrorLabel);
        _generalTab.Controls.Add(_showOutagePopupsCheckBox);
        _generalTab.Controls.Add(_quietHoursCheckBox);
        _generalTab.Controls.Add(_quietHoursStartPicker);
        _generalTab.Controls.Add(_quietHoursToLabel);
        _generalTab.Controls.Add(_quietHoursEndPicker);
        _generalTab.Controls.Add(_checkForUpdatesCheckBox);
        _generalTab.Controls.Add(_timeSyncCheckEnabledCheckBox);

        // ---------------- Speed Test tab ----------------
        _speedTestTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.speedTest"));
        _speedTestAutoRunCheckBox = new CheckBox
        {
            Text = LocalizationManager.Instance.Get("settings.speedTest.autoRun"),
            AutoSize = true,
            Bounds = new Rectangle(16, 16, 372, 24),
            Checked = settings.SpeedTestAutoRunEnabled,
        };
        _speedTestAutoRunCheckBox.CheckedChanged += OnSpeedTestAutoRunChanged;

        _speedTestIntervalLabel = new Label { Text = LocalizationManager.Instance.Get("settings.speedTest.interval"), Bounds = new Rectangle(32, 48, 150, 22) };
        _speedTestIntervalCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Bounds = new Rectangle(32, 70, 120, 24),
            Enabled = settings.SpeedTestAutoRunEnabled,
        };
        foreach (int hours in AppSettings.AllowedSpeedTestIntervalHours)
        {
            _speedTestIntervalCombo.Items.Add(LocalizationManager.Instance.Format("settings.speedTest.interval.format", hours));
        }
        int intervalIndex = Array.IndexOf(AppSettings.AllowedSpeedTestIntervalHours, settings.SpeedTestIntervalHours);
        _speedTestIntervalCombo.SelectedIndex = Math.Max(0, intervalIndex);
        _speedTestIntervalCombo.SelectedIndexChanged += OnSpeedTestIntervalChanged;

        _speedTestHintLabel = new Label
        {
            Text = LocalizationManager.Instance.Get("settings.speedTest.hint"),
            AutoSize = false,
            ForeColor = Color.Gray,
            Bounds = new Rectangle(16, 106, 372, 36),
        };

        _speedTestTab.Controls.Add(_speedTestAutoRunCheckBox);
        _speedTestTab.Controls.Add(_speedTestIntervalLabel);
        _speedTestTab.Controls.Add(_speedTestIntervalCombo);
        _speedTestTab.Controls.Add(_speedTestHintLabel);

        // ---------------- Ping tab ----------------
        _pingTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.ping"));
        _pingTargetsListView = new ListView { View = View.Details, FullRowSelect = true, MultiSelect = false, Bounds = new Rectangle(16, 16, 372, 220) };
        _pingTargetsListView.Columns.Add(LocalizationManager.Instance.Get("pingTarget.name"), 150);
        _pingTargetsListView.Columns.Add(LocalizationManager.Instance.Get("pingTarget.address"), 160);
        _pingTargetsListView.Columns.Add(LocalizationManager.Instance.Get("endpoint.enabled"), 50);
        _pingTargetsListView.DoubleClick += (_, _) => OnEditPingTargetClicked(this, EventArgs.Empty);
        RefreshPingTargetsList();

        _addPingTargetButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.add"), Bounds = new Rectangle(16, 244, 118, 28) };
        _addPingTargetButton.Click += OnAddPingTargetClicked;
        _editPingTargetButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.editButton"), Bounds = new Rectangle(142, 244, 118, 28) };
        _editPingTargetButton.Click += OnEditPingTargetClicked;
        _removePingTargetButton = new Button { Text = LocalizationManager.Instance.Get("endpoint.remove"), Bounds = new Rectangle(268, 244, 118, 28) };
        _removePingTargetButton.Click += OnRemovePingTargetClicked;

        _pingTargetsHintLabel = new Label
        {
            Text = LocalizationManager.Instance.Get("pingTarget.hint"),
            AutoSize = false,
            ForeColor = Color.Gray,
            Bounds = new Rectangle(16, 280, 372, 36),
        };

        _pingTab.Controls.Add(_pingTargetsListView);
        _pingTab.Controls.Add(_addPingTargetButton);
        _pingTab.Controls.Add(_editPingTargetButton);
        _pingTab.Controls.Add(_removePingTargetButton);
        _pingTab.Controls.Add(_pingTargetsHintLabel);

        // ---------------- Endpoints tab ----------------
        _endpointsTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.endpoints"));
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

        _endpointsTab.Controls.Add(_endpointsListView);
        _endpointsTab.Controls.Add(_addEndpointButton);
        _endpointsTab.Controls.Add(_editEndpointButton);
        _endpointsTab.Controls.Add(_removeEndpointButton);
        _endpointsTab.Controls.Add(_statusDiagramEndpointLabel);
        _endpointsTab.Controls.Add(_statusDiagramEndpointCombo);

        // ---------------- Diagnostics (logging) tab ----------------
        _diagTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.diagnostics"));
        _levelGroup = new GroupBox { Text = LocalizationManager.Instance.Get("settings.logLevel.heading"), Bounds = new Rectangle(16, 12, 372, 140) };
        _logOffRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.off"), Bounds = new Rectangle(12, 24, 340, 22) };
        _logBasicRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.basic"), Bounds = new Rectangle(12, 50, 340, 22) };
        _logExtendedRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.extended"), Bounds = new Rectangle(12, 76, 340, 22) };
        _logFullRadio = new RadioButton { Text = LocalizationManager.Instance.Get("settings.logLevel.full"), Bounds = new Rectangle(12, 102, 340, 22) };
        SetLogLevelRadios(settings.DiagnosticLogLevel);
        foreach (var radio in new[] { _logOffRadio, _logBasicRadio, _logExtendedRadio, _logFullRadio })
        {
            radio.CheckedChanged += OnLogLevelChanged;
        }
        _levelGroup.Controls.Add(_logOffRadio);
        _levelGroup.Controls.Add(_logBasicRadio);
        _levelGroup.Controls.Add(_logExtendedRadio);
        _levelGroup.Controls.Add(_logFullRadio);

        _logSuccessCheckBox = new CheckBox { Text = LocalizationManager.Instance.Get("settings.logSuccessful"), AutoSize = true, Bounds = new Rectangle(16, 160, 372, 22), Checked = settings.LogSuccessfulChecks };
        _logSuccessCheckBox.CheckedChanged += OnLoggingOptionsChanged;
        _logDetailedCheckBox = new CheckBox { Text = LocalizationManager.Instance.Get("settings.logDetailed"), AutoSize = true, Bounds = new Rectangle(16, 186, 372, 22), Checked = settings.LogDetailedMeasurements };
        _logDetailedCheckBox.CheckedChanged += OnLoggingOptionsChanged;

        _maxFilesLabel = new Label { Text = LocalizationManager.Instance.Get("settings.maxLogFiles"), Bounds = new Rectangle(16, 220, 200, 22) };
        _maxLogFilesNumeric = new NumericUpDown { Minimum = 1, Maximum = 50, Value = settings.MaxDiagnosticLogFiles, Bounds = new Rectangle(220, 218, 80, 24) };
        // Committed on Leave (not ValueChanged) so clicking the spinner buttons repeatedly
        // doesn't write settings.json to disk on every single click.
        _maxLogFilesNumeric.Leave += OnLoggingOptionsChanged;

        _maxSizeLabel = new Label { Text = LocalizationManager.Instance.Get("settings.maxLogSize"), Bounds = new Rectangle(16, 250, 200, 22) };
        _maxLogSizeMbNumeric = new NumericUpDown { Minimum = 1, Maximum = 100, Value = Math.Max(1, settings.MaxDiagnosticLogFileSizeBytes / (1024 * 1024)), Bounds = new Rectangle(220, 248, 80, 24) };
        _maxLogSizeMbNumeric.Leave += OnLoggingOptionsChanged;

        _latencyGroup = new GroupBox { Text = LocalizationManager.Instance.Get("settings.latency.heading"), Bounds = new Rectangle(16, 280, 372, 100) };
        _latencyWarningLabel = new Label { Text = LocalizationManager.Instance.Get("settings.latency.warning"), Bounds = new Rectangle(12, 24, 200, 22) };
        _latencyWarningNumeric = new NumericUpDown { Minimum = 1, Maximum = 60000, Value = settings.LatencyWarningThresholdMs, Bounds = new Rectangle(216, 22, 80, 24) };
        _latencyWarningNumeric.Leave += OnLatencyThresholdsChanged;
        _latencyErrorLabel = new Label { Text = LocalizationManager.Instance.Get("settings.latency.error"), Bounds = new Rectangle(12, 52, 200, 22) };
        _latencyErrorNumeric = new NumericUpDown { Minimum = 1, Maximum = 60000, Value = settings.LatencyErrorThresholdMs, Bounds = new Rectangle(216, 50, 80, 24) };
        _latencyErrorNumeric.Leave += OnLatencyThresholdsChanged;
        _latencyValidationLabel = new Label { ForeColor = Color.Firebrick, AutoSize = false, Bounds = new Rectangle(12, 76, 348, 20) };
        _latencyGroup.Controls.Add(_latencyWarningLabel);
        _latencyGroup.Controls.Add(_latencyWarningNumeric);
        _latencyGroup.Controls.Add(_latencyErrorLabel);
        _latencyGroup.Controls.Add(_latencyErrorNumeric);
        _latencyGroup.Controls.Add(_latencyValidationLabel);

        _historyGroup = new GroupBox { Text = LocalizationManager.Instance.Get("settings.historyRetention.heading"), Bounds = new Rectangle(16, 386, 372, 76) };
        _historyRetentionLabel = new Label { Text = LocalizationManager.Instance.Get("settings.historyRetention.label"), Bounds = new Rectangle(12, 28, 130, 22) };
        _historyRetentionNumeric = new NumericUpDown { Bounds = new Rectangle(148, 26, 70, 24) };
        _historyRetentionUnitCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(226, 26, 120, 24) };
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.minutes"));
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.hours"));
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.days"));
        InitializeHistoryRetentionControls(settings.HistoryRetentionMinutes);
        _historyRetentionUnitCombo.SelectedIndexChanged += OnHistoryRetentionUnitChanged;
        _historyRetentionNumeric.Leave += OnHistoryRetentionChanged;
        _historyGroup.Controls.Add(_historyRetentionLabel);
        _historyGroup.Controls.Add(_historyRetentionNumeric);
        _historyGroup.Controls.Add(_historyRetentionUnitCombo);

        _diagTab.Controls.Add(_levelGroup);
        _diagTab.Controls.Add(_logSuccessCheckBox);
        _diagTab.Controls.Add(_logDetailedCheckBox);
        _diagTab.Controls.Add(_maxFilesLabel);
        _diagTab.Controls.Add(_maxLogFilesNumeric);
        _diagTab.Controls.Add(_maxSizeLabel);
        _diagTab.Controls.Add(_maxLogSizeMbNumeric);
        _diagTab.Controls.Add(_latencyGroup);
        _diagTab.Controls.Add(_historyGroup);

        // ---------------- SLA Report tab ----------------
        _slaTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.sla"));
        _slaHintLabel = new Label
        {
            Text = LocalizationManager.Instance.Get("settings.sla.hint"),
            AutoSize = false,
            ForeColor = Color.Gray,
            Bounds = new Rectangle(16, 12, 372, 36),
        };
        _slaChecklist = new CheckedListBox { Bounds = new Rectangle(16, 52, 372, 400), CheckOnClick = true };
        PopulateSlaChecklist();
        _slaChecklist.ItemCheck += OnSlaItemCheck;
        _slaTab.Controls.Add(_slaHintLabel);
        _slaTab.Controls.Add(_slaChecklist);

        // ---------------- Uptime Kuma tab ----------------
        _kumaTab = new TabPage(LocalizationManager.Instance.Get("settings.tab.kuma"));
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

        _kumaTab.Controls.Add(_kumaUrlLabel);
        _kumaTab.Controls.Add(_kumaUrlTextBox);
        _kumaTab.Controls.Add(_kumaUrlErrorLabel);
        _kumaTab.Controls.Add(_kumaIntervalLabel);
        _kumaTab.Controls.Add(_kumaIntervalNumeric);
        _kumaTab.Controls.Add(_kumaHintLabel);
        _kumaTab.Controls.Add(_kumaProjectLink);

        tabs.TabPages.Add(_generalTab);
        tabs.TabPages.Add(_endpointsTab);
        tabs.TabPages.Add(_pingTab);
        tabs.TabPages.Add(_diagTab);
        tabs.TabPages.Add(_slaTab);
        tabs.TabPages.Add(_speedTestTab);
        tabs.TabPages.Add(_kumaTab);

        _exportButton = new Button { Text = LocalizationManager.Instance.Get("settings.export"), Bounds = new Rectangle(16, 506, 150, 28) };
        _exportButton.Click += OnExportClicked;
        _importButton = new Button { Text = LocalizationManager.Instance.Get("settings.import"), Bounds = new Rectangle(182, 506, 150, 28) };
        _importButton.Click += OnImportClicked;

        _closeButton = new Button
        {
            Text = LocalizationManager.Instance.Get("settings.close"),
            Bounds = new Rectangle(348, 506, 80, 28),
            DialogResult = DialogResult.OK,
        };
        _closeButton.Click += (_, _) => Close();

        Controls.Add(tabs);
        Controls.Add(_exportButton);
        Controls.Add(_importButton);
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

    private void RefreshPingTargetsList()
    {
        _pingTargetsListView.Items.Clear();
        foreach (PingTargetConfig target in _settings.PingTargets)
        {
            var item = new ListViewItem([target.Name, target.Address, target.Enabled ? "✓" : ""]) { Tag = target };
            _pingTargetsListView.Items.Add(item);
        }
    }

    private void OnAddPingTargetClicked(object? sender, EventArgs e)
    {
        using var editForm = new PingTargetEditForm(null);
        if (editForm.ShowDialog(this) == DialogResult.OK)
        {
            _settings.PingTargets.Add(editForm.Result);
            _settings.Save();
            RefreshPingTargetsList();
            PingTargetsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEditPingTargetClicked(object? sender, EventArgs e)
    {
        if (_pingTargetsListView.SelectedItems.Count == 0)
        {
            return;
        }

        var existing = (PingTargetConfig)_pingTargetsListView.SelectedItems[0].Tag!;
        using var editForm = new PingTargetEditForm(existing);
        if (editForm.ShowDialog(this) == DialogResult.OK)
        {
            int index = _settings.PingTargets.FindIndex(x => x.Id == existing.Id);
            if (index >= 0)
            {
                _settings.PingTargets[index] = editForm.Result;
            }

            _settings.Save();
            RefreshPingTargetsList();
            PingTargetsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnRemovePingTargetClicked(object? sender, EventArgs e)
    {
        if (_pingTargetsListView.SelectedItems.Count == 0)
        {
            return;
        }

        var existing = (PingTargetConfig)_pingTargetsListView.SelectedItems[0].Tag!;
        _settings.PingTargets.RemoveAll(x => x.Id == existing.Id);
        _settings.Save();
        RefreshPingTargetsList();
        PingTargetsChanged?.Invoke(this, EventArgs.Empty);
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
            PopulateSlaChecklist();
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
            PopulateSlaChecklist();
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
        PopulateSlaChecklist();
        EndpointsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// (Re)builds the SLA Report tab's checklist: one row per infra-level diagnosis
    /// classification, plus one row per currently-configured application endpoint (rather than a
    /// single generic "Application" row, since an Application-classified incident can implicate
    /// any one of them and the user may only care about some for this report). Checked = counted
    /// toward the uptime report; unchecked ids are what AppSettings.SlaExcluded* actually stores.
    /// </summary>
    private void PopulateSlaChecklist()
    {
        _slaChecklist.ItemCheck -= OnSlaItemCheck;
        _slaChecklist.Items.Clear();

        (DiagnosisClassification Classification, string LabelKey)[] classifications =
        [
            (DiagnosisClassification.Network, "diag.row.network"),
            (DiagnosisClassification.IpConfiguration, "diag.row.ip"),
            (DiagnosisClassification.Gateway, "diag.row.gateway"),
            (DiagnosisClassification.Internet, "diag.row.internet"),
            (DiagnosisClassification.Dns, "diag.row.dns"),
            (DiagnosisClassification.Https, "diag.row.https"),
            (DiagnosisClassification.TimeSync, "diag.row.time"),
            (DiagnosisClassification.FirewallSuspected, "settings.sla.firewallSuspected"),
        ];

        foreach ((DiagnosisClassification classification, string labelKey) in classifications)
        {
            var item = new SlaItem(LocalizationManager.Instance.Get(labelKey), classification, null);
            _slaChecklist.Items.Add(item, !_settings.SlaExcludedClassifications.Contains(classification));
        }

        foreach (EndpointConfig endpoint in _settings.ApplicationEndpoints)
        {
            var item = new SlaItem(endpoint.Name, null, endpoint.Id);
            _slaChecklist.Items.Add(item, !_settings.SlaExcludedEndpointIds.Contains(endpoint.Id));
        }

        _slaChecklist.ItemCheck += OnSlaItemCheck;
    }

    private void OnSlaItemCheck(object? sender, ItemCheckEventArgs e)
    {
        if (_slaChecklist.Items[e.Index] is not SlaItem item)
        {
            return;
        }

        bool included = e.NewValue == CheckState.Checked;
        if (item.Classification is { } classification)
        {
            if (included)
            {
                _settings.SlaExcludedClassifications.Remove(classification);
            }
            else if (!_settings.SlaExcludedClassifications.Contains(classification))
            {
                _settings.SlaExcludedClassifications.Add(classification);
            }
        }
        else if (item.EndpointId is { } endpointId)
        {
            if (included)
            {
                _settings.SlaExcludedEndpointIds.Remove(endpointId);
            }
            else if (!_settings.SlaExcludedEndpointIds.Contains(endpointId))
            {
                _settings.SlaExcludedEndpointIds.Add(endpointId);
            }
        }

        _settings.Save();
    }

    private sealed record SlaItem(string DisplayName, DiagnosisClassification? Classification, string? EndpointId)
    {
        public override string ToString() => DisplayName;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        string code = LocalizationManager.SupportedLanguages[_languageCombo.SelectedIndex].Code;
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

    private void OnExportClicked(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "internet-monitor-settings.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _settings.ExportTo(dialog.FileName);
            MessageBox.Show(this, LocalizationManager.Instance.Get("settings.export.success"), LocalizationManager.Instance.Get("settings.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, LocalizationManager.Instance.Format("settings.export.error", ex.Message), LocalizationManager.Instance.Get("settings.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Applies an imported settings file live (no app restart) by copying every property onto
    /// the existing AppSettings instance in place (see AppSettings.ApplyFrom - many components
    /// already hold a reference to this exact instance) and then re-raising every "changed" event
    /// this form already has, so each live-reconfigure hook TrayApplicationContext subscribed
    /// picks up the new values immediately. This form's own controls are not individually
    /// resynced (a lot of surface area to get right for a rarely-used action) - closing and
    /// reopening Settings shows the imported values, which is a small ask next to "no restart".
    /// </summary>
    private void OnImportClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            AppSettings? imported = AppSettings.ImportFrom(dialog.FileName)
                ?? throw new InvalidOperationException("File did not contain valid settings.");

            _settings.ApplyFrom(imported);
            _settings.Save();
            ApplyAutostartRegistryValue(_settings.AutoStartWithWindows);
            LocalizationManager.Instance.SetLanguage(_settings.Language);

            KumaSettingsChanged?.Invoke(this, EventArgs.Empty);
            EndpointsChanged?.Invoke(this, EventArgs.Empty);
            PingTargetsChanged?.Invoke(this, EventArgs.Empty);
            LatencyThresholdsChanged?.Invoke(this, EventArgs.Empty);
            HistoryRetentionChanged?.Invoke(this, EventArgs.Empty);
            CheckForUpdatesChanged?.Invoke(this, EventArgs.Empty);
            TimeSyncCheckEnabledChanged?.Invoke(this, EventArgs.Empty);
            SpeedTestSettingsChanged?.Invoke(this, EventArgs.Empty);

            MessageBox.Show(this, LocalizationManager.Instance.Get("settings.import.success"), LocalizationManager.Instance.Get("settings.title"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, LocalizationManager.Instance.Format("settings.import.error", ex.Message), LocalizationManager.Instance.Get("settings.title"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnQuietHoursChanged(object? sender, EventArgs e)
    {
        _settings.QuietHoursEnabled = _quietHoursCheckBox.Checked;
        _settings.QuietHoursStart = TimeOnly.FromDateTime(_quietHoursStartPicker.Value);
        _settings.QuietHoursEnd = TimeOnly.FromDateTime(_quietHoursEndPicker.Value);
        _settings.Save();
    }

    private void OnCheckForUpdatesChanged(object? sender, EventArgs e)
    {
        _settings.CheckForUpdates = _checkForUpdatesCheckBox.Checked;
        _settings.Save();
        CheckForUpdatesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpeedTestAutoRunChanged(object? sender, EventArgs e)
    {
        _settings.SpeedTestAutoRunEnabled = _speedTestAutoRunCheckBox.Checked;
        _settings.Save();
        _speedTestIntervalCombo.Enabled = _speedTestAutoRunCheckBox.Checked;
        SpeedTestSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSpeedTestIntervalChanged(object? sender, EventArgs e)
    {
        int index = Math.Max(0, _speedTestIntervalCombo.SelectedIndex);
        _settings.SpeedTestIntervalHours = AppSettings.AllowedSpeedTestIntervalHours[index];
        _settings.Save();
        SpeedTestSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rebuilds the interval combo's items - same shape as ApplyHistoryRetentionUnitLabels, though today's "{0} h" format is identical across all 4 languages so this never actually changes text on a language switch; kept for consistency/future-proofing rather than because it's currently load-bearing.</summary>
    private void PopulateSpeedTestIntervalCombo()
    {
        int selectedIndex = _speedTestIntervalCombo.SelectedIndex;
        _speedTestIntervalCombo.SelectedIndexChanged -= OnSpeedTestIntervalChanged;
        _speedTestIntervalCombo.Items.Clear();
        foreach (int hours in AppSettings.AllowedSpeedTestIntervalHours)
        {
            _speedTestIntervalCombo.Items.Add(LocalizationManager.Instance.Format("settings.speedTest.interval.format", hours));
        }

        if (selectedIndex >= 0)
        {
            _speedTestIntervalCombo.SelectedIndex = selectedIndex;
        }

        _speedTestIntervalCombo.SelectedIndexChanged += OnSpeedTestIntervalChanged;
    }

    private void OnTimeSyncCheckEnabledChanged(object? sender, EventArgs e)
    {
        _settings.TimeSyncCheckEnabled = _timeSyncCheckEnabledCheckBox.Checked;
        _settings.Save();
        TimeSyncCheckEnabledChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// Re-applies every piece of static translated text in this form to the now-current language
    /// - called once from the constructor's implicit initial state and again, live, from
    /// <see cref="OnLanguageChanged"/> whenever the Language dropdown changes, with no reopen
    /// required. Every control with fixed text is a field precisely so this method can reach it;
    /// anything populated dynamically (the SLA/Uptime-Report checklist, the status-diagram
    /// endpoint combo) instead re-runs the same populate method its own data changes already use,
    /// since those already read the current language at call time.
    /// </summary>
    private void ApplyLocalizedText()
    {
        Text = LocalizationManager.Instance.Get("settings.title");

        _generalTab.Text = LocalizationManager.Instance.Get("settings.tab.general");
        _endpointsTab.Text = LocalizationManager.Instance.Get("settings.tab.endpoints");
        _pingTab.Text = LocalizationManager.Instance.Get("settings.tab.ping");
        _diagTab.Text = LocalizationManager.Instance.Get("settings.tab.diagnostics");
        _slaTab.Text = LocalizationManager.Instance.Get("settings.tab.sla");
        _speedTestTab.Text = LocalizationManager.Instance.Get("settings.tab.speedTest");
        _kumaTab.Text = LocalizationManager.Instance.Get("settings.tab.kuma");

        // General tab
        _languageLabel.Text = LocalizationManager.Instance.Get("settings.language");
        _autostartCheckBox.Text = LocalizationManager.Instance.Get("settings.autostart");
        _showOutagePopupsCheckBox.Text = LocalizationManager.Instance.Get("settings.showOutagePopups");
        _quietHoursCheckBox.Text = LocalizationManager.Instance.Get("settings.quietHours");
        _quietHoursToLabel.Text = LocalizationManager.Instance.Get("settings.quietHours.to");
        _checkForUpdatesCheckBox.Text = LocalizationManager.Instance.Get("settings.checkForUpdates");
        _timeSyncCheckEnabledCheckBox.Text = LocalizationManager.Instance.Get("settings.timeSyncCheckEnabled");
        _addPingTargetButton.Text = LocalizationManager.Instance.Get("endpoint.add");
        _editPingTargetButton.Text = LocalizationManager.Instance.Get("endpoint.editButton");
        _removePingTargetButton.Text = LocalizationManager.Instance.Get("endpoint.remove");
        _pingTargetsHintLabel.Text = LocalizationManager.Instance.Get("pingTarget.hint");
        _pingTargetsListView.Columns[0].Text = LocalizationManager.Instance.Get("pingTarget.name");
        _pingTargetsListView.Columns[1].Text = LocalizationManager.Instance.Get("pingTarget.address");
        _pingTargetsListView.Columns[2].Text = LocalizationManager.Instance.Get("endpoint.enabled");

        // Endpoints tab
        _addEndpointButton.Text = LocalizationManager.Instance.Get("endpoint.add");
        _editEndpointButton.Text = LocalizationManager.Instance.Get("endpoint.editButton");
        _removeEndpointButton.Text = LocalizationManager.Instance.Get("endpoint.remove");
        _statusDiagramEndpointLabel.Text = LocalizationManager.Instance.Get("settings.endpoint.statusDiagram");
        _endpointsListView.Columns[0].Text = LocalizationManager.Instance.Get("endpoint.name");
        _endpointsListView.Columns[1].Text = LocalizationManager.Instance.Get("endpoint.url");
        _endpointsListView.Columns[2].Text = LocalizationManager.Instance.Get("endpoint.enabled");
        RefreshStatusDiagramEndpointCombo();

        // Diagnostics (logging) tab
        _levelGroup.Text = LocalizationManager.Instance.Get("settings.logLevel.heading");
        _logOffRadio.Text = LocalizationManager.Instance.Get("settings.logLevel.off");
        _logBasicRadio.Text = LocalizationManager.Instance.Get("settings.logLevel.basic");
        _logExtendedRadio.Text = LocalizationManager.Instance.Get("settings.logLevel.extended");
        _logFullRadio.Text = LocalizationManager.Instance.Get("settings.logLevel.full");
        _logSuccessCheckBox.Text = LocalizationManager.Instance.Get("settings.logSuccessful");
        _logDetailedCheckBox.Text = LocalizationManager.Instance.Get("settings.logDetailed");
        _maxFilesLabel.Text = LocalizationManager.Instance.Get("settings.maxLogFiles");
        _maxSizeLabel.Text = LocalizationManager.Instance.Get("settings.maxLogSize");
        _latencyGroup.Text = LocalizationManager.Instance.Get("settings.latency.heading");
        _latencyWarningLabel.Text = LocalizationManager.Instance.Get("settings.latency.warning");
        _latencyErrorLabel.Text = LocalizationManager.Instance.Get("settings.latency.error");
        _historyGroup.Text = LocalizationManager.Instance.Get("settings.historyRetention.heading");
        _historyRetentionLabel.Text = LocalizationManager.Instance.Get("settings.historyRetention.label");
        ApplyHistoryRetentionUnitLabels();

        // SLA/Uptime Report tab
        _slaHintLabel.Text = LocalizationManager.Instance.Get("settings.sla.hint");
        PopulateSlaChecklist();

        // Speed Test tab
        _speedTestAutoRunCheckBox.Text = LocalizationManager.Instance.Get("settings.speedTest.autoRun");
        _speedTestIntervalLabel.Text = LocalizationManager.Instance.Get("settings.speedTest.interval");
        _speedTestHintLabel.Text = LocalizationManager.Instance.Get("settings.speedTest.hint");
        PopulateSpeedTestIntervalCombo();

        // Uptime Kuma tab
        _kumaUrlLabel.Text = LocalizationManager.Instance.Get("settings.kuma.url");
        _kumaIntervalLabel.Text = LocalizationManager.Instance.Get("settings.kuma.interval");
        _kumaHintLabel.Text = LocalizationManager.Instance.Get("settings.kuma.hint");
        _kumaProjectLink.Text = LocalizationManager.Instance.Get("settings.kuma.projectLink");

        _exportButton.Text = LocalizationManager.Instance.Get("settings.export");
        _importButton.Text = LocalizationManager.Instance.Get("settings.import");
        _closeButton.Text = LocalizationManager.Instance.Get("settings.close");

        // These only ever populate on a user action (a failed validation), never on load - if one
        // happens to be showing when the language changes, clearing it is simpler and safer than
        // re-running whatever validation set it; it'll show correctly, in the new language, the
        // next time the user actually triggers it again.
        _autostartErrorLabel.Text = string.Empty;
        _latencyValidationLabel.Text = string.Empty;
        _kumaUrlErrorLabel.Text = string.Empty;
    }

    /// <summary>Rebuilds _historyRetentionUnitCombo's 3 items in the new language while preserving the current selection - a plain .Text reassignment doesn't apply to ComboBox.Items, so unlike every label above this needs an actual rebuild.</summary>
    private void ApplyHistoryRetentionUnitLabels()
    {
        int selectedIndex = _historyRetentionUnitCombo.SelectedIndex;
        _historyRetentionUnitCombo.SelectedIndexChanged -= OnHistoryRetentionUnitChanged;
        _historyRetentionUnitCombo.Items.Clear();
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.minutes"));
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.hours"));
        _historyRetentionUnitCombo.Items.Add(LocalizationManager.Instance.Get("settings.historyRetention.unit.days"));
        _historyRetentionUnitCombo.SelectedIndex = selectedIndex;
        _historyRetentionUnitCombo.SelectedIndexChanged += OnHistoryRetentionUnitChanged;
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
