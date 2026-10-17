using System.Net;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.Tools.LanScan;
using Xunit;

namespace InternetMonitor.Tests.Core.Export;

/// <summary>
/// A CSV cell that starts like a formula must open as text: a device on the LAN can call itself
/// anything, and "=cmd|..." is a classic way to turn an export into an attack on whoever opens it.
/// </summary>
public sealed class CsvTests
{
    [Theory]
    [InlineData("router.lan", "router.lan")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("Acme, Inc.", "\"Acme, Inc.\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+31 6", "'+31 6")]
    [InlineData("-dash", "'-dash")]
    [InlineData("@home", "'@home")]
    [InlineData("\tindented", "'\tindented")]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    public void Field_quotes_what_needs_quoting_and_defuses_a_leading_formula_character(string? value, string expected)
    {
        Assert.Equal(expected, Csv.Field(value));
    }

    [Fact]
    public void Lan_scan_export_defuses_a_hostname_and_a_vendor_that_look_like_formulas()
    {
        LanDevice[] devices =
        [
            new(IPAddress.Parse("192.168.1.10"), "=HYPERLINK(\"http://evil\")", "AA:BB:CC:DD:EE:FF", 1.5, "+ACME", false),
            new(IPAddress.Parse("192.168.1.2"), "printer", "AA:BB:CC:00:11:22", null, null, false),
        ];

        string csv = LanScanCsvBuilder.Build(devices);
        string[] lines = csv.TrimEnd().Split(Environment.NewLine);

        Assert.Equal("IP,Hostname,MAC,Vendor,ResponseTimeMs", lines[0]);
        Assert.Equal("192.168.1.2,printer,AA:BB:CC:00:11:22,,", lines[1]);
        Assert.Equal("192.168.1.10,\"'=HYPERLINK(\"\"http://evil\"\")\",AA:BB:CC:DD:EE:FF,'+ACME,2", lines[2]);
    }
}
