using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>The settings page's networkhealth section(s); see SettingsPage.cs for the fields and the shared layout.</summary>
public sealed partial class SettingsPage
{
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
}
