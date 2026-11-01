using InternetMonitor.Configuration;
using Xunit;

namespace InternetMonitor.Tests.Configuration;

[Collection("Localization")]
public sealed class AppSettingsTests
{
    [Fact]
    public void History_retention_defaults_to_thirty_days()
    {
        Assert.Equal(30, new AppSettings().HistoryRetentionDays);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(60)]
    [InlineData(90)]
    public void History_retention_accepts_the_offered_choices(int days)
    {
        var settings = new AppSettings { HistoryRetentionDays = days };
        Assert.Equal(days, settings.HistoryRetentionDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(45)]
    [InlineData(365)]
    public void History_retention_ignores_anything_else(int days)
    {
        var settings = new AppSettings { HistoryRetentionDays = 60 };
        settings.HistoryRetentionDays = days;
        Assert.Equal(60, settings.HistoryRetentionDays);
    }

    [Fact]
    public void An_imported_file_cannot_set_values_that_would_freeze_or_redirect_the_app()
    {
        string path = Path.Combine(Path.GetTempPath(), $"im-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                { "MaxDiagnosticLogFiles": 2147483647, "MaxDiagnosticLogFileSizeBytes": 1,
                  "KumaIntervalSeconds": 999999999, "PacketLoss": { "Enabled": true, "MaxOccurrences": -5, "WindowMinutes": 100000000 },
                  "DiagnosticLogDirectory": "\\\\evil-host\\share\\logs" }
                """);
            AppSettings settings = AppSettings.ImportFrom(path)!;

            Assert.Equal(50, settings.MaxDiagnosticLogFiles);
            Assert.Equal(1024, settings.MaxDiagnosticLogFileSizeBytes);
            Assert.Equal(86_400, settings.KumaIntervalSeconds);
            Assert.Equal(new NetworkHealthThreshold(true, 0, 1440), settings.PacketLoss);
            Assert.Equal(string.Empty, settings.DiagnosticLogDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_log_folder_must_be_a_full_local_path()
    {
        string local = Path.GetTempPath();
        Assert.Equal(local, new AppSettings { DiagnosticLogDirectory = local }.DiagnosticLogDirectory);
        Assert.Equal(string.Empty, new AppSettings { DiagnosticLogDirectory = @"\\server\share" }.DiagnosticLogDirectory);
        Assert.Equal(string.Empty, new AppSettings { DiagnosticLogDirectory = "//server/share" }.DiagnosticLogDirectory);
        Assert.Equal(string.Empty, new AppSettings { DiagnosticLogDirectory = @"logs\here" }.DiagnosticLogDirectory);
        Assert.Equal(string.Empty, new AppSettings { DiagnosticLogDirectory = string.Empty }.DiagnosticLogDirectory);
    }

    [Theory]
    [InlineData("nl-NL", "nl")]
    [InlineData("pt-BR", "pt")]
    [InlineData("de", "de")]
    [InlineData("xx-YY", "en")]
    [InlineData("", "en")]
    public void A_windows_language_tag_maps_to_a_supported_language_or_english(string tag, string expected)
    {
        Assert.Equal(expected, AppSettings.LanguageFor(tag));
    }

    [Fact]
    public void The_default_language_is_english()
    {
        Assert.Equal("en", new AppSettings().Language);
    }

    [Fact]
    public void The_reachability_url_and_the_speed_test_server_accept_only_https_addresses()
    {
        var settings = new AppSettings();
        Assert.Equal(AppSettings.DefaultReachabilityUrl, settings.ReachabilityUrl);
        Assert.Equal(string.Empty, settings.SpeedTestServerUrl);
        Assert.Equal(AppSettings.DefaultSpeedTestServers, settings.SpeedTestServers);

        settings.ReachabilityUrl = " https://check.example/generate_204 ";
        Assert.Equal("https://check.example/generate_204", settings.ReachabilityUrl);
        foreach (string bad in new[] { "http://check.example/204", "https://user:pw@check.example/204", "not a url", "", "   " })
        {
            settings.ReachabilityUrl = bad;
            Assert.Equal(AppSettings.DefaultReachabilityUrl, settings.ReachabilityUrl);
        }

        settings.SpeedTestServerUrl = "https://speed.example/backend/";
        Assert.Equal("https://speed.example/backend", settings.SpeedTestServerUrl);
        Assert.Equal(["https://speed.example/backend"], settings.SpeedTestServers);
        settings.SpeedTestServerUrl = "ftp://speed.example";
        Assert.Equal(string.Empty, settings.SpeedTestServerUrl);
    }

    [Fact]
    public void The_defaults_of_releases_before_2_8_are_moved_on_load_and_a_users_own_values_are_not()
    {
        string path = Path.Combine(Path.GetTempPath(), $"im-settings-{Guid.NewGuid():N}.json");
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.SettingsFile, """{ "PublicIpTargets": ["9.9.9.9", "8.8.8.8", "1.1.1.1"], "NtpServer": "pool.ntp.org" }""");
            AppSettings moved = AppSettings.Load();
            Assert.Equal(AppSettings.DefaultPublicIpTargets, moved.PublicIpTargets);
            Assert.Equal(AppSettings.DefaultNtpServer, moved.NtpServer);

            File.WriteAllText(AppPaths.SettingsFile, """{ "PublicIpTargets": ["9.9.9.9", "8.8.8.8"], "NtpServer": "time.example" }""");
            AppSettings kept = AppSettings.Load();
            Assert.Equal(["9.9.9.9", "8.8.8.8"], kept.PublicIpTargets);
            Assert.Equal("time.example", kept.NtpServer);
        }
        finally
        {
            File.Delete(AppPaths.SettingsFile);
            File.Delete(path);
        }
    }

    [Fact]
    public void A_hand_edited_retention_outside_the_choices_loads_as_the_default()
    {
        string path = Path.Combine(Path.GetTempPath(), $"im-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "HistoryRetentionDays": 1000, "ReportPeriodDays": 90 }""");
            AppSettings settings = AppSettings.ImportFrom(path)!;

            Assert.Equal(30, settings.HistoryRetentionDays);
            Assert.Equal(90, settings.ReportPeriodDays);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
