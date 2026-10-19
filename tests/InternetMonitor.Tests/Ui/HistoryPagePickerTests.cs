using InternetMonitor.Configuration;
using InternetMonitor.Core.History;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Shell.Pages;
using Xunit;

namespace InternetMonitor.Tests.Ui;

/// <summary>
/// The History page's two-row picker, driven through the real chip controls: a group shows only
/// its own series, each group remembers its last series, the user's targets appear as a group
/// only when there are any, and the series shown last is what the page opens on next time.
/// </summary>
[Collection("Localization")]
public sealed class HistoryPagePickerTests
{
    private static (ChipGroup Groups, ChipGroup Series) Rows(HistoryPage page)
    {
        ChipGroup[] rows = [.. UiTestHost.Descendants(page).OfType<ChipGroup>()];
        ChipGroup groups = rows.Single(r => r.Variant == ChipGroupVariant.Menu);
        string[] rangeIds = ["15m", "1h", "6h", "24h", "7d", "30d", "all"];
        ChipGroup series = rows.Single(r => r.Variant != ChipGroupVariant.Menu && !rangeIds.Contains(r.SelectedId));
        return (groups, series);
    }

    [Fact]
    public async Task Groups_show_their_own_series_remember_the_last_one_and_the_shown_series_is_saved()
    {
        var settings = new AppSettings { Language = "en", HistorySeriesId = "jitter" };
        await using TestServices services = TestServices.Create(settings);

        UiTestHost.Run(() =>
        {
            using var page = new HistoryPage(services.Services);
            page.Activate();
            (ChipGroup groups, ChipGroup series) = Rows(page);

            // Opens on the saved series, in its group, with only that group's series on offer.
            Assert.Equal(nameof(SeriesGroup.Quality), groups.SelectedId);
            Assert.Equal("jitter", series.SelectedId);

            // No enabled endpoint: no "your targets" group.
            groups.Select(nameof(SeriesGroup.Endpoints));
            Assert.Equal(nameof(SeriesGroup.Quality), groups.SelectedId);

            groups.Select(nameof(SeriesGroup.Connection));
            Assert.Equal("network", series.SelectedId);

            series.Select("ipv6");
            Assert.Equal("ipv6", settings.HistorySeriesId);

            groups.Select(nameof(SeriesGroup.Latency));
            Assert.Equal("gateway", series.SelectedId);

            // Back to the group it was in before: the series chosen there is still the one shown.
            groups.Select(nameof(SeriesGroup.Connection));
            Assert.Equal("ipv6", series.SelectedId);

            page.Deactivate();
        });
    }

    [Fact]
    public async Task The_targets_group_lists_the_enabled_endpoints_and_leaves_with_them()
    {
        var settings = new AppSettings
        {
            Language = "en",
            HistorySeriesId = SeriesNames.ForEndpoint("u1"),
            ApplicationEndpoints =
            [
                new EndpointConfig("u1", "API", EndpointKind.Url, Url: "https://api.example/health", Enabled: true),
                new EndpointConfig("u2", "Off", EndpointKind.Url, Url: "https://off.example/", Enabled: false),
            ],
        };
        await using TestServices services = TestServices.Create(settings);

        UiTestHost.Run(() =>
        {
            using var page = new HistoryPage(services.Services);
            page.Activate();
            (ChipGroup groups, ChipGroup series) = Rows(page);

            Assert.Equal(nameof(SeriesGroup.Endpoints), groups.SelectedId);
            Assert.Equal(SeriesNames.ForEndpoint("u1"), series.SelectedId);

            groups.Select(nameof(SeriesGroup.Traffic));
            Assert.Equal("traffic-down", series.SelectedId);
            groups.Select(nameof(SeriesGroup.Endpoints));
            Assert.Equal(SeriesNames.ForEndpoint("u1"), series.SelectedId);

            // The last endpoint is removed in Settings: on the next visit the group is gone and
            // the page falls back to the default series rather than an empty chart.
            settings.ApplicationEndpoints.Clear();
            page.Deactivate();
            page.Activate();
            (groups, series) = Rows(page);
            Assert.Equal(nameof(SeriesGroup.Latency), groups.SelectedId);
            Assert.Equal("internet", series.SelectedId);

            page.Deactivate();
        });
    }
}
