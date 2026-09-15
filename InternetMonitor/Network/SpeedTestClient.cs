using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network;

public enum SpeedTestPhase
{
    Ping,
    Jitter,
    Download,
    Upload,
}

public sealed record SpeedTestProgress(SpeedTestPhase Phase, double Value);

public sealed record SpeedTestResult(double PingMs, double JitterMs, double DownloadMbps, double UploadMbps);

/// <summary>
/// Wraps a completed speed test's download or upload throughput as a normal
/// <see cref="IProbeResult"/> - this is what lets a manual or automatic run become a chartable row
/// on the Diagnostics screen (double-click history, etc.) via the exact same generic machinery
/// every other check already uses, even though <see cref="SpeedTestClient"/> itself isn't an
/// <see cref="IProbe"/>. See <see cref="DiagnosticsCoordinator.LatestSpeedTestResults"/>.
/// </summary>
public sealed record SpeedTestProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    double ValueMbps,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => ValueMbps;

    public IReadOnlyDictionary<string, string> ToDetails() => new Dictionary<string, string> { ["Mbps"] = ValueMbps.ToString("F1") };
}

/// <summary>
/// Speaks the LibreSpeed backend protocol (github.com/librespeed/speedtest) directly over a plain
/// <see cref="HttpClient"/> - no server picker, a single fixed server (see <see cref="BaseUrl"/>),
/// per user direction that this feature not grow a configuration surface. This client itself only
/// ever runs when explicitly awaited (<see cref="RunAsync"/>) - the scheduling (manual "Run" click
/// vs. an automatic interval) lives one layer up, in <see cref="DiagnosticsCoordinator"/> and
/// <see cref="Configuration.AppSettings.SpeedTestAutoRunEnabled"/>.
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
    private const int ConcurrentStreams = 2;
    private const int PingSampleCount = 5;
    private const int UploadChunkBytes = 1_000_000;

    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<SpeedTestResult> RunAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
    {
        (double pingMs, double jitterMs) = await MeasurePingJitterAsync(progress, cancellationToken).ConfigureAwait(false);
        double downloadMbps = await MeasureDownloadAsync(progress, cancellationToken).ConfigureAwait(false);
        double uploadMbps = await MeasureUploadAsync(progress, cancellationToken).ConfigureAwait(false);
        return new SpeedTestResult(pingMs, jitterMs, downloadMbps, uploadMbps);
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

    private Task<double> MeasureDownloadAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken) =>
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

    private Task<double> MeasureUploadAsync(IProgress<SpeedTestProgress>? progress, CancellationToken cancellationToken)
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
    /// the request body is handed, then returns the phase's overall average Mbps.
    /// </summary>
    private async Task<double> RunThroughputPhaseAsync(
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

        await Task.WhenAll(Enumerable.Range(0, ConcurrentStreams).Select(_ => RunOneStreamAsync())).ConfigureAwait(false);

        sw.Stop();
        double finalMbps = sw.Elapsed.TotalSeconds > 0 ? totalBytes * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000 : 0;
        progress?.Report(new SpeedTestProgress(phase, finalMbps));
        return finalMbps;
    }

    public void Dispose() => _httpClient.Dispose();
}
