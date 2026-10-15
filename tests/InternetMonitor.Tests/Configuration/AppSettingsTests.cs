using InternetMonitor.Configuration;
using Xunit;

namespace InternetMonitor.Tests.Configuration;

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
