using System.Net;
using InternetMonitor.Core.Tools.LanScan;
using Xunit;

namespace InternetMonitor.Tests.Core.Tools;

/// <summary>What the scanner remembers between scans: new, known, missing, and the names the user gave.</summary>
public sealed class LanScanHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"im-lanscan-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    private static LanDevice Device(string ip, string? mac, string? host = null) => new(IPAddress.Parse(ip), host, mac, 1.0);

    [Fact]
    public void A_device_is_new_once_and_known_from_then_on()
    {
        LanScanHistory history = LanScanHistory.Load(_path);
        LanDevice printer = Device("192.168.1.20", "AA:BB:CC:00:11:22", "printer");

        Assert.False(history.IsKnown(printer));
        Assert.Equal(1, history.RecordScan([printer], DateTimeOffset.UtcNow));
        Assert.True(history.IsKnown(printer));
        Assert.Equal(0, history.RecordScan([printer], DateTimeOffset.UtcNow));

        LanScanHistory reloaded = LanScanHistory.Load(_path);
        Assert.True(reloaded.IsKnown(printer));
        Assert.True(reloaded.IsKnown(printer with { Address = IPAddress.Parse("192.168.1.99") }));
    }

    [Fact]
    public void Without_a_mac_the_address_is_the_identity()
    {
        LanScanHistory history = LanScanHistory.Load(_path);

        Assert.Equal("192.168.1.5", LanScanHistory.KeyOf(Device("192.168.1.5", null)));
        Assert.Equal("AA:BB:CC:00:11:22", LanScanHistory.KeyOf(Device("192.168.1.5", "aa-bb-cc-00-11-22")));
        history.RecordScan([Device("192.168.1.5", null)], DateTimeOffset.UtcNow);
        Assert.True(history.IsKnown(Device("192.168.1.5", null)));
    }

    [Fact]
    public void A_name_survives_a_reload_and_an_empty_name_removes_it()
    {
        LanScanHistory history = LanScanHistory.Load(_path);
        LanDevice nas = Device("192.168.1.30", "AA:BB:CC:00:11:33");

        history.SetName(nas, "  NAS in the attic ");
        Assert.Equal("NAS in the attic", LanScanHistory.Load(_path).Lookup(nas)?.Name);

        history.SetName(nas, "   ");
        Assert.Null(LanScanHistory.Load(_path).Lookup(nas)?.Name);
    }

    [Fact]
    public void Known_devices_absent_from_a_scan_are_reported_missing()
    {
        LanScanHistory history = LanScanHistory.Load(_path);
        LanDevice a = Device("192.168.1.1", "AA:00:00:00:00:01");
        LanDevice b = Device("192.168.1.2", "AA:00:00:00:00:02");
        history.RecordScan([a, b], DateTimeOffset.UtcNow);

        IReadOnlyList<KnownDevice> missing = history.Missing([a]);

        Assert.Equal("AA:00:00:00:00:02", Assert.Single(missing).Key);
    }
}
