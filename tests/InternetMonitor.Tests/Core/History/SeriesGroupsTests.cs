using InternetMonitor.Core.History;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>The History page's picker groups: every built-in series sits in one, every group has a label, and an id finds its group.</summary>
[Collection("Localization")]
public sealed class SeriesGroupsTests
{
    [Fact]
    public void Every_built_in_series_belongs_to_a_group_other_than_the_endpoints_one_and_each_group_is_used()
    {
        Assert.DoesNotContain(SeriesNames.Chartable, s => s.Group == SeriesGroup.Endpoints);

        foreach (SeriesGroup group in Enum.GetValues<SeriesGroup>().Where(g => g != SeriesGroup.Endpoints))
        {
            Assert.Contains(SeriesNames.Chartable, s => s.Group == group);
        }
    }

    [Fact]
    public void The_groups_keep_the_display_order_of_the_series_within_them()
    {
        Assert.Equal(["network", "link", "wifi-signal", "ipv6"], SeriesNames.Chartable.Where(s => s.Group == SeriesGroup.Connection).Select(s => s.Id));
        Assert.Equal(["gateway", "internet", "dns", "https"], SeriesNames.Chartable.Where(s => s.Group == SeriesGroup.Latency).Select(s => s.Id));
    }

    [Fact]
    public void Every_group_has_an_english_label()
    {
        foreach (SeriesGroup group in Enum.GetValues<SeriesGroup>())
        {
            string key = SeriesNames.GroupKey(group);
            Assert.NotEqual(key, LocalizationManager.GetEnglish(key));
        }
    }

    [Fact]
    public void An_id_finds_its_group_and_an_unknown_one_lands_with_the_default_series()
    {
        Assert.Equal(SeriesGroup.Quality, SeriesNames.GroupOf("jitter"));
        Assert.Equal(SeriesGroup.Endpoints, SeriesNames.GroupOf(SeriesNames.ForEndpoint("abc")));
        Assert.Equal(SeriesGroup.Latency, SeriesNames.GroupOf("no-such-series"));
        Assert.Equal(SeriesGroup.Latency, SeriesNames.GroupOf(null));
    }
}
