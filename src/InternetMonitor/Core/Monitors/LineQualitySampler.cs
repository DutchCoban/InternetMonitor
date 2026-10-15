using System.Net;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Monitors;

/// <summary>One burst's result: the share of echoes lost, and how much the round trip varied.</summary>
/// <param name="JitterMs">Null when fewer than two echoes came back - one reply has nothing to vary against.</param>
public sealed record LineQualitySample(DateTimeOffset AtUtc, double LossPercent, double? JitterMs, double? MedianMs);

/// <summary>
/// Measures packet loss and jitter on the line, once a minute, for the history and the provider
/// report.
///
/// The pipeline sends one echo per step per cycle, which says whether the internet answers but not
/// how steadily: one lost echo in a cycle is either a dead line or bad luck. A burst of ten, a tenth
/// of a second apart, tells the two apart and gives a loss percentage and a jitter figure a
/// provider recognises - the kind of number that explains choppy calls and stuttering games on a
/// connection that is technically "up".
///
/// Always on, unlike <see cref="NetworkHealthMonitor"/>'s packet-loss condition: ten small echoes a
/// minute cost nothing, and a series that only exists for users who found a switch in Settings is
/// no use as evidence. A burst in which nothing came back is not recorded: that is an outage (which
/// the pipeline records as one) or a network that filters ICMP, and either way "100% packet loss"
/// would only pull the daily averages towards a number that describes something else.
/// </summary>
public sealed class LineQualitySampler : IAsyncDisposable
{
    public const int EchoesPerBurst = 10;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstBurstDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EchoSpacing = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<IPAddress?> _target;
    private readonly Func<IPAddress?> _source;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    /// <param name="target">The address to ping - the first of the public addresses step 5 uses.</param>
    /// <param name="source">The monitored adapter's address, so the burst leaves through the same adapter as the checks; null when there is none.</param>
    public LineQualitySampler(Func<IPAddress?> target, Func<IPAddress?> source)
    {
        _target = target;
        _source = source;
    }

    public event Action<LineQualitySample>? Sampled;

    public void Start() => _loop ??= RunAsync(_shutdown.Token);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FirstBurstDelay, cancellationToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                if (await MeasureAsync(cancellationToken).ConfigureAwait(false) is { } sample)
                {
                    Sampled?.Invoke(sample);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task<LineQualitySample?> MeasureAsync(CancellationToken cancellationToken)
    {
        if (_target() is not { } target || _source() is not { } source)
        {
            return null;
        }

        var roundTrips = new List<double?>(EchoesPerBurst);
        for (int i = 0; i < EchoesPerBurst; i++)
        {
            if (i > 0)
            {
                await Task.Delay(EchoSpacing, cancellationToken).ConfigureAwait(false);
            }

            PingOutcome outcome = await IcmpPing.SendAsync(target, EchoTimeout, cancellationToken, source).ConfigureAwait(false);
            roundTrips.Add(outcome.Success ? outcome.RoundtripMs : null);
        }

        return Compute(roundTrips, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Loss and jitter from one burst, in send order; null entries are lost echoes. Jitter is the
    /// mean absolute difference between consecutive replies (RFC 3550's definition without its
    /// smoothing, which suits a burst this short). Null when nothing came back at all - see the
    /// class summary for why that burst is not recorded.
    /// </summary>
    public static LineQualitySample? Compute(IReadOnlyList<double?> roundTrips, DateTimeOffset at)
    {
        List<double> replies = [.. roundTrips.OfType<double>()];
        if (roundTrips.Count == 0 || replies.Count == 0)
        {
            return null;
        }

        double loss = 100.0 * (roundTrips.Count - replies.Count) / roundTrips.Count;

        double? jitter = null;
        if (replies.Count >= 2)
        {
            double sum = 0;
            for (int i = 1; i < replies.Count; i++)
            {
                sum += Math.Abs(replies[i] - replies[i - 1]);
            }

            jitter = sum / (replies.Count - 1);
        }

        List<double> sorted = [.. replies.Order()];
        double median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;

        return new LineQualitySample(at, loss, jitter, median);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }
}
