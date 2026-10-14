using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// Settings, in place rather than in its own window - one of thirteen separate forms in v1, here
/// just another page, which is most of why it needed no navigation chrome of its own.
///
/// Scoped to what v2 actually has behind it. Language, theme, auto-start, cadence, the traceroute
/// ownership lookup and the step-7 endpoint list are all real, working settings with something
/// listening on the other end. v1's ping targets, quiet hours, update checking, SLA exclusions,
/// speed test scheduling, diagnostic log levels and Uptime Kuma push are deliberately not here:
/// none of those have a monitor behind them yet in v2, and a setting that controls nothing is
/// worse than a screen that says plainly it is not built yet.
///
/// Every change here applies immediately and saves immediately - no separate "Apply" button,
/// matching v1's live-reconfiguration behaviour. Cadence and the traceroute lookup are read fresh
/// by whatever uses them on their very next cycle; language and theme propagate through the
/// existing LocalizationManager/Theme change events the rest of the app already listens for.
/// </summary>
public sealed class SettingsPage(AppServices services) : PageBase(services)
{
    private Card _generalCard = null!;
    private Card _updatesCard = null!;
    private Card _cadenceCard = null!;
    private Card _toolsCard = null!;
    private Card _diagnosticsCard = null!;
    private Card _networkHealthCard = null!;
    private Card _integrationsCard = null!;
    private Card _endpointsCard = null!;
    private Card _publicIpCard = null!;
    private Card _exportCard = null!;
    private Card _settingsBackupCard = null!;

    private ComboBox _language = null!;
    private ComboBox _theme = null!;
    private Toggle _autoStart = null!;
    private Toggle _showIncidentPopups = null!;
    private Toggle _checkForUpdates = null!;
    private Toggle _autoUpdateInstall = null!;
    private Label _currentVersion = null!;
    private NumericUpDown _foregroundInterval = null!;
    private NumericUpDown _backgroundInterval = null!;
    private Toggle _tracerouteLookup = null!;
    private Toggle _lanScanVendorLookup = null!;
    private Toggle _lanScanTcpFallback = null!;
    private Toggle _speedTestAutoRun = null!;
    private ComboBox _speedTestInterval = null!;
    private ComboBox _logLevel = null!;
    private Toggle _logSuccessfulChecks = null!;
    private Toggle _logDetailedMeasurements = null!;
    private TextBox _logDirectory = null!;
    private AppButton _browseLogDirectory = null!;
    private AppButton _openLogFolder = null!;
    private AppButton _resetLogDirectory = null!;
    private Toggle _networkHealthEnabled = null!;
    private readonly List<ConditionRow> _conditionRows = [];
    private TextBox _kumaUrl = null!;
    private TextBox _publicIpTargets = null!;
    private TextBox _customDnsServers = null!;
    private Label _publicIpError = null!;
    private NumericUpDown _kumaInterval = null!;
    private Label _kumaError = null!;
    private AppButton _kumaAbout = null!;
    private EndpointList _endpoints = null!;
    private AppButton _addEndpoint = null!;
    private AppButton _exportAll = null!;
    private AppButton _exportAi = null!;
    private AppButton _exportPlain = null!;
    private AppButton _exportSettings = null!;
    private AppButton _importSettings = null!;
    private readonly List<AppButton> _exportButtons = [];
    private readonly List<AppButton> _settingsBackupButtons = [];

    private readonly List<(Label Label, Control Field)> _rows = [];

    public override string Id => "settings";

    protected override void Build()
    {
        _generalCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Settings };
        BuildGeneralRows();

        _updatesCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Refresh };
        BuildUpdatesRows();

        _cadenceCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Refresh };
        BuildCadenceRows();

        _toolsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Tools };
        BuildToolsRows();

        _diagnosticsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Diagnostics };
        BuildDiagnosticsRows();

        _networkHealthCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Pulse };
        BuildNetworkHealthRows();

        _integrationsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Globe };
        BuildIntegrationsRows();

        _publicIpCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Globe };
        BuildPublicIpRows();

        _endpointsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Target };
        BuildEndpointsSection();

        _exportCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Copy };
        BuildExportSection();

        _settingsBackupCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Copy };
        BuildSettingsBackupSection();

        // Reverse visual order: DockStyle.Top stacks later siblings nearer the top edge. Export and
        // the configuration backup card are added first - and so end up lowest - because they are
        // occasional actions, not something that belongs among the frequently-changed settings
        // above them. The configuration backup card sits right below Export, added even earlier so
        // it ends up even lower, since the two are closely related but deliberately kept apart - one
        // exports the monitor's *data*, the other its *configuration*.
        Controls.Add(_settingsBackupCard);
        Controls.Add(_exportCard);
        Controls.Add(_endpointsCard);
        Controls.Add(_publicIpCard);
        Controls.Add(_integrationsCard);
        Controls.Add(_networkHealthCard);
        Controls.Add(_diagnosticsCard);
        Controls.Add(_toolsCard);
        Controls.Add(_cadenceCard);
        Controls.Add(_updatesCard);
        Controls.Add(_generalCard);

        LoadFromSettings();
        LayoutCards();
    }

    // ---- General ------------------------------------------------------------------------------

    private void BuildGeneralRows()
    {
        _language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        foreach ((string code, string displayName) in LocalizationManager.SupportedLanguages)
        {
            _language.Items.Add(new ComboItem(code, displayName));
        }

        _language.SelectedIndexChanged += OnLanguageChanged;
        AddRow(_generalCard, "settings.language", _language);

        _theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _theme.SelectedIndexChanged += OnThemeSelectionChanged;
        AddRow(_generalCard, "settings.theme", _theme);

        _autoStart = new Toggle();
        _autoStart.CheckedChanged += OnAutoStartChanged;
        AddRow(_generalCard, "settings.autoStart", _autoStart);

        _showIncidentPopups = new Toggle();
        _showIncidentPopups.CheckedChanged += (_, _) =>
        {
            Services.Settings.ShowIncidentPopups = _showIncidentPopups.Checked;
            Services.Settings.Save();
        };
        AddRow(_generalCard, "settings.showIncidentPopups", _showIncidentPopups);
    }

    // ---- Updates ------------------------------------------------------------------------------

    private void BuildUpdatesRows()
    {
        _checkForUpdates = new Toggle();
        _checkForUpdates.CheckedChanged += (_, _) =>
        {
            Services.Settings.CheckForUpdates = _checkForUpdates.Checked;
            Services.Settings.Save();
            Services.UpdateChecker.Reconfigure(_checkForUpdates.Checked);
            _autoUpdateInstall.Enabled = _checkForUpdates.Checked;
        };
        AddRow(_updatesCard, "settings.checkForUpdates", _checkForUpdates);

        _autoUpdateInstall = new Toggle();
        _autoUpdateInstall.CheckedChanged += (_, _) =>
        {
            Services.Settings.AutoUpdateInstall = _autoUpdateInstall.Checked;
            Services.Settings.Save();
        };
        AddRow(_updatesCard, "settings.autoUpdateInstall", _autoUpdateInstall);

        _currentVersion = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Width = Dp(160),
            Font = Theme.Font(FontRole.Body),
            Text = (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(2, 0, 0)).ToString(3),
        };
        AddRow(_updatesCard, "settings.currentVersion", _currentVersion);
    }

    // ---- Cadence ------------------------------------------------------------------------------

    private void BuildCadenceRows()
    {
        _foregroundInterval = MakeIntervalField(1, 10);
        _foregroundInterval.ValueChanged += (_, _) =>
        {
            Services.Settings.ForegroundIntervalSeconds = (int)_foregroundInterval.Value;
            Services.Settings.Save();
        };
        AddRow(_cadenceCard, "settings.foregroundInterval", _foregroundInterval);

        _backgroundInterval = MakeIntervalField(5, 300);
        _backgroundInterval.ValueChanged += (_, _) =>
        {
            int seconds = (int)_backgroundInterval.Value;
            Services.Settings.BackgroundIntervalSeconds = seconds;
            Services.Settings.Save();

            // History is deliberately sampled at the background rate regardless of how fast the
            // pipeline is actually running (see MetricStore's own comment on why) - changing this
            // setting is the one place that rate itself should also change, live.
            Services.History.MinimumSampleInterval = TimeSpan.FromSeconds(seconds);
        };
        AddRow(_cadenceCard, "settings.backgroundInterval", _backgroundInterval);
    }

    private NumericUpDown MakeIntervalField(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = Dp(70),
        TextAlign = HorizontalAlignment.Right,
    };

    // ---- Tools --------------------------------------------------------------------------------

    private void BuildToolsRows()
    {
        _tracerouteLookup = new Toggle();
        _tracerouteLookup.CheckedChanged += (_, _) =>
        {
            Services.Settings.TracerouteOwnerLookup = _tracerouteLookup.Checked;
            Services.Settings.Save();
        };
        AddRow(_toolsCard, "settings.tracerouteLookup", _tracerouteLookup);

        _lanScanVendorLookup = new Toggle();
        _lanScanVendorLookup.CheckedChanged += (_, _) =>
        {
            Services.Settings.LanScanVendorLookup = _lanScanVendorLookup.Checked;
            Services.Settings.Save();
        };
        AddRow(_toolsCard, "settings.lanScanVendorLookup", _lanScanVendorLookup);

        _lanScanTcpFallback = new Toggle();
        _lanScanTcpFallback.CheckedChanged += (_, _) =>
        {
            Services.Settings.LanScanTcpFallback = _lanScanTcpFallback.Checked;
            Services.Settings.Save();
        };
        AddRow(_toolsCard, "settings.lanScanTcpFallback", _lanScanTcpFallback);

        _speedTestAutoRun = new Toggle();
        _speedTestAutoRun.CheckedChanged += (_, _) =>
        {
            Services.Settings.SpeedTestAutoRunEnabled = _speedTestAutoRun.Checked;
            Services.Settings.Save();
            Services.SpeedTestScheduler.Reconfigure(Services.Settings.SpeedTestAutoRunEnabled, Services.Settings.SpeedTestIntervalHours);
            _speedTestInterval.Enabled = _speedTestAutoRun.Checked;
        };
        AddRow(_toolsCard, "settings.speedTestAutoRun", _speedTestAutoRun);

        _speedTestInterval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _speedTestInterval.SelectedIndexChanged += OnSpeedTestIntervalChanged;
        AddRow(_toolsCard, "settings.speedTestInterval", _speedTestInterval);
    }

    private void OnSpeedTestIntervalChanged(object? sender, EventArgs e)
    {
        if (_speedTestInterval.SelectedItem is not ComboItem item || !int.TryParse(item.Key, out int hours))
        {
            return;
        }

        Services.Settings.SpeedTestIntervalHours = hours;
        Services.Settings.Save();
        Services.SpeedTestScheduler.Reconfigure(Services.Settings.SpeedTestAutoRunEnabled, Services.Settings.SpeedTestIntervalHours);
    }

    // ---- Diagnostics --------------------------------------------------------------------------

    private void BuildDiagnosticsRows()
    {
        _logLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _logLevel.SelectedIndexChanged += OnLogLevelChanged;
        AddRow(_diagnosticsCard, "settings.diagnostics.level", _logLevel);

        _logSuccessfulChecks = new Toggle();
        _logSuccessfulChecks.CheckedChanged += (_, _) =>
        {
            Services.Settings.LogSuccessfulChecks = _logSuccessfulChecks.Checked;
            Services.Settings.Save();
        };
        AddRow(_diagnosticsCard, "settings.diagnostics.logSuccesses", _logSuccessfulChecks);

        _logDetailedMeasurements = new Toggle();
        _logDetailedMeasurements.CheckedChanged += (_, _) =>
        {
            Services.Settings.LogDetailedMeasurements = _logDetailedMeasurements.Checked;
            Services.Settings.Save();
        };
        AddRow(_diagnosticsCard, "settings.diagnostics.logDetails", _logDetailedMeasurements);

        _logDirectory = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Width = Dp(200),
            Font = Theme.Font(FontRole.Body),
            ReadOnly = true,
        };
        AddRow(_diagnosticsCard, "settings.diagnostics.directory", _logDirectory);

        _browseLogDirectory = new AppButton { Variant = ButtonVariant.Secondary, Text = Loc.Get("settings.diagnostics.browse") };
        _browseLogDirectory.SizeToContent();
        _browseLogDirectory.Click += OnBrowseLogDirectoryClicked;
        _diagnosticsCard.Controls.Add(_browseLogDirectory);

        _openLogFolder = new AppButton { Variant = ButtonVariant.Ghost, Text = Loc.Get("settings.diagnostics.openFolder") };
        _openLogFolder.SizeToContent();
        _openLogFolder.Click += (_, _) => OpenLogFolder();
        _diagnosticsCard.Controls.Add(_openLogFolder);

        _resetLogDirectory = new AppButton { Variant = ButtonVariant.Ghost, Text = Loc.Get("settings.diagnostics.resetDirectory") };
        _resetLogDirectory.SizeToContent();
        _resetLogDirectory.Click += (_, _) =>
        {
            Services.Settings.DiagnosticLogDirectory = string.Empty;
            Services.Settings.Save();
            RefreshLogDirectoryText();
        };
        _diagnosticsCard.Controls.Add(_resetLogDirectory);
    }

    private void OnLogLevelChanged(object? sender, EventArgs e)
    {
        if (_logLevel.SelectedItem is not ComboItem item || !Enum.TryParse(item.Key, out DiagnosticLogLevel level))
        {
            return;
        }

        Services.Settings.DiagnosticLogLevel = level;
        Services.Settings.Save();
    }

    private void OnBrowseLogDirectoryClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Services.DiagnosticLog.ResolvedDirectory(),
            Description = Loc.Get("settings.diagnostics.browse"),
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        Services.Settings.DiagnosticLogDirectory = dialog.SelectedPath;
        Services.Settings.Save();
        RefreshLogDirectoryText();
    }

    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.DiagnosticLog.ResolvedDirectory()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The folder does not exist yet (nothing has logged there) or no shell handler is
            // registered - not worth surfacing for a convenience shortcut the user can open
            // themselves via Explorer.
        }
    }

    private void RefreshLogDirectoryText() => _logDirectory.Text = Services.DiagnosticLog.ResolvedDirectory();

    // ---- Network health -------------------------------------------------------------------------

    private void BuildNetworkHealthRows()
    {
        _networkHealthEnabled = new Toggle();
        _networkHealthEnabled.CheckedChanged += (_, _) =>
        {
            Services.Settings.NetworkHealthMonitoringEnabled = _networkHealthEnabled.Checked;
            Services.Settings.Save();
            ReconfigureNetworkHealth();
        };
        AddRow(_networkHealthCard, "settings.networkHealth.enabled", _networkHealthEnabled);

        MakeConditionRow("tcpRetransmissions", "settings.networkHealth.tcpRetransmissions", 0, 1_000_000, hasThreshold: true);
        MakeConditionRow("packetLoss", "settings.networkHealth.packetLoss", 0, 100, hasThreshold: true);
        MakeConditionRow("ttlExceeded", "settings.networkHealth.ttlExceeded", 0, 1_000_000, hasThreshold: true);

        // Rogue-DHCP has no occurrence threshold - any server that is not the known gateway is
        // rogue - so this row skips the threshold field and "more than"/"in" wording entirely and
        // reads as "[toggle] Rogue DHCP server, check every [window] minutes" instead.
        MakeConditionRow("rogueDhcp", "settings.networkHealth.rogueDhcp", 0, 0, hasThreshold: false);
    }

    /// <summary>
    /// One condition's own row: [toggle] [name] "more than" [threshold] "in" [window] "minutes",
    /// or (for rogue-DHCP, see <see cref="BuildNetworkHealthRows"/>) [toggle] [name] "every"
    /// [window] "minutes". Four conditions, each with its own enable flag and numbers that do not
    /// all mean the same thing, do not fit the generic single-label/single-field
    /// <see cref="AddRow"/> shape, so these are laid out by hand in <see cref="LayoutCards"/>
    /// instead - same reasoning as the diagnostics directory buttons just above, which already sit
    /// below their card's generic rows for the same kind of reason.
    /// </summary>
    private void MakeConditionRow(string key, string nameKey, int minThreshold, int maxThreshold, bool hasThreshold)
    {
        var toggle = new Toggle();
        var name = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(FontRole.Body), Tag = nameKey };
        NumericUpDown? threshold = hasThreshold ? MakeIntervalField(minThreshold, Math.Max(minThreshold, maxThreshold)) : null;
        var intervalLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Font(FontRole.Body),
            Tag = hasThreshold ? "settings.networkHealth.in" : "settings.networkHealth.every",
        };
        NumericUpDown window = MakeIntervalField(1, 1440);
        var minutesLabel = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(FontRole.Body), Tag = "settings.networkHealth.minutes" };

        var row = new ConditionRow(key, toggle, name, threshold, intervalLabel, window, minutesLabel);
        _conditionRows.Add(row);

        void Apply(object? sender, EventArgs e)
        {
            int thresholdValue = threshold is null ? 0 : (int)threshold.Value;
            SetConditionThreshold(key, new NetworkHealthThreshold(toggle.Checked, thresholdValue, (int)window.Value));
            Services.Settings.Save();
            ReconfigureNetworkHealth();
        }

        toggle.CheckedChanged += Apply;
        if (threshold is not null)
        {
            threshold.ValueChanged += Apply;
        }

        window.ValueChanged += Apply;

        _networkHealthCard.Controls.Add(toggle);
        _networkHealthCard.Controls.Add(name);
        if (threshold is not null)
        {
            _networkHealthCard.Controls.Add(threshold);
        }

        _networkHealthCard.Controls.Add(intervalLabel);
        _networkHealthCard.Controls.Add(window);
        _networkHealthCard.Controls.Add(minutesLabel);
    }

    private void SetConditionThreshold(string key, NetworkHealthThreshold value)
    {
        switch (key)
        {
            case "tcpRetransmissions":
                Services.Settings.TcpRetransmissions = value;
                break;
            case "packetLoss":
                Services.Settings.PacketLoss = value;
                break;
            case "ttlExceeded":
                Services.Settings.TtlExceeded = value;
                break;
            case "rogueDhcp":
                Services.Settings.RogueDhcp = value;
                break;
        }
    }

    private NetworkHealthThreshold GetConditionThreshold(string key) => key switch
    {
        "tcpRetransmissions" => Services.Settings.TcpRetransmissions,
        "packetLoss" => Services.Settings.PacketLoss,
        "ttlExceeded" => Services.Settings.TtlExceeded,
        "rogueDhcp" => Services.Settings.RogueDhcp,
        _ => new NetworkHealthThreshold(false, 0, 5),
    };

    private void ReconfigureNetworkHealth() => Services.NetworkHealth.Reconfigure(
        Services.Settings.NetworkHealthMonitoringEnabled,
        Services.Settings.TcpRetransmissions,
        Services.Settings.PacketLoss,
        Services.Settings.TtlExceeded,
        Services.Settings.RogueDhcp);

    // ---- Public internet (steps 5 and 6) --------------------------------------------------------

    private void BuildPublicIpRows()
    {
        _publicIpTargets = MakeAddressField();
        AddRow(_publicIpCard, "settings.publicIp.targets", _publicIpTargets);

        _customDnsServers = MakeAddressField();
        AddRow(_publicIpCard, "settings.publicIp.customDns", _customDnsServers);

        _publicIpError = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            Text = string.Empty,
        };
        _publicIpCard.Controls.Add(_publicIpError);
    }

    private TextBox MakeAddressField()
    {
        var box = new TextBox { BorderStyle = BorderStyle.FixedSingle, Width = Dp(260), Font = Theme.Font(FontRole.Body) };
        box.Leave += OnPublicAddressesChanged;
        return box;
    }

    /// <summary>
    /// Validates both address fields together, saves whichever is valid, and shows the first
    /// error - checking both on every edit keeps the one error line honest about a field that is
    /// still wrong after the other one was fixed.
    /// </summary>
    private void OnPublicAddressesChanged(object? sender, EventArgs e)
    {
        string? targetsError = ApplyAddressField(_publicIpTargets, list => Services.Settings.PublicIpTargets = list, allowEmpty: false, maxCount: null);
        string? customError = ApplyAddressField(_customDnsServers, list => Services.Settings.CustomDnsServers = list, allowEmpty: true, maxCount: AppSettings.MaxCustomDnsServers);
        _publicIpError.Text = targetsError ?? customError ?? string.Empty;
        Services.Settings.Save();
    }

    /// <summary>
    /// Accepts a comma-, semicolon- or space-separated list of IPv4 addresses. Any entry that is
    /// not one rejects the field as a whole rather than being silently dropped, so what is saved
    /// is always exactly what the field shows. Returns the error to show, or null once stored.
    /// </summary>
    private string? ApplyAddressField(TextBox box, Action<List<string>> store, bool allowEmpty, int? maxCount)
    {
        string[] entries = box.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0 && !allowEmpty)
        {
            return Loc.Get("settings.publicIp.error.empty");
        }

        if (entries.FirstOrDefault(entry => AppSettings.ParseIPv4List([entry]).Count == 0) is { } invalid)
        {
            return Loc.Format("settings.publicIp.error.invalid", invalid);
        }

        List<string> normalized = [.. AppSettings.ParseIPv4List(entries).Select(a => a.ToString())];
        if (maxCount is { } max && normalized.Count > max)
        {
            return Loc.Format("settings.publicIp.error.tooMany", max);
        }

        store(normalized);
        box.Text = string.Join(", ", normalized);
        return null;
    }

    // ---- Integrations ---------------------------------------------------------------------------

    private void BuildIntegrationsRows()
    {
        _kumaUrl = new TextBox { BorderStyle = BorderStyle.FixedSingle, Width = Dp(200), Font = Theme.Font(FontRole.Body) };
        _kumaUrl.Leave += OnKumaSettingsChanged;
        AddRow(_integrationsCard, "settings.kuma.pushUrl", _kumaUrl);

        _kumaInterval = MakeIntervalField(KumaPusher.MinimumIntervalSeconds, 86400);
        _kumaInterval.ValueChanged += OnKumaSettingsChanged;
        AddRow(_integrationsCard, "settings.kuma.interval", _kumaInterval);

        _kumaError = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            Text = string.Empty,
        };
        _integrationsCard.Controls.Add(_kumaError);

        _kumaAbout = new AppButton { Variant = ButtonVariant.Ghost, Glyph = Glyph.Globe, Text = Loc.Get("settings.kuma.about") };
        _kumaAbout.SizeToContent();
        _kumaAbout.Click += (_, _) => OpenUptimeKumaProjectPage();
        _integrationsCard.Controls.Add(_kumaAbout);
    }

    private static void OpenUptimeKumaProjectPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/louislam/uptime-kuma") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser configured, or the shell call otherwise failed - not worth
            // surfacing for an informational link the user can open themselves.
        }
    }

    private void OnKumaSettingsChanged(object? sender, EventArgs e)
    {
        string url = _kumaUrl.Text.Trim();

        // Empty is valid - it means the integration is off. Otherwise require a well-formed https
        // URL, the same standard endpoint targets enforce, so a typo or plaintext http:// URL
        // doesn't silently reach KumaPusher.
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps))
        {
            _kumaError.Text = Loc.Get("settings.kuma.url.error");
            return;
        }

        _kumaError.Text = string.Empty;
        Services.Settings.KumaPushUrl = url;
        Services.Settings.KumaIntervalSeconds = (int)_kumaInterval.Value;
        Services.Settings.Save();
        Services.Kuma.Reconfigure(Services.Settings.KumaPushUrl, Services.Settings.KumaIntervalSeconds);
    }

    // ---- Endpoints ----------------------------------------------------------------------------

    private void BuildEndpointsSection()
    {
        _endpoints = new EndpointList { Dock = DockStyle.Top };
        _endpoints.EditRequested += OnEndpointEditRequested;
        _endpoints.ToggleChanged += OnEndpointToggled;
        _endpoints.RemoveRequested += OnEndpointRemoved;

        _addEndpoint = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Target, Dock = DockStyle.Top };
        _addEndpoint.Click += (_, _) => OpenEndpointEditor(null);

        // Fill before Top within this card: the button sits above a list whose height tracks the
        // number of endpoints, so the button is added last to land above it.
        _endpointsCard.Controls.Add(_endpoints);
        _endpointsCard.Controls.Add(_addEndpoint);
    }

    private void OnEndpointEditRequested(EndpointConfig endpoint) => OpenEndpointEditor(endpoint);

    private void OpenEndpointEditor(EndpointConfig? existing)
    {
        using EndpointEditWindow window = existing is null ? EndpointEditWindow.ForNew() : EndpointEditWindow.ForEdit(existing);
        if (window.ShowDialog(FindForm()) != DialogResult.OK || window.Result is not { } result)
        {
            return;
        }

        List<EndpointConfig> endpoints = Services.Settings.ApplicationEndpoints;
        int index = endpoints.FindIndex(e => e.Id == result.Id);
        if (index >= 0)
        {
            endpoints[index] = result;
        }
        else
        {
            endpoints.Add(result);
        }

        Services.Settings.Save();
        RefreshEndpoints();
    }

    private void OnEndpointToggled(EndpointConfig updated)
    {
        List<EndpointConfig> endpoints = Services.Settings.ApplicationEndpoints;
        int index = endpoints.FindIndex(e => e.Id == updated.Id);
        if (index < 0)
        {
            return;
        }

        endpoints[index] = updated;
        Services.Settings.Save();
    }

    private void OnEndpointRemoved(EndpointConfig endpoint)
    {
        Services.Settings.ApplicationEndpoints.RemoveAll(e => e.Id == endpoint.Id);
        Services.Settings.Save();
        RefreshEndpoints();
    }

    private void RefreshEndpoints()
    {
        _endpoints.SetEndpoints(Services.Settings.ApplicationEndpoints);
        ResizeEndpointsList();
    }

    private void ResizeEndpointsList()
    {
        _endpoints.Height = _endpoints.GetPreferredSize(Size.Empty).Height;
        LayoutCards();
    }

    // ---- Export ---------------------------------------------------------------------------------

    private void BuildExportSection()
    {
        _exportAll = AddExportButton(_exportCard, _exportButtons, ExportAllData);
        _exportAi = AddExportButton(_exportCard, _exportButtons, ExportForAi);
        _exportPlain = AddExportButton(_exportCard, _exportButtons, ExportPlainData);
    }

    /// <summary>
    /// The configuration backup/restore pair, kept in its own card - see the comment on
    /// <see cref="Build"/> for why this is deliberately separate from <see cref="BuildExportSection"/>.
    /// </summary>
    private void BuildSettingsBackupSection()
    {
        _exportSettings = AddExportButton(_settingsBackupCard, _settingsBackupButtons, ExportSettings);
        _importSettings = AddExportButton(_settingsBackupCard, _settingsBackupButtons, ImportSettings);
    }

    private static AppButton AddExportButton(Card card, List<AppButton> buttons, Action onClick)
    {
        var button = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Copy };
        button.Click += (_, _) => onClick();
        card.Controls.Add(button);
        buttons.Add(button);
        return button;
    }

    private void ExportAllData()
    {
        byte[]? logBytes = null;
        try
        {
            if (File.Exists(Services.DiagnosticLog.LogPath))
            {
                logBytes = File.ReadAllBytes(Services.DiagnosticLog.LogPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Export still proceeds without the log - better than failing the whole export over a
            // file that happened to be locked or unreadable at that moment.
        }

        byte[] zip = DataExportBuilder.Build(Services.Settings, Services.Incidents.All, BuildHistorySnapshot(), logBytes);

        using var dialog = new SaveFileDialog
        {
            Filter = "ZIP (*.zip)|*.zip",
            FileName = $"internetmonitor-export-{DateTime.Now:yyyy-MM-dd}.zip",
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth surfacing further for a manual export the user can just retry.
        }
    }

    private void ExportForAi()
    {
        string markdown = AiExportBuilder.Build(Services.Incidents.All, BuildHistorySnapshot(), Services.Settings);

        using var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = $"internetmonitor-ai-export-{DateTime.Now:yyyy-MM-dd}.md",
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, markdown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth surfacing further for a manual export the user can just retry.
        }
    }

    private void ExportPlainData()
    {
        string markdown = AiExportBuilder.BuildPlain(Services.Incidents.All, BuildHistorySnapshot(), Services.Settings);

        using var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = $"internetmonitor-data-export-{DateTime.Now:yyyy-MM-dd}.md",
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, markdown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth surfacing further for a manual export the user can just retry.
        }
    }

    /// <summary>
    /// Unlike every other export on this card, this one is deliberately unredacted - confirmed
    /// with the user to match v1's own settings export exactly. It is a literal backup/restore
    /// mechanism, not something meant to be shared, and a Kuma push URL or endpoint header with
    /// its secret blanked out would not actually restore anything on import.
    /// </summary>
    private void ExportSettings()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = $"internetmonitor-settings-{DateTime.Now:yyyy-MM-dd}.json",
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            Services.Settings.ExportTo(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth surfacing further for a manual export the user can just retry.
        }
    }

    private void ImportSettings()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        AppSettings? imported;
        try
        {
            imported = AppSettings.ImportFrom(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // Not worth surfacing further for a manual import the user can just retry with a
            // known-good file.
            return;
        }

        if (imported is null)
        {
            return;
        }

        // ApplyFrom only copies properties - every setting that also has a live side effect
        // (autostart's registry key, the active language/theme, Kuma's running pusher, the
        // network-health monitor) needs that side effect re-applied explicitly, the same way each
        // one's own change handler already does when the user edits it directly.
        Services.Settings.ApplyFrom(imported);
        Services.Settings.Save();

        App.AutoStart.SetEnabled(Services.Settings.AutoStartWithWindows);
        LocalizationManager.Instance.SetLanguage(Services.Settings.Language);
        Theme.Mode = Services.Settings.Theme;
        Services.Kuma.Reconfigure(Services.Settings.KumaPushUrl, Services.Settings.KumaIntervalSeconds);
        ReconfigureNetworkHealth();

        LoadFromSettings();
    }

    private Dictionary<string, IReadOnlyList<MetricPoint>> BuildHistorySnapshot()
    {
        var history = new Dictionary<string, IReadOnlyList<MetricPoint>>();
        foreach (ChartableSeries series in SeriesNames.Chartable)
        {
            history[series.Id] = Services.History.Get(series.Id);
        }

        return history;
    }

    // ---- Shared row layout ----------------------------------------------------------------------

    /// <summary>
    /// Adds one label/control row to a settings card. Cards size themselves from their rows, so a
    /// longer translation grows the card instead of clipping the control beside it.
    /// </summary>
    private void AddRow(Card card, string labelKey, Control field)
    {
        var label = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(FontRole.Body) };
        card.Controls.Add(label);
        card.Controls.Add(field);
        _rows.Add((label, field));
        label.Tag = labelKey;
    }

    private int RowHeight => Dp(36);

    /// <summary>
    /// Positions one card's label/field rows and resizes the card to fit them. Bounds set on a
    /// manually-positioned child are always relative to the parent control's own origin (0,0) -
    /// unlike Dock/Anchor layout, WinForms does not consult a custom DisplayRectangle override for
    /// them - so every row has to be offset by the card's own DisplayRectangle explicitly, or it
    /// lands at the card's top-left and sits directly on top of the title instead of below it.
    /// </summary>
    private void LayoutCardRows(Card card)
    {
        List<(Label Label, Control Field)> rows = [.. _rows.Where(r => r.Label.Parent == card)];
        Rectangle content = card.DisplayRectangle;
        int y = content.Top;
        int width = Math.Max(Dp(200), content.Width);

        foreach ((Label label, Control field) in rows)
        {
            label.Bounds = new Rectangle(content.Left, y, Math.Max(0, width - field.Width - Sp(3)), RowHeight);
            field.Location = new Point(content.Left + width - field.Width, y + ((RowHeight - field.Height) / 2));
            y += RowHeight;
        }

        card.Height = y + card.Inset;
    }

    /// <summary>
    /// Positions the four condition rows below the network-health card's one generic row (the
    /// overall enable toggle, already placed by <see cref="LayoutCardRows"/>) - same "generic rows,
    /// then hand-laid-out extras below" pattern the diagnostics and integrations cards use just
    /// below here.
    /// </summary>
    private void LayoutConditionRows()
    {
        Rectangle content = _networkHealthCard.DisplayRectangle;
        int y = _networkHealthCard.Height - _networkHealthCard.Inset;

        int toggleWidth = Dp(44);
        int nameWidth = Dp(170);
        int numberWidth = Dp(70);
        int intervalLabelWidth = Dp(40);
        int minutesLabelWidth = Dp(80);
        int gap = Sp(2);

        foreach (ConditionRow row in _conditionRows)
        {
            int x = content.Left;

            row.Toggle.Location = new Point(x, y + ((RowHeight - row.Toggle.Height) / 2));
            x += toggleWidth + gap;

            row.Name.Bounds = new Rectangle(x, y, nameWidth, RowHeight);
            x += nameWidth + gap;

            if (row.Threshold is { } threshold)
            {
                threshold.Location = new Point(x, y + ((RowHeight - threshold.Height) / 2));
                x += numberWidth + gap;
            }

            row.IntervalLabel.Bounds = new Rectangle(x, y, intervalLabelWidth, RowHeight);
            x += intervalLabelWidth + gap;

            row.Window.Location = new Point(x, y + ((RowHeight - row.Window.Height) / 2));
            x += numberWidth + gap;

            row.MinutesLabel.Bounds = new Rectangle(x, y, minutesLabelWidth, RowHeight);

            y += RowHeight;
        }

        _networkHealthCard.Height = y + _networkHealthCard.Inset;
    }

    /// <summary>
    /// Wraps a card's export/import buttons left-to-right, same wrap-on-overflow approach as
    /// <see cref="ChipGroup"/> - labels long enough to vary a lot by language, a fixed side-by-side
    /// row would overflow a narrower window. Shared by <see cref="_exportCard"/> (three data
    /// exports) and <see cref="_settingsBackupCard"/> (configuration export/import).
    /// </summary>
    private void LayoutExportButtons(Card card, List<AppButton> buttons)
    {
        Rectangle content = card.DisplayRectangle;
        int rowHeight = Dp(32);
        int x = content.Left;
        int y = content.Top;

        foreach (AppButton button in buttons)
        {
            if (x > content.Left && x + button.Width > content.Right)
            {
                x = content.Left;
                y += rowHeight + Sp(1);
            }

            button.Location = new Point(x, y);
            x += button.Width + Sp(2);
        }

        card.Height = y + rowHeight + card.Inset;
    }

    private void LayoutCards()
    {
        LayoutCardRows(_generalCard);
        LayoutCardRows(_updatesCard);
        LayoutCardRows(_cadenceCard);
        LayoutCardRows(_toolsCard);
        LayoutCardRows(_diagnosticsCard);
        LayoutCardRows(_networkHealthCard);
        LayoutConditionRows();
        LayoutCardRows(_integrationsCard);

        // LayoutCardRows sized the card to its three label/field rows; the directory buttons sit
        // below those, in the space the row layout does not know about - same pattern as the Kuma
        // extras just below.
        Rectangle diagnosticsContent = _diagnosticsCard.DisplayRectangle;
        int diagnosticsY = _diagnosticsCard.Height - _diagnosticsCard.Inset;
        _browseLogDirectory.Location = new Point(diagnosticsContent.Right - _browseLogDirectory.Width, diagnosticsY);
        _openLogFolder.Location = new Point(_browseLogDirectory.Left - Sp(2) - _openLogFolder.Width, diagnosticsY);
        _resetLogDirectory.Location = new Point(_openLogFolder.Left - Sp(2) - _resetLogDirectory.Width, diagnosticsY);
        diagnosticsY += Math.Max(_browseLogDirectory.Height, Math.Max(_openLogFolder.Height, _resetLogDirectory.Height));
        _diagnosticsCard.Height = diagnosticsY + _diagnosticsCard.Inset;

        // LayoutCardRows sized the card to its two label/field rows; the error label and the
        // "about" link sit below those, in the space the row layout does not know about.
        Rectangle integrationsContent = _integrationsCard.DisplayRectangle;
        int integrationsY = _integrationsCard.Height - _integrationsCard.Inset;
        _kumaError.Bounds = new Rectangle(integrationsContent.Left, integrationsY, integrationsContent.Width, Dp(16));
        integrationsY += Dp(18);
        _kumaAbout.Location = new Point(integrationsContent.Left, integrationsY);
        integrationsY += _kumaAbout.Height;
        _integrationsCard.Height = integrationsY + _integrationsCard.Inset;

        // Same "rows, then an error line below them" shape as the integrations card above.
        LayoutCardRows(_publicIpCard);
        Rectangle publicIpContent = _publicIpCard.DisplayRectangle;
        int publicIpY = _publicIpCard.Height - _publicIpCard.Inset;
        _publicIpError.Bounds = new Rectangle(publicIpContent.Left, publicIpY, publicIpContent.Width, Dp(16));
        _publicIpCard.Height = publicIpY + Dp(18) + _publicIpCard.Inset;

        Rectangle content = _endpointsCard.DisplayRectangle;
        _addEndpoint.Width = content.Width;
        _endpoints.Width = content.Width;

        int contentHeight = _addEndpoint.Height + Sp(2) + _endpoints.Height;
        _endpointsCard.Height = content.Top + contentHeight + _endpointsCard.Inset;

        LayoutExportButtons(_exportCard, _exportButtons);
        LayoutExportButtons(_settingsBackupCard, _settingsBackupButtons);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCards();
    }

    // ---- Loading / applying settings ------------------------------------------------------------

    private void LoadFromSettings()
    {
        AppSettings settings = Services.Settings;

        _language.SelectedIndexChanged -= OnLanguageChanged;
        SelectComboItem(_language, settings.Language);
        _language.SelectedIndexChanged += OnLanguageChanged;

        _theme.SelectedIndexChanged -= OnThemeSelectionChanged;
        PopulateThemeItems();
        SelectComboItem(_theme, settings.Theme.ToString());
        _theme.SelectedIndexChanged += OnThemeSelectionChanged;

        _autoStart.CheckedChanged -= OnAutoStartChanged;
        _autoStart.SetWithoutNotify(App.AutoStart.IsEnabled());
        _autoStart.CheckedChanged += OnAutoStartChanged;

        _showIncidentPopups.SetWithoutNotify(settings.ShowIncidentPopups);

        _checkForUpdates.SetWithoutNotify(settings.CheckForUpdates);
        _autoUpdateInstall.SetWithoutNotify(settings.AutoUpdateInstall);
        _autoUpdateInstall.Enabled = settings.CheckForUpdates;

        _foregroundInterval.Value = settings.ForegroundIntervalSeconds;
        _backgroundInterval.Value = settings.BackgroundIntervalSeconds;

        _tracerouteLookup.SetWithoutNotify(settings.TracerouteOwnerLookup);
        _lanScanVendorLookup.SetWithoutNotify(settings.LanScanVendorLookup);
        _lanScanTcpFallback.SetWithoutNotify(settings.LanScanTcpFallback);

        _speedTestAutoRun.SetWithoutNotify(settings.SpeedTestAutoRunEnabled);
        _speedTestInterval.Enabled = settings.SpeedTestAutoRunEnabled;
        _speedTestInterval.SelectedIndexChanged -= OnSpeedTestIntervalChanged;
        PopulateSpeedTestIntervalItems();
        SelectComboItem(_speedTestInterval, settings.SpeedTestIntervalHours.ToString());
        _speedTestInterval.SelectedIndexChanged += OnSpeedTestIntervalChanged;

        _logLevel.SelectedIndexChanged -= OnLogLevelChanged;
        PopulateLogLevelItems();
        SelectComboItem(_logLevel, settings.DiagnosticLogLevel.ToString());
        _logLevel.SelectedIndexChanged += OnLogLevelChanged;

        _logSuccessfulChecks.SetWithoutNotify(settings.LogSuccessfulChecks);
        _logDetailedMeasurements.SetWithoutNotify(settings.LogDetailedMeasurements);
        RefreshLogDirectoryText();

        _networkHealthEnabled.SetWithoutNotify(settings.NetworkHealthMonitoringEnabled);
        foreach (ConditionRow row in _conditionRows)
        {
            NetworkHealthThreshold threshold = GetConditionThreshold(row.Key);
            row.Toggle.SetWithoutNotify(threshold.Enabled);
            if (row.Threshold is not null)
            {
                row.Threshold.Value = Math.Clamp(threshold.MaxOccurrences, row.Threshold.Minimum, row.Threshold.Maximum);
            }

            row.Window.Value = Math.Clamp(threshold.WindowMinutes, row.Window.Minimum, row.Window.Maximum);
        }

        _publicIpTargets.Leave -= OnPublicAddressesChanged;
        _customDnsServers.Leave -= OnPublicAddressesChanged;
        _publicIpTargets.Text = string.Join(", ", settings.ParsedPublicIpTargets());
        _customDnsServers.Text = string.Join(", ", settings.ParsedCustomDnsServers());
        _publicIpError.Text = string.Empty;
        _publicIpTargets.Leave += OnPublicAddressesChanged;
        _customDnsServers.Leave += OnPublicAddressesChanged;

        _kumaUrl.Leave -= OnKumaSettingsChanged;
        _kumaInterval.ValueChanged -= OnKumaSettingsChanged;
        _kumaUrl.Text = settings.KumaPushUrl;
        _kumaInterval.Value = Math.Clamp(settings.KumaIntervalSeconds, KumaPusher.MinimumIntervalSeconds, 86400);
        _kumaUrl.Leave += OnKumaSettingsChanged;
        _kumaInterval.ValueChanged += OnKumaSettingsChanged;

        RefreshEndpoints();
    }

    private void PopulateThemeItems()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _theme.Items.Clear();
        _theme.Items.Add(new ComboItem(nameof(ThemeMode.System), loc.Get("settings.theme.system")));
        _theme.Items.Add(new ComboItem(nameof(ThemeMode.Light), loc.Get("settings.theme.light")));
        _theme.Items.Add(new ComboItem(nameof(ThemeMode.Dark), loc.Get("settings.theme.dark")));
    }

    private void PopulateLogLevelItems()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _logLevel.Items.Clear();
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Off), loc.Get("settings.diagnostics.level.off")));
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Basic), loc.Get("settings.diagnostics.level.basic")));
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Extended), loc.Get("settings.diagnostics.level.extended")));
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Full), loc.Get("settings.diagnostics.level.full")));
    }

    private void PopulateSpeedTestIntervalItems()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _speedTestInterval.Items.Clear();
        foreach (int hours in AppSettings.AllowedSpeedTestIntervalHours)
        {
            _speedTestInterval.Items.Add(new ComboItem(hours.ToString(), loc.Format("settings.speedTestInterval.option", hours)));
        }
    }

    private static void SelectComboItem(ComboBox box, string key)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboItem item && item.Key == key)
            {
                box.SelectedIndex = i;
                return;
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_language.SelectedItem is not ComboItem item)
        {
            return;
        }

        Services.Settings.Language = item.Key;
        Services.Settings.Save();

        // Raises LanguageChanged, which MainWindow already listens for and uses to re-localize
        // every page including this one - ApplyLocalization() below is what repopulates the theme
        // combo's translated labels and the "no endpoints yet" placeholder text.
        LocalizationManager.Instance.SetLanguage(item.Key);
    }

    private void OnThemeSelectionChanged(object? sender, EventArgs e)
    {
        if (_theme.SelectedItem is not ComboItem item || !Enum.TryParse(item.Key, out ThemeMode mode))
        {
            return;
        }

        Services.Settings.Theme = mode;
        Services.Settings.Save();
        Theme.Mode = mode;
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        bool wanted = _autoStart.Checked;
        bool applied = App.AutoStart.SetEnabled(wanted);

        Services.Settings.AutoStartWithWindows = applied && wanted;
        Services.Settings.Save();

        if (applied != true && wanted)
        {
            // The registry write failed (locked-down machine, say) - show the truth rather than a
            // toggle that claims success it could not deliver.
            _autoStart.CheckedChanged -= OnAutoStartChanged;
            _autoStart.SetWithoutNotify(false);
            _autoStart.CheckedChanged += OnAutoStartChanged;
        }
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_generalCard is null)
        {
            return;
        }

        _generalCard.Title = Loc.Get("settings.section.general");
        _updatesCard.Title = Loc.Get("settings.section.updates");
        _updatesCard.Subtitle = Loc.Get("settings.section.updates.subtitle");
        _cadenceCard.Title = Loc.Get("settings.section.cadence");
        _cadenceCard.Subtitle = Loc.Get("settings.section.cadence.subtitle");
        _toolsCard.Title = Loc.Get("settings.section.tools");
        _diagnosticsCard.Title = Loc.Get("settings.section.diagnostics");
        _diagnosticsCard.Subtitle = Loc.Get("settings.section.diagnostics.subtitle");
        _networkHealthCard.Title = Loc.Get("settings.section.networkHealth");
        _networkHealthCard.Subtitle = Loc.Get("settings.section.networkHealth.subtitle");
        _integrationsCard.Title = Loc.Get("settings.section.integrations");
        _integrationsCard.Subtitle = Loc.Get("settings.section.integrations.subtitle");
        _publicIpCard.Title = Loc.Get("settings.section.publicIp");
        _publicIpCard.Subtitle = Loc.Get("settings.section.publicIp.subtitle");
        _endpointsCard.Title = Loc.Get("settings.section.endpoints");
        _endpointsCard.Subtitle = Loc.Get("settings.section.endpoints.subtitle");
        _exportCard.Title = Loc.Get("settings.section.export");
        _exportCard.Subtitle = Loc.Get("settings.section.export.subtitle");
        _settingsBackupCard.Title = Loc.Get("settings.section.configBackup");
        _settingsBackupCard.Subtitle = Loc.Get("settings.section.configBackup.subtitle");

        foreach ((Label label, _) in _rows)
        {
            if (label.Tag is string key)
            {
                label.Text = Loc.Get(key);
            }
        }

        foreach (ConditionRow row in _conditionRows)
        {
            row.Name.Text = Loc.Get((string)row.Name.Tag!);
            row.IntervalLabel.Text = Loc.Get((string)row.IntervalLabel.Tag!);
            row.MinutesLabel.Text = Loc.Get((string)row.MinutesLabel.Tag!);
        }

        string previousLanguage = (_language.SelectedItem as ComboItem)?.Key ?? Services.Settings.Language;
        string previousTheme = (_theme.SelectedItem as ComboItem)?.Key ?? Services.Settings.Theme.ToString();
        string previousLogLevel = (_logLevel.SelectedItem as ComboItem)?.Key ?? Services.Settings.DiagnosticLogLevel.ToString();
        string previousSpeedTestInterval = (_speedTestInterval.SelectedItem as ComboItem)?.Key ?? Services.Settings.SpeedTestIntervalHours.ToString();

        PopulateThemeItems();
        SelectComboItem(_theme, previousTheme);
        SelectComboItem(_language, previousLanguage);

        PopulateLogLevelItems();
        SelectComboItem(_logLevel, previousLogLevel);

        PopulateSpeedTestIntervalItems();
        SelectComboItem(_speedTestInterval, previousSpeedTestInterval);

        _addEndpoint.Text = Loc.Get("settings.endpoint.add");
        _addEndpoint.SizeToContent();

        _exportAll.Text = Loc.Get("settings.export.all");
        _exportAll.SizeToContent();
        _exportAi.Text = Loc.Get("settings.export.ai");
        _exportAi.SizeToContent();
        _exportPlain.Text = Loc.Get("settings.export.plain");
        _exportPlain.SizeToContent();
        _exportSettings.Text = Loc.Get("settings.export.settings");
        _exportSettings.SizeToContent();
        _importSettings.Text = Loc.Get("settings.import.settings");
        _importSettings.SizeToContent();

        _browseLogDirectory.Text = Loc.Get("settings.diagnostics.browse");
        _browseLogDirectory.SizeToContent();
        _openLogFolder.Text = Loc.Get("settings.diagnostics.openFolder");
        _openLogFolder.SizeToContent();
        _resetLogDirectory.Text = Loc.Get("settings.diagnostics.resetDirectory");
        _resetLogDirectory.SizeToContent();

        _kumaAbout.Text = Loc.Get("settings.kuma.about");
        _kumaAbout.SizeToContent();

        RefreshEndpoints();
        ApplyControlColors();
        LayoutCards();
    }

    protected override void OnThemeChanged() => ApplyControlColors();

    private void ApplyControlColors()
    {
        Palette p = Theme.Current;

        foreach ((Label label, _) in _rows)
        {
            label.ForeColor = p.TextSecondary;
            label.BackColor = p.Surface;
        }

        foreach (ComboBox box in new[] { _language, _theme, _logLevel, _speedTestInterval })
        {
            box.BackColor = p.SurfaceAlt;
            box.ForeColor = p.TextPrimary;
            box.FlatStyle = FlatStyle.Flat;
        }

        foreach (NumericUpDown field in new[] { _foregroundInterval, _backgroundInterval, _kumaInterval })
        {
            field.BackColor = p.SurfaceAlt;
            field.ForeColor = p.TextPrimary;
        }

        foreach (ConditionRow row in _conditionRows)
        {
            row.Name.ForeColor = p.TextSecondary;
            row.Name.BackColor = p.Surface;
            row.IntervalLabel.ForeColor = p.TextSecondary;
            row.IntervalLabel.BackColor = p.Surface;
            row.MinutesLabel.ForeColor = p.TextSecondary;
            row.MinutesLabel.BackColor = p.Surface;

            if (row.Threshold is not null)
            {
                row.Threshold.BackColor = p.SurfaceAlt;
                row.Threshold.ForeColor = p.TextPrimary;
            }

            row.Window.BackColor = p.SurfaceAlt;
            row.Window.ForeColor = p.TextPrimary;
        }

        _logDirectory.BackColor = p.SurfaceAlt;
        _logDirectory.ForeColor = p.TextPrimary;

        _kumaUrl.BackColor = p.SurfaceAlt;
        _kumaUrl.ForeColor = p.TextPrimary;
        _kumaError.BackColor = p.Surface;
        _kumaError.ForeColor = p.Error;

        foreach (TextBox box in new[] { _publicIpTargets, _customDnsServers })
        {
            box.BackColor = p.SurfaceAlt;
            box.ForeColor = p.TextPrimary;
        }
        _publicIpError.BackColor = p.Surface;
        _publicIpError.ForeColor = p.Error;
    }

    /// <summary>A combo-box entry carrying both the stored key and the text shown to the user.</summary>
    private sealed record ComboItem(string Key, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>
    /// One network-health condition's row of controls. <see cref="Threshold"/> is null for
    /// rogue-DHCP, which has no occurrence threshold - see <see cref="MakeConditionRow"/>.
    /// </summary>
    private sealed record ConditionRow(
        string Key,
        Toggle Toggle,
        Label Name,
        NumericUpDown? Threshold,
        Label IntervalLabel,
        NumericUpDown Window,
        Label MinutesLabel);
}
