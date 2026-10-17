using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Reports;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.Reports;

[Collection("Localization")]
public sealed class ReportComparisonTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(7);

    private static ProviderReportData Period(DateTimeOffset from, DateTimeOffset to, double availability, int outages, double latencyMs) => new(
        from,
        to,
        to - from,
        availability,
        [.. Enumerable.Range(0, outages).Select(i => new OutageEntry(from.AddHours(i + 1), TimeSpan.FromMinutes(10), RootCause.Internet, OutageSide.Provider, false))],
        [],
        [new DayLatency(DateOnly.FromDateTime(from.UtcDateTime), latencyMs, latencyMs * 2, 100)],
        [new SpeedTestEntry(from.AddHours(2), 150, 30, 12, null)]);

    private static ProviderReportOptions Options => new(200, 40, "2.3.0", To, TimeZoneInfo.Utc);

    [Fact]
    public void Comparison_windows_sit_where_the_setting_says()
    {
        Assert.Equal((From.AddDays(-7), From), ProviderReportHtml.ComparisonWindow(From, To, ReportComparison.PreviousPeriod));
        Assert.Equal((From.AddDays(-7), To.AddDays(-7)), ProviderReportHtml.ComparisonWindow(From, To, ReportComparison.WeekEarlier));
        Assert.Equal((From.AddMonths(-1), To.AddMonths(-1)), ProviderReportHtml.ComparisonWindow(From, To, ReportComparison.MonthEarlier));
        Assert.Null(ProviderReportHtml.ComparisonWindow(From, To, ReportComparison.None));
    }

    [Fact]
    public void A_comparison_section_shows_both_periods_and_colours_the_change()
    {
        ProviderReportData now = Period(From, To, 0.995, 2, 20);
        ProviderReportData then = Period(From.AddDays(-7), From, 0.990, 4, 30);

        string html = ProviderReportHtml.Render(now, Options, LocalizationManager.Instance, then, ReportComparison.PreviousPeriod);

        Assert.Contains("Compared with the 7 days before", html);
        Assert.Contains("99.50%", html);
        Assert.Contains("99.00%", html);
        Assert.Contains("class=\"num better\">▲ 0.50 pp", html);
        Assert.Contains("class=\"num better\">▼ 2", html);
        Assert.Contains("class=\"num better\">▼ 10 ms", html);
    }

    [Fact]
    public void Without_a_comparison_the_report_is_as_before()
    {
        string html = ProviderReportHtml.Render(Period(From, To, 0.995, 2, 20), Options, LocalizationManager.Instance);

        Assert.DoesNotContain("Compared with", html);
    }

    [Fact]
    public void An_earlier_period_with_almost_nothing_measured_says_so_instead_of_comparing()
    {
        ProviderReportData then = Period(From.AddDays(-7), From, 0.5, 1, 30) with { Monitored = TimeSpan.FromMinutes(5) };

        string html = ProviderReportHtml.Render(Period(From, To, 0.995, 2, 20), Options, LocalizationManager.Instance, then, ReportComparison.PreviousPeriod);

        Assert.Contains("Too little was measured", html);
        Assert.DoesNotContain("<td class=\"num better\">", html);
    }

    [Fact]
    public void Any_number_of_days_up_to_a_year_is_a_valid_report_period()
    {
        var settings = new AppSettings { ReportPeriodDays = 2 };
        Assert.Equal(2, settings.ReportPeriodDays);

        settings.ReportPeriodDays = 0;
        Assert.Equal(1, settings.ReportPeriodDays);

        settings.ReportPeriodDays = 1000;
        Assert.Equal(365, settings.ReportPeriodDays);
        Assert.Equal(ReportComparison.None, settings.ReportComparison);
    }
}
