using System.Diagnostics;
using System.Net.Sockets;
using InternetMonitor.Localization;

namespace InternetMonitor.Network.Probes;

public sealed record TimeSyncProbeResult(
    string ProbeId,
    ProbeStatus Status,
    string Summary,
    TimeSpan Duration,
    DateTimeOffset TimestampUtc,
    string Server,
    TimeSpan? Offset,
    bool WithinMargin,
    string? ErrorDetail = null) : IProbeResult
{
    public double? ChartValue => Offset?.TotalSeconds;

    public IReadOnlyDictionary<string, string> ToDetails()
    {
        var d = new Dictionary<string, string> { ["Server"] = Server, ["WithinMargin"] = WithinMargin.ToString() };
        if (Offset is not null) d["OffsetSeconds"] = Offset.Value.TotalSeconds.ToString("F3");
        return d;
    }
}

/// <summary>
/// Minimal SNTP client (RFC 4330) - .NET has no built-in NTP client. Computes the clock offset
/// using the standard formula ((T2-T1)+(T3-T4))/2, where T1/T4 are local send/receive times
/// and T2/T3 are the server's receive/transmit timestamps.
/// </summary>
public sealed class TimeSyncProbe : IProbe
{
    public string Id => "time";
    public string Category => "Network";

    public const string DefaultServer = "pool.ntp.org";
    private static readonly TimeSpan AcceptableMargin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<IProbeResult> RunAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = (int)Timeout.TotalMilliseconds;

            byte[] request = new byte[48];
            request[0] = 0x1B; // LI=0, VN=3, Mode=3 (client)

            DateTime t1 = DateTime.UtcNow;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);

            await udpClient.SendAsync(request, DefaultServer, 123, cts.Token).ConfigureAwait(false);
            UdpReceiveResult received = await udpClient.ReceiveAsync(cts.Token).ConfigureAwait(false);
            DateTime t4 = DateTime.UtcNow;

            byte[] response = received.Buffer;
            if (response.Length < 48)
            {
                throw new IOException("NTP response too short");
            }

            DateTime t2 = ReadNtpTimestamp(response, 32); // server receive time
            DateTime t3 = ReadNtpTimestamp(response, 40); // server transmit time

            TimeSpan offset = TimeSpan.FromTicks(((t2 - t1) + (t3 - t4)).Ticks / 2);
            bool withinMargin = offset.Duration() <= AcceptableMargin;
            sw.Stop();

            string sign = offset.Ticks >= 0 ? "+" : "-";
            string summary = $"{sign}{FormatOffset(offset.Duration())}";

            return new TimeSyncProbeResult(
                Id, withinMargin ? ProbeStatus.Ok : ProbeStatus.Warning, summary, sw.Elapsed, DateTimeOffset.UtcNow,
                DefaultServer, offset, withinMargin);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            sw.Stop();
            return new TimeSyncProbeResult(
                Id, ProbeStatus.Unknown, LocalizationManager.Instance.Get("diagnosis.timeSync.unreachable.title"), sw.Elapsed, DateTimeOffset.UtcNow,
                DefaultServer, null, false, ex.Message);
        }
    }

    // A well-synced clock's offset is normally a fraction of a second - formatting it as
    // hh:mm:ss alone always rounds that down to "00:00:00", which looks like a suspiciously
    // perfect sync rather than the small (but real) offset it actually is. Millisecond
    // precision throughout, in a fixed hh:mm:ss.fff shape regardless of magnitude.
    private static string FormatOffset(TimeSpan duration) => duration.ToString(@"hh\:mm\:ss\.fff");

    private static DateTime ReadNtpTimestamp(byte[] buffer, int offset)
    {
        uint seconds = ReadUInt32BigEndian(buffer, offset);
        uint fraction = ReadUInt32BigEndian(buffer, offset + 4);
        double milliseconds = seconds * 1000.0 + (fraction * 1000.0 / uint.MaxValue);
        return NtpEpoch.AddMilliseconds(milliseconds);
    }

    private static uint ReadUInt32BigEndian(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) | ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
}
