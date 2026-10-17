using InternetMonitor.Core.History;
using InternetMonitor.Net;
using Xunit;

namespace InternetMonitor.Tests.Core.History;

/// <summary>LTTB keeps the shape of a series when it thins it, and a race stops at the first acceptable answer.</summary>
public sealed class DownsampleAndRaceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lttb_keeps_the_ends_the_count_and_a_single_dip_that_plain_decimation_would_lose()
    {
        List<MetricPoint> points = [.. Enumerable.Range(0, 1000).Select(i => new MetricPoint(T0.AddSeconds(i), i == 537 ? 0 : 20, null))];

        IReadOnlyList<MetricPoint> thinned = Downsample.Lttb(points, 50);

        Assert.Equal(50, thinned.Count);
        Assert.Equal(points[0], thinned[0]);
        Assert.Equal(points[^1], thinned[^1]);
        Assert.Contains(thinned, p => p.Value == 0);
        (MetricPoint First, MetricPoint Second)[] outOfOrder = [.. thinned.Zip(thinned.Skip(1)).Where(pair => pair.First.Timestamp >= pair.Second.Timestamp)];
        Assert.True(outOfOrder.Length == 0, $"{outOfOrder.Length} pairs out of order, first: {outOfOrder.FirstOrDefault().First.Timestamp:HH:mm:ss} then {outOfOrder.FirstOrDefault().Second.Timestamp:HH:mm:ss}");
    }

    [Fact]
    public void Lttb_passes_small_inputs_and_tiny_thresholds_through_untouched()
    {
        List<MetricPoint> points = [.. Enumerable.Range(0, 10).Select(i => new MetricPoint(T0.AddSeconds(i), i, null))];

        Assert.Same(points, Downsample.Lttb(points, 10));
        Assert.Same(points, Downsample.Lttb(points, 2));
    }

    [Fact]
    public void Summarize_gives_min_average_max_and_p95_and_nothing_for_nothing()
    {
        List<MetricPoint> points = [.. Enumerable.Range(1, 100).Select(i => new MetricPoint(T0.AddSeconds(i), i, null))];

        (double min, double average, double max, double p95) = Downsample.Summarize(points)!.Value;

        Assert.Equal(1, min);
        Assert.Equal(50.5, average);
        Assert.Equal(100, max);
        Assert.Equal(95, p95);
        Assert.Null(Downsample.Summarize([]));
    }

    [Fact]
    public async Task The_first_acceptable_answer_wins_and_the_others_are_cancelled()
    {
        bool slowCancelled = false;
        List<Func<CancellationToken, Task<int>>> contenders =
        [
            async ct =>
            {
                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException)
                {
                    slowCancelled = true;
                    throw;
                }

                return 1;
            },
            async ct => { await Task.Delay(10, ct); return -1; },
            async ct => { await Task.Delay(40, ct); return 2; },
        ];

        RaceOutcome<int> outcome = await Race.FirstAsync(contenders, v => v > 0, CancellationToken.None);

        Assert.True(outcome.HasWinner);
        Assert.Equal(2, outcome.Winner);
        Assert.Equal([-1, 2], outcome.Finished);
        for (int i = 0; i < 50 && !slowCancelled; i++)
        {
            await Task.Delay(20);
        }

        Assert.True(slowCancelled);
    }

    [Fact]
    public async Task With_no_acceptable_answer_there_is_no_winner_but_every_result_is_listed()
    {
        List<Func<CancellationToken, Task<int>>> contenders =
        [
            _ => Task.FromResult(-1),
            _ => Task.FromResult(-2),
        ];

        RaceOutcome<int> outcome = await Race.FirstAsync(contenders, v => v > 0, CancellationToken.None);

        Assert.False(outcome.HasWinner);
        Assert.Equal(2, outcome.Finished.Count);
        Assert.False((await Race.FirstAsync<int>([], _ => true, CancellationToken.None)).HasWinner);
    }
}
