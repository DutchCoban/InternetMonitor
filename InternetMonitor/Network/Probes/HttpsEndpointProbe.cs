using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public enum HttpsFailureStage
{
    None,
    Dns,
    Tcp,
    Tls,
    Http,
}

public sealed record HttpsEndpointProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string Url,
    string? RemoteAddress,
    int Port,
    TimeSpan? DnsTime,
    TimeSpan? ConnectTime,
    TimeSpan? TlsTime,
    TimeSpan? HttpTime,
    int? HttpStatusCode,
    HttpsFailureStage FailureStage,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => Status == ProbeStatus.Ok || Status == ProbeStatus.Warning ? Duration.TotalMilliseconds : null;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string> { ["Url"] = Url, ["Port"] = Port.ToString() };
        if (RemoteAddress is not null) d["RemoteAddress"] = RemoteAddress;
        if (DnsTime is not null) d["DnsTimeMs"] = DnsTime.Value.TotalMilliseconds.ToString("F0");
        if (ConnectTime is not null) d["ConnectTimeMs"] = ConnectTime.Value.TotalMilliseconds.ToString("F0");
        if (TlsTime is not null) d["TlsTimeMs"] = TlsTime.Value.TotalMilliseconds.ToString("F0");
        if (HttpTime is not null) d["HttpTimeMs"] = HttpTime.Value.TotalMilliseconds.ToString("F0");
        if (HttpStatusCode is not null) d["HttpStatusCode"] = HttpStatusCode.Value.ToString();
        if (FailureStage != HttpsFailureStage.None) d["FailureStage"] = FailureStage.ToString();
        return d;
    }
}

/// <summary>
/// Staged HTTPS reachability probe: DNS -&gt; TCP connect -&gt; TLS handshake -&gt; a minimal raw
/// HTTP/1.1 request, each independently timed. Hand-rolled rather than built on HttpClient
/// because HttpClient doesn't cleanly expose per-phase timings - this gives exact DNS/connect/
/// TLS/HTTP/total numbers and lets each stage's failure map to a distinct, specific status
/// instead of one flattened "ERROR".
///
/// Status policy: any HTTP response at all (2xx-4xx) counts as reachable - the point is
/// proving the stack works, not that the root path is a valid route (real endpoints often
/// 404 on "/"). A 5xx is a Warning (the server answered but is unhealthy). Anything that
/// fails before an HTTP response is received is an Error, tagged with the failing stage.
/// </summary>
public sealed class HttpsEndpointProbe : IProbe
{
    public string Id { get; }
    public string Category { get; }

    private readonly string _url;
    private readonly TimeSpan _timeout;

    public HttpsEndpointProbe(string id, string category, string url, TimeSpan timeout)
    {
        Id = id;
        Category = category;
        _url = url;
        _timeout = timeout;
    }

    public async Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var totalSw = Stopwatch.StartNew();
        var uri = new Uri(_url);
        string host = uri.Host;
        int port = uri.Port;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        IPAddress? remoteAddress = null;
        TimeSpan? dnsTime = null, connectTime = null, tlsTime = null, httpTime = null;

        try
        {
            var dnsSw = Stopwatch.StartNew();
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return Fail(HttpsFailureStage.Dns, LocalizationManager.Instance.Get("probe.dns.resolutionFailed"), ex.Message, totalSw, uri, port, null, null, null, null);
            }
            dnsTime = dnsSw.Elapsed;
            remoteAddress = addresses.FirstOrDefault();
            if (remoteAddress is null)
            {
                return Fail(HttpsFailureStage.Dns, LocalizationManager.Instance.Get("probe.dns.resolutionFailed"), "No addresses returned", totalSw, uri, port, dnsTime, null, null, null);
            }

            using var tcpClient = new TcpClient { NoDelay = true };
            var tcpSw = Stopwatch.StartNew();
            try
            {
                await tcpClient.ConnectAsync(remoteAddress, port, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                return Fail(HttpsFailureStage.Tcp, LocalizationManager.Instance.Get("probe.https.tcpFailed"), ex.Message, totalSw, uri, port, dnsTime, tcpSw.Elapsed, null, null, remoteAddress);
            }
            connectTime = tcpSw.Elapsed;

            using NetworkStream tcpStream = tcpClient.GetStream();
            using var sslStream = new SslStream(tcpStream, leaveInnerStreamOpen: false);
            var tlsSw = Stopwatch.StartNew();
            try
            {
                await sslStream.AuthenticateAsClientAsync(host).WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
            {
                return Fail(HttpsFailureStage.Tls, LocalizationManager.Instance.Get("probe.https.tlsFailed"), ex.Message, totalSw, uri, port, dnsTime, connectTime, tlsSw.Elapsed, null, remoteAddress);
            }
            tlsTime = tlsSw.Elapsed;

            var httpSw = Stopwatch.StartNew();
            int statusCode;
            try
            {
                statusCode = await SendMinimalRequestAsync(sslStream, host, uri.PathAndQuery, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or FormatException)
            {
                return Fail(HttpsFailureStage.Http, LocalizationManager.Instance.Get("probe.https.noResponse"), ex.Message, totalSw, uri, port, dnsTime, connectTime, tlsTime, httpSw.Elapsed, remoteAddress);
            }
            httpTime = httpSw.Elapsed;
            totalSw.Stop();

            ProbeStatus status = statusCode >= 500 ? ProbeStatus.Warning : ProbeStatus.Ok;
            string summary = $"HTTP {statusCode} ({totalSw.Elapsed.TotalMilliseconds:F0} ms)";

            return new HttpsEndpointProbeResult(
                Id, status, summary, totalSw.Elapsed, DateTimeOffset.UtcNow,
                _url, remoteAddress.ToString(), port, dnsTime, connectTime, tlsTime, httpTime,
                statusCode, HttpsFailureStage.None);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // Defensive fallback for anything not already attributed to a specific stage.
            totalSw.Stop();
            return new HttpsEndpointProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Get("probe.https.unknownError"), totalSw.Elapsed, DateTimeOffset.UtcNow,
                _url, remoteAddress?.ToString(), port, dnsTime, connectTime, tlsTime, httpTime,
                null, HttpsFailureStage.None, ex.Message);
        }

        HttpsEndpointProbeResult Fail(
            HttpsFailureStage stage, string summary, string error, Stopwatch sw, Uri u, int p,
            TimeSpan? dns, TimeSpan? connect, TimeSpan? tls, TimeSpan? http, IPAddress? addr = null)
        {
            sw.Stop();
            return new HttpsEndpointProbeResult(
                Id, ProbeStatus.Error, summary, sw.Elapsed, DateTimeOffset.UtcNow,
                _url, addr?.ToString(), p, dns, connect, tls, http, null, stage, error);
        }
    }

    private static async Task<int> SendMinimalRequestAsync(SslStream stream, string host, string pathAndQuery, CancellationToken cancellationToken)
    {
        // No "Connection: close": some servers/WAFs treat that header as a scanner/bot signal
        // and deliberately delay their response by 1-3+ seconds (confirmed live against a real
        // endpoint: 2.1s with the header present, 0.26s without, all other conditions equal).
        // We only ever read the status line and immediately dispose the connection ourselves,
        // so we don't need the server to close it - a plain, ordinary-looking request avoids
        // the slow path entirely.
        string request = $"GET {pathAndQuery} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: InternetMonitor\r\nAccept: */*\r\n\r\n";
        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[512];
        int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read <= 0)
        {
            throw new IOException("Connection closed before any response was received");
        }

        string statusLine = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0];
        string[] parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out int statusCode))
        {
            throw new FormatException($"Unexpected HTTP status line: {statusLine}");
        }

        return statusCode;
    }
}
