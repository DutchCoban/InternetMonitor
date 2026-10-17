using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>The settings page's diagnostics section(s); see SettingsPage.cs for the fields and the shared layout.</summary>
public sealed partial class SettingsPage
{
    // ---- Diagnostics --------------------------------------------------------------------------

    private void BuildDiagnosticsRows()
    {
        _logLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _logLevel.SelectedIndexChanged += OnLogLevelChanged;
        AddRow(_diagnosticsCard, "settings.diagnostics.level", _logLevel);

        _logSuccessfulChecks = new Toggle();
        _logSuccessfulChecks.CheckedChanged += (_, _) => OnLogSwitchChanged();
        AddRow(_diagnosticsCard, "settings.diagnostics.logSuccesses", _logSuccessfulChecks);

        _logDetailedMeasurements = new Toggle();
        _logDetailedMeasurements.CheckedChanged += (_, _) => OnLogSwitchChanged();
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

        // Full is Extended with both switches on, and the page says so both ways: choosing Full
        // turns them on, stepping back from Full to Extended turns them off again - otherwise the
        // two switches would promote the level straight back to Full.
        AppSettings settings = Services.Settings;
        bool wasFull = settings.DiagnosticLogLevel == DiagnosticLogLevel.Full;
        settings.DiagnosticLogLevel = level;
        if (level == DiagnosticLogLevel.Full || (wasFull && level == DiagnosticLogLevel.Extended))
        {
            bool on = level == DiagnosticLogLevel.Full;
            settings.LogSuccessfulChecks = on;
            settings.LogDetailedMeasurements = on;
            _logSuccessfulChecks.SetWithoutNotify(on);
            _logDetailedMeasurements.SetWithoutNotify(on);
        }

        settings.Save();
        RefreshDependentRows();
    }

    /// <summary>A log switch flipped: Extended with both on is exactly Full, so the level follows.</summary>
    private void OnLogSwitchChanged()
    {
        AppSettings settings = Services.Settings;
        settings.LogSuccessfulChecks = _logSuccessfulChecks.Checked;
        settings.LogDetailedMeasurements = _logDetailedMeasurements.Checked;

        if (settings.DiagnosticLogLevel == DiagnosticLogLevel.Extended && settings.LogSuccessfulChecks && settings.LogDetailedMeasurements)
        {
            settings.DiagnosticLogLevel = DiagnosticLogLevel.Full;
            _logLevel.SelectedIndexChanged -= OnLogLevelChanged;
            SelectComboItem(_logLevel, nameof(DiagnosticLogLevel.Full));
            _logLevel.SelectedIndexChanged += OnLogLevelChanged;
        }

        settings.Save();
        RefreshDependentRows();
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

    // The folder may not exist yet (nothing has logged there) - not worth surfacing for a
    // convenience shortcut the user can open themselves via Explorer.
    private void OpenLogFolder() => ShellOpen.OpenPath(Services.DiagnosticLog.ResolvedDirectory());

    private void RefreshLogDirectoryText() => _logDirectory.Text = Services.DiagnosticLog.ResolvedDirectory();
}
