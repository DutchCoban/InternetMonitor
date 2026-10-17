using System.Globalization;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using Xunit;

namespace InternetMonitor.Tests.Configuration;

/// <summary>
/// settings.json survives a restart with every value, ignores what it no longer knows (an older
/// version's keys), and never takes the app down when it is damaged or missing.
/// </summary>
[Collection("DataDir")]
public sealed class AppSettingsStorageTests
{
    [Fact]
    public void Saved_settings_load_back_with_enums_written_as_names()
    {
        var settings = new AppSettings
        {
            Language = "de",
            AiExportDetail = AiExportDetail.Compact,
            ReportComparison = ReportComparison.WeekEarlier,
            DiagnosticLogLevel = DiagnosticLogLevel.Extended,
            LogDetailedMeasurements = true,
            WebhookUrl = "https://hooks.example/incident",
            WebhookLabel = "Balie Utrecht",
            PacketLoss = new NetworkHealthThreshold(true, 15, 10),
            HistoryRetentionDays = 90,
        };

        Assert.True(settings.Save());
        AppSettings loaded = AppSettings.Load();

        Assert.Equal("de", loaded.Language);
        Assert.Equal(AiExportDetail.Compact, loaded.AiExportDetail);
        Assert.Equal(ReportComparison.WeekEarlier, loaded.ReportComparison);
        Assert.Equal(DiagnosticLogLevel.Extended, loaded.DiagnosticLogLevel);
        Assert.True(loaded.LogDetailedMeasurements);
        Assert.Equal("https://hooks.example/incident", loaded.WebhookUrl);
        Assert.Equal("Balie Utrecht", loaded.WebhookLabel);
        Assert.Equal(new NetworkHealthThreshold(true, 15, 10), loaded.PacketLoss);
        Assert.True(loaded.NetworkHealthMonitoringEnabled);
        Assert.Equal(90, loaded.HistoryRetentionDays);

        string json = File.ReadAllText(AppPaths.SettingsFile);
        Assert.Contains("\"Compact\"", json);
        Assert.DoesNotContain("NetworkHealthMonitoringEnabled", json);
        Assert.DoesNotContain("EnabledNetworkHealthConditions", json);
    }

    [Fact]
    public void Keys_from_an_older_version_are_ignored_rather_than_refused()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, """
            { "Language": "pl", "WidgetEnabled": true, "WidgetTopMost": true, "WidgetX": 12,
              "NetworkHealthMonitoringEnabled": true, "SomethingFromTheFuture": { "a": 1 } }
            """);

        AppSettings loaded = AppSettings.Load();

        Assert.Equal("pl", loaded.Language);
        // The old master switch is gone: with every condition off, nothing is monitored.
        Assert.False(loaded.NetworkHealthMonitoringEnabled);
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults_and_a_missing_one_speaks_windows_language()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, "{ \"Language\": ");
        Assert.Equal(new AppSettings().Language, AppSettings.Load().Language);

        File.Delete(AppPaths.SettingsFile);
        Assert.Equal(AppSettings.LanguageFor(CultureInfo.CurrentUICulture), AppSettings.Load().Language);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_behind()
    {
        Assert.True(new AppSettings().Save());

        Assert.True(File.Exists(AppPaths.SettingsFile));
        Assert.False(File.Exists(AppPaths.SettingsFile + ".tmp"));
    }
}
