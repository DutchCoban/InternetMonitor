using InternetMonitor.Configuration;
using InternetMonitor.Ui.Controls;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>The settings page's network-changes section; see SettingsPage.cs for the fields and the shared layout.</summary>
public sealed partial class SettingsPage
{
    // ---- Network changes ------------------------------------------------------------------------

    private Toggle _incidentOnPathChange = null!;
    private Toggle _incidentOnApipa = null!;
    private Toggle _incidentOnNetworkChange = null!;
    private Toggle _incidentOnGatewayHardwareChange = null!;

    /// <summary>
    /// Four plain toggles, one per condition of <see cref="Core.Monitors.NetworkChangeMonitor"/>.
    /// No thresholds or windows: each is one incident per event, and the monitor reads the
    /// settings every cycle, so a change takes effect at the next one without a reconfigure.
    /// </summary>
    private void BuildNetworkChangesRows()
    {
        _incidentOnPathChange = MakeNetworkChangeToggle("settings.networkChanges.pathChanged", (s, on) => s.IncidentOnPathChange = on);
        _incidentOnApipa = MakeNetworkChangeToggle("settings.networkChanges.apipa", (s, on) => s.IncidentOnApipa = on);
        _incidentOnNetworkChange = MakeNetworkChangeToggle("settings.networkChanges.networkChanged", (s, on) => s.IncidentOnNetworkChange = on);
        _incidentOnGatewayHardwareChange = MakeNetworkChangeToggle("settings.networkChanges.gatewayHardware", (s, on) => s.IncidentOnGatewayHardwareChange = on);
    }

    private Toggle MakeNetworkChangeToggle(string labelKey, Action<AppSettings, bool> apply)
    {
        var toggle = new Toggle();
        toggle.CheckedChanged += (_, _) =>
        {
            apply(Services.Settings, toggle.Checked);
            Services.Settings.Save();
        };
        AddRow(_networkChangesCard, labelKey, toggle);
        return toggle;
    }

    private void LoadNetworkChangesFromSettings(AppSettings settings)
    {
        _incidentOnPathChange.SetWithoutNotify(settings.IncidentOnPathChange);
        _incidentOnApipa.SetWithoutNotify(settings.IncidentOnApipa);
        _incidentOnNetworkChange.SetWithoutNotify(settings.IncidentOnNetworkChange);
        _incidentOnGatewayHardwareChange.SetWithoutNotify(settings.IncidentOnGatewayHardwareChange);
    }
}
