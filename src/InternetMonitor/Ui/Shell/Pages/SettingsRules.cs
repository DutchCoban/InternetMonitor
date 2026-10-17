using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// The decisions the Settings and Diagnostics pages make about what to show and how one setting
/// follows another, as pure functions over <see cref="AppSettings"/> - so they can be tested
/// without a window, and so a page only places controls and never reasons about them.
/// </summary>
public static class SettingsRules
{
    /// <summary>Which dependent rows are shown: a setting is asked about only while another makes it matter.</summary>
    public sealed record DependentRows(
        bool AdapterChoice,
        bool NotificationStyle,
        bool AutoUpdateInstall,
        bool SpeedTestOptions,
        bool LogSwitches,
        bool LogFolder,
        bool Kuma,
        bool Webhook);

    public static DependentRows RowsFor(AppSettings s) => new(
        AdapterChoice: s.AdapterMode != AdapterMode.Automatic,
        NotificationStyle: s.ShowIncidentPopups,
        AutoUpdateInstall: s.CheckForUpdates,
        SpeedTestOptions: s.SpeedTestAutoRunEnabled,
        LogSwitches: s.DiagnosticLogLevel == DiagnosticLogLevel.Extended,
        LogFolder: s.DiagnosticLogLevel != DiagnosticLogLevel.Off,
        Kuma: s.KumaPushUrl.Length > 0,
        Webhook: s.WebhookUrl.Length > 0);

    /// <summary>The log level and its two switches, which are one setting seen two ways: Full is Extended with both on.</summary>
    public sealed record LogChoice(DiagnosticLogLevel Level, bool Successes, bool Details);

    /// <summary>
    /// The user picked a level. Full turns both switches on; stepping back from Full to Extended turns
    /// them off, or the switches would promote the level straight back to Full. Any other change
    /// leaves the switches as they were.
    /// </summary>
    public static LogChoice AfterLevelChosen(DiagnosticLogLevel previous, DiagnosticLogLevel chosen, bool successes, bool details)
    {
        if (chosen == DiagnosticLogLevel.Full)
        {
            return new LogChoice(chosen, true, true);
        }

        if (previous == DiagnosticLogLevel.Full && chosen == DiagnosticLogLevel.Extended)
        {
            return new LogChoice(chosen, false, false);
        }

        return new LogChoice(chosen, successes, details);
    }

    /// <summary>The user flipped a switch. At Extended with both on, that is exactly Full, so the level follows.</summary>
    public static LogChoice AfterSwitchFlipped(DiagnosticLogLevel level, bool successes, bool details) =>
        level == DiagnosticLogLevel.Extended && successes && details
            ? new LogChoice(DiagnosticLogLevel.Full, true, true)
            : new LogChoice(level, successes, details);

    /// <summary>Whether a network-health condition row is shown on the Diagnostics page: only the conditions that are on.</summary>
    public static bool ConditionShown(AppSettings s, string conditionKey) => s.EnabledNetworkHealthConditions.Contains(conditionKey);
}
