using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InternetMonitor.Net;

/// <summary>What one captive-portal probe found.</summary>
/// <param name="Answered">Something answered over HTTP at all. When false, nothing can be concluded.</param>
/// <param name="IsPortal">The answer was not the known test page: the network is intercepting web traffic.</param>
/// <param name="RedirectHost">Where a redirect pointed, for display only - never opened.</param>
public sealed record CaptivePortalOutcome(bool Answered, bool IsPortal, int? StatusCode, string? RedirectHost, string? Error);

/// <summary>
/// Asks "is this network putting a login page in front of the internet?" - the hotel, train and
/// guest Wi-Fi case.
///
/// The step 7 reachability check is HTTPS, and a login page cannot answer HTTPS cleanly: the user
/// sees a TLS failure or a time-out, which is true but tells them nothing. So when that check
/// fails, this sends one plain-HTTP request for a page whose exact content is known - the same
/// test page Windows itself uses for its "action needed" network notice. A network without a
/// login page returns it untouched; one with a login page redirects the request or answers it
/// with its own page instead.
///
/// The decision is deliberately narrow (see <see cref="Classify"/>): only a redirect, an explicit
/// 511, or a 200 carrying the wrong page counts. Anything else - a time-out, a refused connection,
/// a 403 from a filtering proxy - is "no evidence", and the ordinary diagnosis stands.
/// </summary>
public static class CaptivePortalProbe
{
    /// <summary>
    /// What "open the login page" opens. Not the redirect a portal answered with - on a hostile
    /// network that could point anywhere - but the address Windows itself opens for its "action
    /// needed" notice: the network intercepts it and sends the browser to its own login page.
    /// </summary>
    public const string LoginPageUrl = "http://www.msftconnecttest.com/redirect";

    private const int MaxResponseBytes = 4096;

    public static async Task<CaptivePortalOutcome> ProbeAsync(
        Uri url,
        string expectedBody,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IPAddress? source = null,
        IReadOnlyList<IPAddress>? dnsServers = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            DnsOutcome dns = await DnsResolve.ResolveViaAsync(url.Host, dnsServers, source, timeout, budget.Token).ConfigureAwait(false);
            IPAddress? remote = dns.Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? dns.Addresses.FirstOrDefault();
            if (remote is null)
            {
                return new CaptivePortalOutcome(false, false, null, null, "DNS lookup failed");
            }

            using var tcp = new TcpClient(remote.AddressFamily) { NoDelay = true };
            if (!TcpConnect.TryBind(tcp.Client, source))
            {
                return new CaptivePortalOutcome(false, false, null, null, "Local address not available");
            }

            await tcp.ConnectAsync(remote, url.Port, budget.Token).ConfigureAwait(false);
            NetworkStream stream = tcp.GetStream();

            // Unlike TlsTimedRequest this does send "Connection: close": the body is read to the end
            // here, and a server that closes when done is the simplest way to know where that is.
            string request =
                $"GET {url.PathAndQuery} HTTP/1.1\r\n" +
                $"Host: {url.Host}\r\n" +
                "User-Agent: InternetMonitor\r\n" +
                "Accept: */*\r\n" +
                "Connection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request), budget.Token).ConfigureAwait(false);

            string response = await ReadResponseAsync(stream, budget.Token).ConfigureAwait(false);
            return Classify(response, expectedBody);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            return new CaptivePortalOutcome(false, false, null, null, ex is OperationCanceledException ? "Timed out" : ex.Message);
        }
    }

    /// <summary>
    /// Reads until the server closes the connection, the announced body is complete, or
    /// <see cref="MaxResponseBytes"/> is reached - the test page is a few bytes, and a login page is
    /// recognised from its first kilobyte as well as from its last.
    /// </summary>
    private static async Task<string> ReadResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxResponseBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (total > 0)
            {
                // Plenty of portal boxes reset the connection right after sending their redirect
                // instead of closing it cleanly. What arrived before the reset is the answer.
                break;
            }

            if (read == 0)
            {
                break;
            }

            total += read;
            string sofar = Encoding.ASCII.GetString(buffer, 0, total);
            int headerEnd = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0 && ContentLength(sofar[..headerEnd]) is { } length && total >= headerEnd + 4 + length)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Decides from a raw HTTP response whether a login page is in the way. Pure, so it can be
    /// tested against every shape a portal is known to answer with.
    /// </summary>
    public static CaptivePortalOutcome Classify(string response, string expectedBody)
    {
        int headerEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string head = headerEnd >= 0 ? response[..headerEnd] : response;
        string body = headerEnd >= 0 ? response[(headerEnd + 4)..] : string.Empty;

        string[] lines = head.Split("\r\n");
        string[] statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !statusParts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(statusParts[1], out int status))
        {
            return new CaptivePortalOutcome(false, false, null, null, "Not an HTTP response");
        }

        string? redirectHost = Header(lines, "Location") is { } location && Uri.TryCreate(location, UriKind.Absolute, out Uri? target)
            ? target.Host
            : null;

        bool isPortal = status switch
        {
            >= 300 and < 400 => true,

            // RFC 6585 "Network Authentication Required" - the one status made for exactly this.
            511 => true,

            200 => !string.Equals(body.Trim(), expectedBody, StringComparison.Ordinal),
            _ => false,
        };

        return new CaptivePortalOutcome(true, isPortal, status, redirectHost, null);
    }

    private static int? ContentLength(string head) =>
        Header(head.Split("\r\n"), "Content-Length") is { } value && int.TryParse(value, out int length) ? length : null;

    private static string? Header(string[] lines, string name)
    {
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }
}
