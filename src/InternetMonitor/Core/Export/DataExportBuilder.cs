using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InternetMonitor.Configuration;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;

namespace InternetMonitor.Core.Export;

/// <summary>
/// Builds the "export all data" .zip: a redacted copy of settings, every incident, one CSV per
/// history series, and the diagnostics log when there is one. Traceroute results are not
/// included - <see cref="Incident.TraceroutePath"/> and <see cref="AppPaths.TraceroutesDir"/>
/// exist as scaffolding nothing writes to yet, and this export does not change that.
///
/// A pure function over data the caller already has (from <see cref="App.AppServices"/>, via
/// Settings) - no file-system reads here beyond what is handed in, same reasoning as
/// <see cref="Diagnosis.DiagnosticReportBuilder"/>.
/// </summary>
public static class DataExportBuilder
{
    private const string Redacted = "[verborgen]";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="documents">
    /// Readable documents to include next to the raw data, by file name - the summary and the
    /// provider report, so the zip holds everything the app can produce, not just its inputs.
    /// </param>
    public static byte[] Build(
        AppSettings settings,
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> historySeries,
        byte[]? diagnosticsLogBytes,
        IReadOnlyDictionary<string, string>? documents = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(zip, "settings.json", RedactSettings(settings));
            WriteTextEntry(zip, "incidents.json", JsonSerializer.Serialize(incidents, Json));

            foreach ((string seriesId, IReadOnlyList<MetricPoint> points) in historySeries)
            {
                WriteTextEntry(zip, $"history/{seriesId}.csv", BuildHistoryCsv(points));
            }

            foreach ((string name, string content) in documents ?? new Dictionary<string, string>())
            {
                WriteTextEntry(zip, name, content);
            }

            if (diagnosticsLogBytes is { Length: > 0 })
            {
                ZipArchiveEntry entry = zip.CreateEntry("diagnostics.log", CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                entryStream.Write(diagnosticsLogBytes);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Replaces the Uptime Kuma push URL and every custom endpoint header value with
    /// <see cref="Redacted"/> before serializing - both are real credentials (a push URL is a
    /// bearer token in disguise; a header is frequently an API key), and nothing in this export is
    /// worth sending those out unencrypted for.
    /// </summary>
    private static string RedactSettings(AppSettings settings)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(settings, Json);
        if (node is not JsonObject root)
        {
            return "{}";
        }

        if (root["KumaPushUrl"]?.GetValue<string>() is { Length: > 0 })
        {
            root["KumaPushUrl"] = Redacted;
        }

        if (root["ApplicationEndpoints"] is JsonArray endpoints)
        {
            foreach (JsonNode? endpointNode in endpoints)
            {
                if (endpointNode is JsonObject endpoint && endpoint["Headers"] is JsonArray headers)
                {
                    foreach (JsonNode? headerNode in headers)
                    {
                        if (headerNode is JsonObject header && header.ContainsKey("Value"))
                        {
                            header["Value"] = Redacted;
                        }
                    }
                }
            }
        }

        return root.ToJsonString(Json);
    }

    private static string BuildHistoryCsv(IReadOnlyList<MetricPoint> points)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TimestampUtc,Value,Metadata");

        foreach (MetricPoint point in points)
        {
            sb.Append(point.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(point.Value.ToString(CultureInfo.InvariantCulture)).Append(',')
                .AppendLine(point.Metadata ?? string.Empty);
        }

        return sb.ToString();
    }

    private static void WriteTextEntry(ZipArchive zip, string name, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using Stream entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Encoding.UTF8);
        writer.Write(content);
    }
}
