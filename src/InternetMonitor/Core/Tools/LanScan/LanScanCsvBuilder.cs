using System.Text;

namespace InternetMonitor.Core.Tools.LanScan;

/// <summary>Builds the CSV behind the LAN scanner's export button - a pure function, like <see cref="Diagnosis.DiagnosticReportBuilder"/>.</summary>
public static class LanScanCsvBuilder
{
    public static string Build(IReadOnlyList<LanDevice> devices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("IP,Hostname,MAC,ResponseTimeMs");

        foreach (LanDevice device in devices.OrderBy(d => d.Address, IpAddressComparer.Instance))
        {
            sb.Append(Escape(device.Address.ToString())).Append(',')
                .Append(Escape(device.Hostname ?? string.Empty)).Append(',')
                .Append(Escape(device.MacAddress ?? string.Empty)).Append(',')
                .AppendLine(device.RoundtripMs is { } ms ? ms.ToString("F0") : string.Empty);
        }

        return sb.ToString();
    }

    /// <summary>Quotes a field only when it needs it, matching how every common CSV reader expects an unquoted field to look.</summary>
    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

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
