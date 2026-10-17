using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Ui.Shell.Pages;
using Xunit;

namespace InternetMonitor.Tests.Ui;

/// <summary>
/// A setting is asked about only while another makes it matter, and the log level and its two
/// switches stay one consistent choice - the page's decisions, tested without a window.
/// </summary>
public sealed class SettingsRulesTests
{
    [Fact]
    public void With_everything_off_only_the_always_present_rows_remain()
    {
        var s = new AppSettings { ShowIncidentPopups = false, CheckForUpdates = false };

        SettingsRules.DependentRows rows = SettingsRules.RowsFor(s);

        Assert.Equal(new SettingsRules.DependentRows(false, false, false, false, false, false, false, false), rows);
    }

    [Fact]
    public void Each_dependent_row_follows_its_own_switch()
    {
        var s = new AppSettings
        {
            AdapterMode = AdapterMode.Preferred,
            ShowIncidentPopups = true,
            CheckForUpdates = true,
            SpeedTestAutoRunEnabled = true,
            DiagnosticLogLevel = DiagnosticLogLevel.Extended,
            KumaPushUrl = "https://kuma.example/api/push/x",
            WebhookUrl = "https://hooks.example/incident",
        };

        Assert.Equal(new SettingsRules.DependentRows(true, true, true, true, true, true, true, true), SettingsRules.RowsFor(s));

        s.AdapterMode = AdapterMode.Automatic;
        Assert.False(SettingsRules.RowsFor(s).AdapterChoice);
        s.KumaPushUrl = string.Empty;
        Assert.False(SettingsRules.RowsFor(s).Kuma);
    }

    [Theory]
    [InlineData(DiagnosticLogLevel.Off, false, false)]
    [InlineData(DiagnosticLogLevel.Basic, false, true)]
    [InlineData(DiagnosticLogLevel.Extended, true, true)]
    [InlineData(DiagnosticLogLevel.Full, false, true)]
    public void The_log_switches_show_only_at_extended_and_the_folder_whenever_logging_is_on(DiagnosticLogLevel level, bool switches, bool folder)
    {
        SettingsRules.DependentRows rows = SettingsRules.RowsFor(new AppSettings { DiagnosticLogLevel = level });

        Assert.Equal(switches, rows.LogSwitches);
        Assert.Equal(folder, rows.LogFolder);
    }

    [Fact]
    public void Choosing_full_turns_both_switches_on_and_stepping_back_to_extended_turns_them_off()
    {
        Assert.Equal(
            new SettingsRules.LogChoice(DiagnosticLogLevel.Full, true, true),
            SettingsRules.AfterLevelChosen(DiagnosticLogLevel.Extended, DiagnosticLogLevel.Full, false, false));

        Assert.Equal(
            new SettingsRules.LogChoice(DiagnosticLogLevel.Extended, false, false),
            SettingsRules.AfterLevelChosen(DiagnosticLogLevel.Full, DiagnosticLogLevel.Extended, true, true));

        // Any other move keeps the switches as they were.
        Assert.Equal(
            new SettingsRules.LogChoice(DiagnosticLogLevel.Basic, true, false),
            SettingsRules.AfterLevelChosen(DiagnosticLogLevel.Extended, DiagnosticLogLevel.Basic, true, false));
    }

    [Fact]
    public void Both_switches_on_at_extended_is_full_and_one_switch_is_not()
    {
        Assert.Equal(
            new SettingsRules.LogChoice(DiagnosticLogLevel.Full, true, true),
            SettingsRules.AfterSwitchFlipped(DiagnosticLogLevel.Extended, true, true));

        Assert.Equal(
            new SettingsRules.LogChoice(DiagnosticLogLevel.Extended, true, false),
            SettingsRules.AfterSwitchFlipped(DiagnosticLogLevel.Extended, true, false));

        // At another level the switches do not promote anything.
        Assert.Equal(
            new SettingsRules.LogChoice(DiagnosticLogLevel.Basic, true, true),
            SettingsRules.AfterSwitchFlipped(DiagnosticLogLevel.Basic, true, true));
    }

    [Fact]
    public void Only_the_conditions_that_are_on_are_shown_on_the_diagnostics_page()
    {
        var s = new AppSettings { PacketLoss = new NetworkHealthThreshold(true, 20, 5) };

        Assert.True(SettingsRules.ConditionShown(s, "packetLoss"));
        Assert.False(SettingsRules.ConditionShown(s, "tcpRetransmissions"));
        Assert.False(SettingsRules.ConditionShown(s, "no-such-condition"));
    }
}
