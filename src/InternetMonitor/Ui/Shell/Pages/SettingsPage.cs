using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Monitors;
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
public sealed partial class SettingsPage(AppServices services) : PageBase(services)
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
    private Card _settingsBackupCard = null!;

    private ComboBox _language = null!;
    private ComboBox _theme = null!;
    private Toggle _autoStart = null!;
    private Toggle _showIncidentPopups = null!;
    private ComboBox _notificationStyle = null!;
    private NumericUpDown _latencyWarning = null!;
    private NumericUpDown _latencyError = null!;
    private NumericUpDown _weakWifi = null!;
    private Label _kumaStatus = null!;
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
    private ComboBox _historyRetention = null!;
    private Toggle _speedTestOnMetered = null!;
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
    private Label _settingsBackupNote = null!;
    private AppButton _kumaAbout = null!;
    private EndpointList _endpoints = null!;
    private AppButton _addEndpoint = null!;
    private AppButton _exportSettings = null!;
    private AppButton _importSettings = null!;
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

        _settingsBackupCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Copy };
        BuildSettingsBackupSection();

        // Reverse visual order: DockStyle.Top stacks later siblings nearer the top edge. The
        // configuration backup card is added first - and so ends up lowest - because it is an
        // occasional action, not something that belongs among the frequently-changed settings
        // above it. Exporting the monitor's *data* lives on the Reports page; this card only
        // backs up and restores its *configuration*.
        Controls.Add(_settingsBackupCard);
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

        CloseCard(card, y);
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
        int y = BelowRows(_networkHealthCard);

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

        CloseCard(_networkHealthCard, y);
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
        int diagnosticsY = BelowRows(_diagnosticsCard);
        _browseLogDirectory.Location = new Point(diagnosticsContent.Right - _browseLogDirectory.Width, diagnosticsY);
        _openLogFolder.Location = new Point(_browseLogDirectory.Left - Sp(2) - _openLogFolder.Width, diagnosticsY);
        _resetLogDirectory.Location = new Point(_openLogFolder.Left - Sp(2) - _resetLogDirectory.Width, diagnosticsY);
        diagnosticsY += Math.Max(_browseLogDirectory.Height, Math.Max(_openLogFolder.Height, _resetLogDirectory.Height));
        CloseCard(_diagnosticsCard, diagnosticsY);

        // LayoutCardRows sized the card to its two label/field rows; the error label and the
        // "about" link sit below those, in the space the row layout does not know about.
        Rectangle integrationsContent = _integrationsCard.DisplayRectangle;
        int integrationsY = BelowRows(_integrationsCard);
        _kumaError.Bounds = new Rectangle(integrationsContent.Left, integrationsY, integrationsContent.Width, Dp(16));
        integrationsY += Dp(18);
        _kumaStatus.Bounds = new Rectangle(integrationsContent.Left, integrationsY, integrationsContent.Width, Dp(16));
        integrationsY += Dp(18);
        _kumaAbout.Location = new Point(integrationsContent.Left, integrationsY);
        integrationsY += _kumaAbout.Height;
        CloseCard(_integrationsCard, integrationsY);

        // Same "rows, then an error line below them" shape as the integrations card above.
        LayoutCardRows(_publicIpCard);
        Rectangle publicIpContent = _publicIpCard.DisplayRectangle;
        int publicIpY = BelowRows(_publicIpCard);
        _publicIpError.Bounds = new Rectangle(publicIpContent.Left, publicIpY, publicIpContent.Width, Dp(16));
        CloseCard(_publicIpCard, publicIpY + Dp(18));

        Rectangle content = _endpointsCard.DisplayRectangle;
        _addEndpoint.Width = content.Width;
        _endpoints.Width = content.Width;

        int contentHeight = _addEndpoint.Height + Sp(2) + _endpoints.Height;
        _endpointsCard.Height = content.Top + contentHeight + _endpointsCard.Inset;

        LayoutButtonRow(_settingsBackupCard, _settingsBackupButtons);

        // The note goes under the buttons the row layout just placed, same shape as the Kuma extras.
        Rectangle backupContent = _settingsBackupCard.DisplayRectangle;
        int backupY = BelowRows(_settingsBackupCard) + Sp(1);
        _settingsBackupNote.Bounds = new Rectangle(backupContent.Left, backupY, backupContent.Width, Dp(32));
        CloseCard(_settingsBackupCard, backupY + Dp(32));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCards();
    }

    // ---- Loading / applying settings ------------------------------------------------------------

    /// <summary>
    /// Fills every control from the current settings. Each control's change handler is unhooked while
    /// it is set (or the toggle is set without notifying), so loading never writes the settings back,
    /// re-applies them, or - for the language - triggers a full re-localisation.
    /// </summary>
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

        _notificationStyle.SelectedIndexChanged -= OnNotificationStyleChanged;
        PopulateNotificationStyleItems();
        SelectComboItem(_notificationStyle, settings.NotificationStyle.ToString());
        _notificationStyle.SelectedIndexChanged += OnNotificationStyleChanged;

        _checkForUpdates.SetWithoutNotify(settings.CheckForUpdates);
        _autoUpdateInstall.SetWithoutNotify(settings.AutoUpdateInstall);
        _autoUpdateInstall.Enabled = settings.CheckForUpdates;

        _foregroundInterval.Value = settings.ForegroundIntervalSeconds;
        _backgroundInterval.Value = settings.BackgroundIntervalSeconds;
        _latencyWarning.Value = settings.LatencyWarningMs;
        _latencyError.Value = settings.LatencyErrorMs;
        _weakWifi.Value = settings.WeakWifiSignalPercent;
        RefreshKumaStatus(Services.Kuma.LastPush);

        _historyRetention.SelectedIndexChanged -= OnHistoryRetentionChanged;
        PopulateHistoryRetentionItems();
        SelectComboItem(_historyRetention, settings.HistoryRetentionDays.ToString());
        _historyRetention.SelectedIndexChanged += OnHistoryRetentionChanged;

        _tracerouteLookup.SetWithoutNotify(settings.TracerouteOwnerLookup);
        _lanScanVendorLookup.SetWithoutNotify(settings.LanScanVendorLookup);
        _lanScanTcpFallback.SetWithoutNotify(settings.LanScanTcpFallback);

        _speedTestAutoRun.SetWithoutNotify(settings.SpeedTestAutoRunEnabled);
        _speedTestInterval.Enabled = settings.SpeedTestAutoRunEnabled;
        _speedTestOnMetered.SetWithoutNotify(settings.SpeedTestOnMetered);
        _speedTestOnMetered.Enabled = settings.SpeedTestAutoRunEnabled;
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

    private void PopulateNotificationStyleItems()
    {
        _notificationStyle.Items.Clear();
        foreach (NotificationStyle style in Enum.GetValues<NotificationStyle>())
        {
            _notificationStyle.Items.Add(new ComboItem(style.ToString(), Loc.Get($"settings.notificationStyle.{style.ToString().ToLowerInvariant()}")));
        }
    }

    private void OnNotificationStyleChanged(object? sender, EventArgs e)
    {
        if (_notificationStyle.SelectedItem is ComboItem item && Enum.TryParse(item.Key, out NotificationStyle style))
        {
            Services.Settings.NotificationStyle = style;
            Services.Settings.Save();
        }
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

    /// <summary>Selects the item with this key, or the first item when the stored value is no longer offered.</summary>
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
        _settingsBackupCard.Title = Loc.Get("settings.section.configBackup");
        _settingsBackupCard.Subtitle = Loc.Get("settings.section.configBackup.subtitle");
        _settingsBackupNote.Text = Loc.Get("settings.backup.note");
        RefreshKumaStatus(Services.Kuma.LastPush);

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
        string previousRetention = (_historyRetention.SelectedItem as ComboItem)?.Key ?? Services.Settings.HistoryRetentionDays.ToString();

        PopulateThemeItems();
        SelectComboItem(_theme, previousTheme);
        SelectComboItem(_language, previousLanguage);

        PopulateLogLevelItems();
        SelectComboItem(_logLevel, previousLogLevel);

        PopulateSpeedTestIntervalItems();
        SelectComboItem(_speedTestInterval, previousSpeedTestInterval);

        _historyRetention.SelectedIndexChanged -= OnHistoryRetentionChanged;
        PopulateHistoryRetentionItems();
        SelectComboItem(_historyRetention, previousRetention);
        _historyRetention.SelectedIndexChanged += OnHistoryRetentionChanged;

        _addEndpoint.Text = Loc.Get("settings.endpoint.add");
        _addEndpoint.SizeToContent();

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

        foreach (ComboBox box in new[] { _language, _theme, _notificationStyle, _logLevel, _speedTestInterval, _historyRetention })
        {
            box.BackColor = p.SurfaceAlt;
            box.ForeColor = p.TextPrimary;
            box.FlatStyle = FlatStyle.Flat;
        }

        foreach (NumericUpDown field in new[] { _foregroundInterval, _backgroundInterval, _latencyWarning, _latencyError, _weakWifi, _kumaInterval })
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
        _kumaStatus.BackColor = p.Surface;
        _kumaStatus.ForeColor = p.TextMuted;
        _settingsBackupNote.BackColor = p.Surface;
        _settingsBackupNote.ForeColor = p.TextMuted;

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
