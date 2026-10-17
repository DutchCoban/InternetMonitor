using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>The settings page's endpoints, configurationbackup section(s); see SettingsPage.cs for the fields and the shared layout.</summary>
public sealed partial class SettingsPage
{
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

    /// <summary>
    /// Adds or edits one endpoint (matched by id) and saves. The checks pick it up on the next cycle,
    /// since the pipeline rebuilds its targets from the settings every cycle.
    /// </summary>
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

    // ---- Configuration backup -------------------------------------------------------------------

    /// <summary>
    /// The configuration backup/restore pair. Data exports live on the Reports page - see the
    /// comment on <see cref="Build"/>.
    /// </summary>
    private void BuildSettingsBackupSection()
    {
        _exportSettings = AddActionButton(_settingsBackupCard, _settingsBackupButtons, ExportSettings);
        _importSettings = AddActionButton(_settingsBackupCard, _settingsBackupButtons, ImportSettings);

        // The export is deliberately unredacted (see ExportSettings), which is worth one line of
        // warning next to the button rather than a dialog in the way of every export.
        _settingsBackupNote = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            Font = Theme.Font(FontRole.Small),
        };
        _settingsBackupCard.Controls.Add(_settingsBackupNote);
    }

    /// <summary>
    /// Unlike the data exports on the Reports page, this one is deliberately unredacted - confirmed
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
}
