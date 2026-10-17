using System.Globalization;
using InternetMonitor.Core.History;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>
/// The history store samples no faster than its interval, slices by time, forgets a series on
/// request, writes what it holds on dispose and reads it back, and cleans a damaged or stale file
/// while loading it.
/// </summary>
public sealed class MetricStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"im-history-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private MetricStore Store() => new(TimeSpan.FromDays(30), _directory) { MinimumSampleInterval = TimeSpan.FromSeconds(15) };

    [Fact]
    public async Task Sampling_respects_the_minimum_interval_while_recording_does_not()
    {
        await using MetricStore store = Store();
        await store.Loaded;

        Assert.True(store.Sample("latency", T0, 10));
        Assert.False(store.Sample("latency", T0.AddSeconds(5), 11));
        Assert.True(store.Sample("latency", T0.AddSeconds(15), 12));
        store.Record("latency", T0.AddSeconds(16), 13);

        Assert.Equal([10, 12, 13], store.Get("latency").Select(p => p.Value));
        Assert.Equal([12, 13], store.Get("latency", T0.AddSeconds(15)).Select(p => p.Value));
        Assert.Contains("latency", store.SeriesIds);
    }

    [Fact]
    public async Task What_is_held_survives_dispose_and_a_cleared_series_is_gone_from_disk()
    {
        await using (MetricStore store = Store())
        {
            await store.Loaded;
            store.Record("gateway", T0, 3, "meta");
            store.Record("gateway", T0.AddMinutes(1), 4);
            store.Record("other", T0, 1);
            store.Clear("other");
        }

        Assert.True(File.Exists(Path.Combine(_directory, "gateway.csv")));
        Assert.False(File.Exists(Path.Combine(_directory, "other.csv")));

        await using MetricStore reloaded = Store();
        await reloaded.Loaded;
        IReadOnlyList<MetricPoint> points = reloaded.Get("gateway");
        Assert.Equal(2, points.Count);
        Assert.Equal(new MetricPoint(T0, 3, "meta"), points[0]);
        Assert.Empty(reloaded.Get("other"));
    }

    [Fact]
    public async Task A_damaged_or_stale_line_is_dropped_and_the_file_rewritten_on_load()
    {
        Directory.CreateDirectory(_directory);
        DateTimeOffset stale = DateTimeOffset.UtcNow.AddDays(-40);
        DateTimeOffset fresh = DateTimeOffset.UtcNow.AddHours(-1);
        File.WriteAllLines(Path.Combine(_directory, "dns.csv"),
        [
            $"{fresh.AddMinutes(1).UtcTicks},9.5,",
            "not a line at all",
            $"{stale.UtcTicks},1,",
            $"{fresh.UtcTicks},8,x|y",
        ]);

        await using MetricStore store = Store();
        await store.Loaded;

        IReadOnlyList<MetricPoint> points = store.Get("dns");
        Assert.Equal([8, 9.5], points.Select(p => p.Value));
        Assert.Equal("x|y", points[0].Metadata);

        string[] rewritten = File.ReadAllLines(Path.Combine(_directory, "dns.csv"));
        Assert.Equal(2, rewritten.Length);
        Assert.StartsWith(fresh.UtcTicks.ToString(CultureInfo.InvariantCulture), rewritten[0]);
    }

    [Fact]
    public void Bounds_find_the_first_point_at_or_after_and_the_first_after()
    {
        List<MetricPoint> points = [.. Enumerable.Range(0, 10).Select(i => new MetricPoint(T0.AddMinutes(i), i, null))];

        Assert.Equal(3, MetricStore.LowerBound(points, T0.AddMinutes(3)));
        Assert.Equal(4, MetricStore.LowerBound(points, T0.AddMinutes(3).AddSeconds(1)));
        Assert.Equal(4, MetricStore.UpperBound(points, T0.AddMinutes(3)));
        Assert.Equal(0, MetricStore.LowerBound(points, T0.AddHours(-1)));
        Assert.Equal(10, MetricStore.LowerBound(points, T0.AddHours(1)));
    }
}
