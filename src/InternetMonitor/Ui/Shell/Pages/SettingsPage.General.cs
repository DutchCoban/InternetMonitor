using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>The settings page's general, updates, cadence, tools section(s); see SettingsPage.cs for the fields and the shared layout.</summary>
public sealed partial class SettingsPage
{
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

        _historyRetention = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _historyRetention.SelectedIndexChanged += OnHistoryRetentionChanged;
        AddRow(_cadenceCard, "settings.historyRetention", _historyRetention);
    }

    private void OnHistoryRetentionChanged(object? sender, EventArgs e)
    {
        if (_historyRetention.SelectedItem is not ComboItem item || !int.TryParse(item.Key, out int days))
        {
            return;
        }

        Services.Settings.HistoryRetentionDays = days;
        Services.Settings.Save();

        // Takes effect at the next background prune; a shorter period drops the oldest points
        // there and from the files on the next start.
        Services.History.SetRetention(TimeSpan.FromDays(days));
    }

    private void PopulateHistoryRetentionItems()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _historyRetention.Items.Clear();
        foreach (int days in AppSettings.AllowedHistoryRetentionDays)
        {
            _historyRetention.Items.Add(new ComboItem(days.ToString(), loc.Format("settings.historyRetention.option", days)));
        }
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
            _speedTestOnMetered.Enabled = _speedTestAutoRun.Checked;
        };
        AddRow(_toolsCard, "settings.speedTestAutoRun", _speedTestAutoRun);

        _speedTestInterval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _speedTestInterval.SelectedIndexChanged += OnSpeedTestIntervalChanged;
        AddRow(_toolsCard, "settings.speedTestInterval", _speedTestInterval);

        _speedTestOnMetered = new Toggle();
        _speedTestOnMetered.CheckedChanged += (_, _) =>
        {
            Services.Settings.SpeedTestOnMetered = _speedTestOnMetered.Checked;
            Services.Settings.Save();
        };
        AddRow(_toolsCard, "settings.speedTestOnMetered", _speedTestOnMetered);
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
}
