using System.Text;
using InternetMonitor.Core.Export;

namespace InternetMonitor.Core.Tools.LanScan;

/// <summary>
/// Builds the CSV behind the LAN scanner's export button - a pure function, like
/// <see cref="Diagnosis.DiagnosticReportBuilder"/>. Host names and manufacturers are text other
/// parties chose, so every field goes through <see cref="Csv.Field"/>.
/// </summary>
public static class LanScanCsvBuilder
{
    public static string Build(IReadOnlyList<LanDevice> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("IP,Name,Hostname,MAC,Vendor,FirstSeenUtc,ResponseTimeMs");

        foreach (LanDevice device in devices.OrderBy(d => d.Address, IpAddressComparer.Instance))
        {
            sb.Append(Csv.Field(device.Address.ToString())).Append(',')
                .Append(Csv.Field(device.Name)).Append(',')
                .Append(Csv.Field(device.Hostname)).Append(',')
                .Append(Csv.Field(device.MacAddress)).Append(',')
                .Append(Csv.Field(device.Vendor)).Append(',')
                .Append(device.FirstSeenUtc is { } seen ? seen.UtcDateTime.ToString("O") : string.Empty).Append(',')
                .AppendLine(device.RoundtripMs is { } ms ? ms.ToString("F0") : string.Empty);
        }

        return sb.ToString();
    }

    /// <summary>Sorts addresses numerically (10.0.0.2 before 10.0.0.10) rather than as text.</summary>
    private sealed class IpAddressComparer : IComparer<System.Net.IPAddress>
    {
        public static readonly IpAddressComparer Instance = new();

        public int Compare(System.Net.IPAddress? x, System.Net.IPAddress? y)
        {
            byte[] a = x?.GetAddressBytes() ?? [];
            byte[] b = y?.GetAddressBytes() ?? [];

            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int cmp = a[i].CompareTo(b[i]);
                if (cmp != 0)
                {
                    return cmp;
                }
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
