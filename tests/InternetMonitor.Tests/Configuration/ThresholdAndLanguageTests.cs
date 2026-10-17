using System.Globalization;
using InternetMonitor.Configuration;
using Xunit;

namespace InternetMonitor.Tests.Configuration;

public sealed class ThresholdAndLanguageTests
{
    [Theory]
    [InlineData("nl-NL", "nl")]
    [InlineData("de-AT", "de")]
    [InlineData("pl-PL", "pl")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR", "fr")]
    [InlineData("es-MX", "es")]
    [InlineData("it-IT", "it")]
    [InlineData("pt-BR", "pt")]
    [InlineData("ja-JP", "en")]
    public void A_first_start_speaks_the_windows_language_when_it_is_one_of_ours(string culture, string expected)
    {
        Assert.Equal(expected, AppSettings.LanguageFor(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Thresholds_are_clamped_and_the_warning_stays_below_the_error()
    {
        var settings = new AppSettings { LatencyWarningMs = 5, LatencyErrorMs = 999_999, WeakWifiSignalPercent = 200 };

        Assert.Equal(50, settings.LatencyWarningMs);
        Assert.Equal(10_000, settings.LatencyErrorMs);
        Assert.Equal(80, settings.WeakWifiSignalPercent);

        settings.LatencyWarningMs = 2000;
        settings.LatencyErrorMs = 1000;
        Assert.Equal((999, 1000), settings.LatencyThresholds);
    }

    [Fact]
    public void Defaults_match_what_the_checks_always_used()
    {
        var settings = new AppSettings();
        var targets = new CheckTargets();

        Assert.Equal(targets.LatencyWarningMs, settings.LatencyWarningMs);
        Assert.Equal(targets.LatencyErrorMs, settings.LatencyErrorMs);
        Assert.Equal(targets.WeakWifiSignalPercent, settings.WeakWifiSignalPercent);
        Assert.Equal(NotificationStyle.Popup, settings.NotificationStyle);
    }
}
