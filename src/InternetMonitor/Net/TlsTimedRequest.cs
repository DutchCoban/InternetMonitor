using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace InternetMonitor.Net;

/// <summary>
/// One extra header line to send with a request - an API key or an Authorization token, most
/// often. A plain pair rather than reusing Configuration's EndpointHeader: this namespace is a
/// leaf (no other file here references Configuration), and keeping it that way means the request-
/// building code below has no reason to know anything about where a header came from.
/// </summary>
public readonly record struct RequestHeader(string Name, string Value);

/// <summary>Which stage of the request failed, when one did.</summary>
public enum HttpStage
{
    None,
    Dns,
    Tcp,
    Tls,
    Http,
}

/// <summary>Asks <see cref="TlsTimedRequest"/> to look up AAAA and connect over IPv6, from <paramref name="Source"/>.</summary>
public sealed record IPv6Path(IPAddress? Source);

/// <summary>How long each stage of a request took. A stage that was never reached is null.</summary>
public sealed record HttpTimings(TimeSpan? Dns, TimeSpan? Tcp, TimeSpan? Tls, TimeSpan? Http, TimeSpan Total)
{
    /// <summary>The address came from <see cref="DnsAnswerCache"/>, so <see cref="Dns"/> measured no lookup.</summary>
    public bool DnsFromCache { get; init; }
}

/// <summary>
/// The result of one <see cref="TlsTimedRequest"/>: whether any HTTP response came back, which
/// stage failed when none did, and the per-stage timings either way.
/// </summary>
public sealed record HttpOutcome(
    bool Succeeded,
    int? StatusCode,
    HttpStage FailedStage,
    string? Error,
    IPAddress? RemoteAddress,
    int Port,
    string? TlsProtocol,
    HttpTimings Timings)
{
    /// <summary>A 5xx means the stack works but the service behind it does not - degraded, not down.</summary>
    public bool ServerError => StatusCode >= 500;
}

/// <summary>
/// A full DNS -> TCP -> TLS -> HTTP request with every stage timed separately and every failure
/// attributed to the stage it happened in.
///
/// Hand-rolled on TcpClient and SslStream rather than built on HttpClient, exactly as v1 did, and
/// for the same reason: HttpClient gives one flattened success/failure and no way to say whether
/// the two seconds went into resolution, the handshake or waiting for the server. Those are three
/// different diagnoses, and telling them apart is the entire point of the tool.
///
/// Two details carried over from v1 that were paid for in debugging and would be easy to lose in
/// a rewrite:
///
///  - DNS gets its own short sub-budget inside the overall timeout. When resolution is what is
///    broken, the request should fail fast with a DNS cause rather than spend the whole budget on
///    a lookup that was never going to succeed, leaving no time to even attempt the rest.
///  - The request deliberately does NOT send "Connection: close". Some servers and WAFs read that
///    header as a scanner signal and delay the response by seconds. Measured live against a real
///    endpoint: 2.1s with the header, 0.26s without, everything else equal. Only the status line
///    is read and the connection is then disposed from this side, so the server closing it is not
///    needed.
/// </summary>
public static class TlsTimedRequest
{
    private static readonly TimeSpan DnsSubBudget = TimeSpan.FromSeconds(1.5);

    public static Task<HttpOutcome> RequestAsync(Uri uri, TimeSpan timeout, CancellationToken cancellationToken) =>
        RequestAsync(uri, timeout, [], cancellationToken);

    public static async Task<HttpOutcome> RequestAsync(
        Uri uri,
        TimeSpan timeout,
        IReadOnlyList<RequestHeader> headers,
        CancellationToken cancellationToken,
        IPAddress? source = null,
        IReadOnlyList<IPAddress>? dnsServers = null,
        IPv6Path? ipv6 = null)
    {
        var total = Stopwatch.StartNew();
        int port = uri.Port;
        TimeSpan? dnsTime = null;
        bool dnsFromCache = false;
        TimeSpan? tcpTime = null;
        TimeSpan? tlsTime = null;
        IPAddress? remote = null;
        string? tlsProtocol = null;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            DnsRecordType recordType = ipv6 is null ? DnsRecordType.A : DnsRecordType.Aaaa;
            DnsOutcome dns = await DnsResolve.ResolveViaAsync(uri.Host, dnsServers, source, DnsSubBudget, budget.Token, recordType).ConfigureAwait(false);
            dnsTime = TimeSpan.FromMilliseconds(dns.ElapsedMs);
            dnsFromCache = dns.FromCache;
            if (!dns.Success)
            {
                return Fail(HttpStage.Dns, dns.TimedOut ? "DNS lookup timed out" : $"DNS lookup failed ({dns.Error})");
            }

            // Prefer IPv4: the rest of the pipeline (gateway, public-IP reachability) is IPv4, and
            // mixing families here would make the target step's latency incomparable with the
            // others on a dual-stack network. The IPv6 check asks for AAAA records only.
            remote = dns.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? dns.Addresses[0];

            // The lookup went out over IPv4 from the adapter's IPv4 address; the connection itself
            // leaves from the adapter's IPv6 address when this is the IPv6 check.
            IPAddress? connectFrom = ipv6 is null ? source : ipv6.Source;
            using var tcp = new TcpClient(remote.AddressFamily) { NoDelay = true };
            if (!TcpConnect.TryBind(tcp.Client, connectFrom))
            {
                return Fail(HttpStage.Tcp, "Local address not available");
            }

            var tcpWatch = Stopwatch.StartNew();
            try
            {
                await tcp.ConnectAsync(remote, port, budget.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                tcpTime = tcpWatch.Elapsed;
                return Fail(HttpStage.Tcp, Describe(ex));
            }

            tcpTime = tcpWatch.Elapsed;

            Stream stream = tcp.GetStream();
            SslStream? ssl = null;
            try
            {
                if (uri.Scheme == Uri.UriSchemeHttps)
                {
                    // No custom certificate callback anywhere in this app: TLS validation is never
                    // bypassed, and a certificate problem is a real finding worth reporting.
                    ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    var tlsWatch = Stopwatch.StartNew();
                    try
                    {
                        await ssl.AuthenticateAsClientAsync(uri.Host).WaitAsync(budget.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        tlsTime = tlsWatch.Elapsed;
                        return Fail(HttpStage.Tls, Describe(ex));
                    }

                    tlsTime = tlsWatch.Elapsed;
                    tlsProtocol = ssl.SslProtocol.ToString();
                    stream = ssl;
                }

                var httpWatch = Stopwatch.StartNew();
                int statusCode;
                try
                {
                    statusCode = await ReadStatusLineAsync(stream, uri, headers, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or FormatException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    return Fail(HttpStage.Http, Describe(ex), httpWatch.Elapsed);
                }

                total.Stop();
                return new HttpOutcome(
                    true,
                    statusCode,
                    HttpStage.None,
                    null,
                    remote,
                    port,
                    tlsProtocol,
                    new HttpTimings(dnsTime, tcpTime, tlsTime, httpWatch.Elapsed, total.Elapsed) { DnsFromCache = dnsFromCache });
            }
            finally
            {
                ssl?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or UriFormatException)
        {
            return Fail(HttpStage.None, Describe(ex));
        }

        HttpOutcome Fail(HttpStage stage, string error, TimeSpan? httpTime = null)
        {
            total.Stop();
            return new HttpOutcome(
                false, null, stage, error, remote, port, tlsProtocol,
                new HttpTimings(dnsTime, tcpTime, tlsTime, httpTime, total.Elapsed) { DnsFromCache = dnsFromCache });
        }
    }

    private static async Task<int> ReadStatusLineAsync(
        Stream stream,
        Uri uri,
        IReadOnlyList<RequestHeader> headers,
        CancellationToken cancellationToken)
    {
        string request =
            $"GET {uri.PathAndQuery} HTTP/1.1\r\n"
            + $"Host: {uri.Host}\r\n"
            + "User-Agent: InternetMonitor/2.0\r\n"
            + "Accept: */*\r\n"
            + string.Concat(headers.Select(h => $"{Sanitize(h.Name)}: {Sanitize(h.Value)}\r\n"))
            + "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Strips any stray CR/LF from a user-supplied header name or value before it goes into the raw
    /// request text above. These come from a settings dialog the user types into themselves, not an
    /// untrusted network source - this is cheap insurance against a pasted newline producing a
    /// malformed request, not a defence against a hostile actor.
    /// </summary>
    private static string Sanitize(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private static string Describe(Exception ex) => ex switch
    {
        SocketException socket => socket.SocketErrorCode.ToString(),
        OperationCanceledException => "Timed out",
        AuthenticationException auth => auth.InnerException?.Message ?? auth.Message,
        _ => ex.Message,
    };
}
