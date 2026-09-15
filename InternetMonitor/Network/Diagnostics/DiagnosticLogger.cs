using System.Text.Json;
using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using InternetMonitor.Network.Diagnosis;
using InternetMonitor.Network.Probes;

namespace InternetMonitor.Network.Diagnostics;

/// <summary>
/// Structured JSON Lines diagnostic log, level-gated by <see cref="AppSettings.DiagnosticLogLevel"/>.
/// Off = nothing written; Basic = status changes and incident open/close only; Extended = every
/// periodic probe result too; Full = verbose fields on successes as well. Rotates by size,
/// keeping a configurable number of files. A logging failure is swallowed - it must never
/// surface as a monitoring error (mirrors the existing SimpleFileLogger's contract).
/// </summary>
public sealed class DiagnosticLogger
{
    private readonly AppSettings _settings;
    private readonly string _logPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, ProbeStatus> _lastStatusByProbe = new();

    public DiagnosticLogger(AppSettings settings)
    {
        _settings = settings;
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetMonitor", "logs");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "diagnostics.log");
    }

    public string LogPath => _logPath;

    /// <summary>Called once per poll cycle with every probe's latest result plus the derived diagnosis.</summary>
    public void LogCycle(ProbeSnapshot snapshot, DiagnosisResult diagnosis, string? incidentId)
    {
        if (_settings.DiagnosticLogLevel == DiagnosticLogLevel.Off)
        {
            return;
        }

        LogProbeIfNeeded(snapshot.NetworkInterface, incidentId);
        LogProbeIfNeeded(snapshot.IpAddress, incidentId);
        LogProbeIfNeeded(snapshot.Gateway, incidentId);
        LogProbeIfNeeded(snapshot.Internet, incidentId);
        LogProbeIfNeeded(snapshot.Dns, incidentId);
        LogProbeIfNeeded(snapshot.GeneralHttps, incidentId);
        LogProbeIfNeeded(snapshot.TimeSync, incidentId);
        foreach (var (_, result) in snapshot.ApplicationEndpoints)
        {
            LogProbeIfNeeded(result, incidentId);
        }

        if (diagnosis.Classification != DiagnosisClassification.Healthy)
        {
            WriteLine("WARN", "Diagnosis", diagnosis.Headline, incidentId, new Dictionary<string, string>
            {
                ["classification"] = diagnosis.Classification.ToString(),
                ["explanation"] = diagnosis.Explanation,
            });
        }
    }

    public void LogIncidentOpened(Incident incident)
    {
        if (_settings.DiagnosticLogLevel == DiagnosticLogLevel.Off)
        {
            return;
        }

        WriteLine("ERROR", "Incident", LocalizationManager.Instance.Format("diagnosticLog.incident.opened", incident.Diagnosis), incident.Id, new Dictionary<string, string>
        {
            ["classification"] = incident.Classification.ToString(),
        });
    }

    public void LogIncidentResolved(Incident incident)
    {
        if (_settings.DiagnosticLogLevel == DiagnosticLogLevel.Off)
        {
            return;
        }

        WriteLine("INFO", "Incident", LocalizationManager.Instance.Format("diagnosticLog.incident.resolved", incident.Duration?.ToString(@"hh\:mm\:ss") ?? "-"), incident.Id, new Dictionary<string, string>
        {
            ["classification"] = incident.Classification.ToString(),
        });
    }

    /// <summary>Logged at any level except Off, not further gated by Basic/Extended/Full nuance - a speed test is a deliberate, infrequent, information-dense event, not a routine per-cycle check.</summary>
    public void LogSpeedTestCompleted(SpeedTestResult result, TimeSpan duration)
    {
        if (_settings.DiagnosticLogLevel == DiagnosticLogLevel.Off)
        {
            return;
        }

        WriteLine("INFO", "SpeedTest",
            LocalizationManager.Instance.Format("diagnosticLog.speedTest.completed", result.DownloadMbps.ToString("F1"), result.UploadMbps.ToString("F1")),
            null,
            new Dictionary<string, string>
            {
                ["downloadMbps"] = result.DownloadMbps.ToString("F1"),
                ["uploadMbps"] = result.UploadMbps.ToString("F1"),
                ["pingMs"] = result.PingMs.ToString("F0"),
                ["jitterMs"] = result.JitterMs.ToString("F0"),
                ["durationMs"] = duration.TotalMilliseconds.ToString("F0"),
            });
    }

    private void LogProbeIfNeeded(IProbeResult result, string? incidentId)
    {
        bool statusChanged = !_lastStatusByProbe.TryGetValue(result.ProbeId, out ProbeStatus previous) || previous != result.Status;
        _lastStatusByProbe[result.ProbeId] = result.Status;

        bool shouldLog = _settings.DiagnosticLogLevel switch
        {
            DiagnosticLogLevel.Basic => statusChanged,
            DiagnosticLogLevel.Extended => statusChanged || _settings.LogSuccessfulChecks || result.Status != ProbeStatus.Ok,
            DiagnosticLogLevel.Full => true,
            _ => false,
        };

        if (!shouldLog)
        {
            return;
        }

        string level = result.Status switch
        {
            ProbeStatus.Ok => "INFO",
            ProbeStatus.Warning => "WARN",
            ProbeStatus.Error or ProbeStatus.Blocked => "ERROR",
            _ => "INFO",
        };

        var fields = _settings.LogDetailedMeasurements || _settings.DiagnosticLogLevel == DiagnosticLogLevel.Full
            ? new Dictionary<string, string>(result.ToDetails())
            : new Dictionary<string, string>();
        fields["durationMs"] = result.Duration.TotalMilliseconds.ToString("F0");

        WriteLine(level, result.ProbeId, result.Summary, incidentId, fields);
    }

    private void WriteLine(string level, string category, string message, string? incidentId, IReadOnlyDictionary<string, string> fields)
    {
        try
        {
            var line = new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                level,
                category,
                incidentId,
                message,
                fields,
            };
            string json = JsonSerializer.Serialize(line);

            lock (_lock)
            {
                RotateIfNeeded();
                File.AppendAllText(_logPath, json + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic logging must never interrupt monitoring.
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_logPath))
        {
            return;
        }

        var info = new FileInfo(_logPath);
        if (info.Length <= _settings.MaxDiagnosticLogFileSizeBytes)
        {
            return;
        }

        int maxFiles = Math.Max(1, _settings.MaxDiagnosticLogFiles);
        string oldestPath = $"{_logPath}.{maxFiles}";
        if (File.Exists(oldestPath))
        {
            File.Delete(oldestPath);
        }

        for (int i = maxFiles - 1; i >= 1; i--)
        {
            string src = $"{_logPath}.{i}";
            if (File.Exists(src))
            {
                File.Move(src, $"{_logPath}.{i + 1}");
            }
        }

        File.Move(_logPath, $"{_logPath}.1");
    }
}
