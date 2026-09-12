using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using InternetMonitor.Configuration;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record PortReachabilityProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string Host,
    int Port,
    PortProtocol Protocol,
    bool? TcpReachable,
    double? TcpLatencyMs,
    bool? UdpResponded,
    bool? UdpDefinitelyClosed,
    string? ErrorDetail = null) : IProbeResult
{
    /// <summary>TCP connect latency, when available - the natural chartable value for this probe.</summary>
    public double? ChartValue => TcpLatencyMs;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string>
        {
            ["Host"] = Host,
            ["Port"] = Port.ToString(),
            ["Protocol"] = Protocol.ToString(),
        };
        if (TcpReachable is not null) d["TcpReachable"] = TcpReachable.Value.ToString();
        if (TcpLatencyMs is not null) d["TcpLatencyMs"] = TcpLatencyMs.Value.ToString("F0");
        if (UdpResponded is not null) d["UdpResponded"] = UdpResponded.Value.ToString();
        if (UdpDefinitelyClosed is not null) d["UdpDefinitelyClosed"] = UdpDefinitelyClosed.Value.ToString();
        return d;
    }
}

/// <summary>
/// Generic TCP/UDP port reachability probe - no protocol handshake, just "can we reach this
/// port". TCP is always conclusive (connect success or an actively-refused connection both
/// prove the host answered; only a timeout means unreachable - same pattern already proven in
/// GatewayReachability, reimplemented standalone here for an arbitrary host/port). UDP is
/// inherently ambiguous: a timeout could mean "open but silently ignored us" or "filtered" -
/// only an OS-surfaced ICMP port-unreachable error is a real, confident "closed" signal, so a
/// plain UDP timeout is reported as Unknown, never a false Ok or Error.
/// </summary>
public sealed class PortReachabilityProbe : IProbe
{
    private static readonly TimeSpan UdpReceiveWindow = TimeSpan.FromMilliseconds(800);

    public string Id { get; }
    public string Category => "Application";

    private readonly string _host;
    private readonly int _port;
    private readonly PortProtocol _protocol;
    private readonly TimeSpan _timeout;

    public PortReachabilityProbe(string id, string host, int port, PortProtocol protocol, TimeSpan timeout)
    {
        Id = id;
        _host = host;
        _port = port;
        _protocol = protocol;
        _timeout = timeout;
    }

    public async Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);

        IPAddress? address;
        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(_host, cts.Token).ConfigureAwait(false);
            address = addresses.FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            sw.Stop();
            return new PortReachabilityProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Get("probe.dns.resolutionFailed"), sw.Elapsed, DateTimeOffset.UtcNow,
                _host, _port, _protocol, null, null, null, null, ex.Message);
        }

        if (address is null)
        {
            sw.Stop();
            return new PortReachabilityProbeResult(
                Id, ProbeStatus.Error, LocalizationManager.Instance.Get("probe.dns.resolutionFailed"), sw.Elapsed, DateTimeOffset.UtcNow,
                _host, _port, _protocol, null, null, null, null, "No addresses returned");
        }

        bool? tcpReachable = null;
        double? tcpLatencyMs = null;
        bool? udpResponded = null;
        bool? udpDefinitelyClosed = null;

        if (_protocol is PortProtocol.Tcp or PortProtocol.Both)
        {
            (tcpReachable, tcpLatencyMs) = await CheckTcpAsync(address, cts.Token).ConfigureAwait(false);
        }

        if (_protocol is PortProtocol.Udp or PortProtocol.Both)
        {
            (udpResponded, udpDefinitelyClosed) = await CheckUdpAsync(address, cts.Token).ConfigureAwait(false);
        }

        sw.Stop();
        return BuildResult(sw.Elapsed, tcpReachable, tcpLatencyMs, udpResponded, udpDefinitelyClosed);
    }

    private PortReachabilityProbeResult BuildResult(
        TimeSpan duration, bool? tcpReachable, double? tcpLatencyMs, bool? udpResponded, bool? udpDefinitelyClosed)
    {
        ProbeStatus status;
        string summary;

        // Plain language for non-technical users - the protocol (TCP/UDP) is still recorded in
        // ToDetails() and only mentioned here when it disambiguates a mixed TCP+UDP result.
        LocalizationManager loc = LocalizationManager.Instance;
        if (tcpReachable is not null)
        {
            // TCP is always conclusive - it is the primary signal even in Both mode, per design.
            status = tcpReachable.Value ? ProbeStatus.Ok : ProbeStatus.Error;
            summary = tcpReachable.Value
                ? loc.Format("probe.port.reachable", _port, tcpLatencyMs!.Value)
                : loc.Format("probe.port.unreachable", _port);
            if (_protocol == PortProtocol.Both && udpResponded is not null)
            {
                summary += udpResponded.Value ? loc.Get("probe.port.udpAlsoResponds")
                    : udpDefinitelyClosed == true ? loc.Get("probe.port.udpClosed")
                    : loc.Get("probe.port.udpUnclear");
            }
        }
        else if (udpResponded is true)
        {
            status = ProbeStatus.Ok;
            summary = loc.Format("probe.port.reachableUdp", _port);
        }
        else if (udpDefinitelyClosed is true)
        {
            status = ProbeStatus.Error;
            summary = loc.Format("probe.port.closed", _port);
        }
        else
        {
            status = ProbeStatus.Unknown;
            summary = loc.Format("probe.port.noResponse", _port);
        }

        return new PortReachabilityProbeResult(
            Id, status, summary, duration, DateTimeOffset.UtcNow,
            _host, _port, _protocol, tcpReachable, tcpLatencyMs, udpResponded, udpDefinitelyClosed);
    }

    private async Task<(bool Reachable, double? LatencyMs)> CheckTcpAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var client = new TcpClient { NoDelay = true };
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(address, _port, cancellationToken).ConfigureAwait(false);
            return (true, sw.Elapsed.TotalMilliseconds);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return (true, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return (false, null);
        }
    }

    private async Task<(bool Responded, bool DefinitelyClosed)> CheckUdpAsync(IPAddress address, CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient(address.AddressFamily);
            using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            innerCts.CancelAfter(UdpReceiveWindow);

            await udp.SendAsync(Array.Empty<byte>(), new IPEndPoint(address, _port), cancellationToken).ConfigureAwait(false);
            await udp.ReceiveAsync(innerCts.Token).ConfigureAwait(false);
            return (true, false);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            // Windows surfaces an ICMP "port unreachable" as ECONNRESET on the next socket call -
            // this is the one unambiguous "closed" signal UDP can give us.
            return (false, true);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return (false, false);
        }
    }
}
