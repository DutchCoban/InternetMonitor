using System.Net;
using InternetMonitor.Net;

namespace InternetMonitor.Core.Tools.SpeedTest;

/// <summary>The stages of a speed test, in the order they run.</summary>
public enum SpeedTestPhase
{
    Ping,
    Jitter,
    Download,
    Upload,
}

/// <summary>
/// A live reading for the result tiles: milliseconds for Ping and Jitter, Mbit/s for Download and
/// Upload.
/// </summary>
public sealed record SpeedTestProgress(SpeedTestPhase Phase, double Value);

/// <param name="IdlePingMs">ICMP round trip with the line quiet - the baseline for the two loaded readings.</param>
/// <param name="LoadedPingDownloadMs">Median ICMP round trip while the download phase filled the line.</param>
/// <param name="LoadedPingUploadMs">Median ICMP round trip while the upload phase filled the line.</param>
public sealed record SpeedTestResult(
    double PingMs,
    double JitterMs,
    double DownloadMbps,
    double UploadMbps,
    double? IdlePingMs = null,
    double? LoadedPingDownloadMs = null,
    double? LoadedPingUploadMs = null)
{
    /// <summary>
    /// How much the round trip grew while the line was full, in the worse direction - the number
    /// bufferbloat is judged by. Null when ICMP was blocked and nothing could be compared.
    /// </summary>
    public double? LoadedIncreaseMs =>
        IdlePingMs is { } idle && (LoadedPingDownloadMs ?? LoadedPingUploadMs) is not null
            ? Math.Max(0, Math.Max(LoadedPingDownloadMs ?? 0, LoadedPingUploadMs ?? 0) - idle)
            : null;

    /// <summary>
    /// A-F for <see cref="LoadedIncreaseMs"/>, on the scale the widely used bufferbloat tests use:
    /// under 5 ms extra is excellent, over 200 ms means calls and games fall apart whenever
    /// anyone downloads something.
    /// </summary>
    public static string Grade(double increaseMs) => increaseMs switch
    {
        < 5 => "A",
        < 30 => "B",
        < 60 => "C",
        < 200 => "D",
        _ => "F",
    };
}

/// <summary>
/// Speaks the LibreSpeed backend protocol (github.com/librespeed/speedtest) directly over a plain
/// <see cref="HttpClient"/> - no server picker, a single fixed server (see <see cref="BaseUrl"/>),
/// per user direction that this feature not grow a configuration surface. Ported from v1's
/// identically-named class (<c>InternetMonitor/Network/SpeedTestClient.cs</c>) essentially
/// unchanged - the protocol and the chosen server are both still correct, and v2 has nothing to
/// add here beyond its own namespace.
///
/// Protocol confirmed by reading the actual backend source (backend/garbage.php,
/// backend/empty.php) rather than assumed: garbage.php streams back <c>ckSize</c> MiB of
/// octet-stream data; empty.php accepts and discards any POST body (used for both the upload
/// measurement and, with no body, as a lightweight round-trip-time probe for ping/jitter).
/// </summary>
public sealed class SpeedTestClient : IDisposable
{
    // Amsterdam, Netherlands (Clouvider) - picked from LibreSpeed's own official public-server
    // directory (https://librespeed.org/backend-servers/servers.php), not guessed: a real,
    // currently-working server, European (matches this app's other checks), geographically close
    // for most of this app's users.
    private const string BaseUrl = "https://ams.speedtest.clouvider.net/backend";
    private const string DownloadPath = "garbage.php";
    private const string UploadPath = "empty.php";
    private const string PingPath = "empty.php";

    private static readonly TimeSpan PhaseDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Roughly how much data one test moves at the given speeds: each direction runs flat out for
    /// one phase. Used to warn before a manual test on a metered connection.
    /// </summary>
    public static double EstimatedMegabytes(double downloadMbps, double uploadMbps) =>
        (downloadMbps + uploadMbps) * PhaseDuration.TotalSeconds / 8;
    private const int ConcurrentStreams = 2;
    private const int PingSampleCount = 5;
    private const int UploadChunkBytes = 1_000_000;

    // Latency under load is measured with ICMP to a well-known anycast address rather than with the
    // speed-test server's own HTTP endpoint: the HTTP connections are the ones being saturated, so
    // a request on them would measure their own queue, not the line's.
    private static readonly TimeSpan LoadedPingInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan LoadedPingTimeout = TimeSpan.FromSeconds(1);
    private const int IdlePingSamples = 5;

    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly IPAddress _pingTarget;

    public SpeedTestClient(IPAddress? pingTarget = null) => _pingTarget = pingTarget ?? IPAddress.Parse("9.9.9.9");

    public async Task<SpeedTestResult> RunAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        // Recorded for the network-health monitor: a full line makes TCP retransmit, and an
        // incident raised over it should say a speed test was running (see SpeedTestActivity).
        SpeedTestActivity.Begin();
        try
        {
            (double pingMs, double jitterMs) = await MeasurePingJitterAsync(progress, cancellationToken).ConfigureAwait(false);
            double? idlePing = Median(await PingRepeatedlyAsync(IdlePingSamples, cancellationToken).ConfigureAwait(false));
            (double downloadMbps, double? loadedDown) = await MeasureDownloadAsync(progress, cancellationToken).ConfigureAwait(false);
            (double uploadMbps, double? loadedUp) = await MeasureUploadAsync(progress, cancellationToken).ConfigureAwait(false);
            return new SpeedTestResult(pingMs, jitterMs, downloadMbps, uploadMbps, idlePing, loadedDown, loadedUp);
        }
        finally
        {
            SpeedTestActivity.End();
        }
    }

    /// <summary>ICMP pings at <see cref="LoadedPingInterval"/> until <paramref name="count"/> are sent or the token fires.</summary>
    private async Task<List<double>> PingRepeatedlyAsync(int count, CancellationToken cancellationToken)
    {
        var samples = new List<double>();
        try
        {
            for (int i = 0; i < count && !cancellationToken.IsCancellationRequested; i++)
            {
                PingOutcome outcome = await IcmpPing.SendAsync(_pingTarget, LoadedPingTimeout, cancellationToken).ConfigureAwait(false);
                if (outcome.Success && outcome.RoundtripMs is { } roundtrip)
                {
                    samples.Add(roundtrip);
                }

                await Task.Delay(LoadedPingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The phase ended; whatever was collected stands.
        }

        return samples;
    }

    private static double? Median(List<double> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private async Task<(double PingMs, double JitterMs)> MeasurePingJitterAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        var samples = new List<double>();
        for (int i = 0; i < PingSampleCount; i++)
        {
            string url = $"{BaseUrl}/{PingPath}?r={Random.Shared.Next()}";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                progress?.Report(new SpeedTestProgress(SpeedTestPhase.Ping, samples.Min()));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                // A single failed ping sample shouldn't abort the whole measurement - just skip it.
            }
        }

        if (samples.Count == 0)
        {
            return (0, 0);
        }

        double pingMs = samples.Min();
        double jitterMs = samples.Count > 1
            ? samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).Average()
            : 0;
        progress?.Report(new SpeedTestProgress(SpeedTestPhase.Jitter, jitterMs));
        return (pingMs, jitterMs);
    }

    private Task<(double Mbps, double? LoadedPingMs)> MeasureDownloadAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken) =>
        RunThroughputPhaseAsync(SpeedTestPhase.Download, async (token, reportBytes) =>
        {
            string url = $"{BaseUrl}/{DownloadPath}?ckSize=100&r={Random.Shared.Next()}";
            using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buffer = new byte[65536];
            int read;
            // Reported per chunk (not just once at the end) so bytes already read before the
            // phase's time budget cancels this request are still counted - garbage.php's ckSize=100
            // (100 MiB) request is expected to often get cut off mid-stream, not run to completion.
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                reportBytes(read);
            }
        }, progress, cancellationToken);

    private Task<(double Mbps, double? LoadedPingMs)> MeasureUploadAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        var payload = new byte[UploadChunkBytes];
        Random.Shared.NextBytes(payload);

        return RunThroughputPhaseAsync(SpeedTestPhase.Upload, async (token, reportBytes) =>
        {
            string url = $"{BaseUrl}/{UploadPath}?r={Random.Shared.Next()}";
            using var content = new ByteArrayContent(payload);
            using HttpResponseMessage response = await _httpClient.PostAsync(url, content, token).ConfigureAwait(false);
            // Unlike download, HttpClient doesn't expose upload progress mid-POST without a
            // custom HttpContent - reported as one chunk per completed request instead. The
            // one in-flight POST still running when the phase's time budget cancels it is the
            // only thing not counted, capped at UploadChunkBytes (1 MB) - an acceptable, small
            // approximation for a diagnostic reading, not worth a hand-rolled streaming HttpContent.
            reportBytes(payload.Length);
        }, progress, cancellationToken);
    }

    /// <summary>
    /// Shared scaffolding for both throughput phases: runs <paramref name="runOneRequestAsync"/>
    /// repeatedly across <see cref="ConcurrentStreams"/> concurrent loops until
    /// <see cref="PhaseDuration"/> elapses, reporting live Mbps as bytes come in via the callback
    /// the request body is handed, then returns the phase's overall average Mbps - plus the median
    /// ICMP round trip measured alongside it while the line was full.
    /// </summary>
    private async Task<(double Mbps, double? LoadedPingMs)> RunThroughputPhaseAsync(
        SpeedTestPhase phase,
        Func<CancellationToken, Action<int>, Task> runOneRequestAsync,
        IProgress<SpeedTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(PhaseDuration);

        long totalBytes = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        void ReportBytes(int count)
        {
            long running = Interlocked.Add(ref totalBytes, count);
            double elapsed = sw.Elapsed.TotalSeconds;
            if (elapsed > 0.2)
            {
                progress?.Report(new SpeedTestProgress(phase, running * 8.0 / elapsed / 1_000_000));
            }
        }

        async Task RunOneStreamAsync()
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    await runOneRequestAsync(cts.Token, ReportBytes).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                // Expected once the phase's CancelAfter budget elapses - not an error. Bytes
                // transferred before cancellation were already reported via ReportBytes above.
            }
        }

        // Pings start a second in, once the streams have ramped up: the first moments of a phase
        // are TCP slow start, not a full line.
        Task<List<double>> loadedPings = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
                return await PingRepeatedlyAsync(int.MaxValue, cts.Token).ConfigureAwait(false);
            },
            CancellationToken.None);

        await Task.WhenAll(Enumerable.Range(0, ConcurrentStreams).Select(_ => RunOneStreamAsync())).ConfigureAwait(false);

        sw.Stop();
        double finalMbps = sw.Elapsed.TotalSeconds > 0 ? totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000 : 0;
        progress?.Report(new SpeedTestProgress(phase, finalMbps));

        List<double> pings;
        try
        {
            pings = await loadedPings.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pings = [];
        }

        return (finalMbps, Median(pings));
    }

    public void Dispose() => _httpClient.Dispose();
}
