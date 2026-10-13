using System.Text.Json;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;

namespace InternetMonitor.Core.Diagnosis;

/// <summary>How much a <see cref="DiagnosticLogger"/> writes, from nothing to every cycle.</summary>
public enum DiagnosticLogLevel
{
    Off,

    /// <summary>Only a step whose state just changed.</summary>
    Basic,

    /// <summary>A changed step, plus every non-Ok step, plus every step if <see cref="AppSettings.LogSuccessfulChecks"/> is on.</summary>
    Extended,

    /// <summary>Every step, every cycle.</summary>
    Full,
}

/// <summary>
/// Writes a JSON-Lines record of what the pipeline found - one line per included step result, plus
/// one line each time an incident opens or resolves - for diagnosing a problem after the fact or
/// handing a log to someone else for support. Off by default: this is troubleshooting output, not
/// something a monitor needs running to do its job.
///
/// Every write is best-effort. A diagnostics log is a convenience for looking back at what
/// happened, not something the pipeline depends on - a locked file or a full disk must never be
/// the reason a connectivity check fails to run.
/// </summary>
public sealed class DiagnosticLogger
{
    private readonly AppSettings _settings;
    private readonly Dictionary<StepId, StepState> _lastState = [];
    private readonly object _lock = new();

    public DiagnosticLogger(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Where diagnostics.log currently lives - the user's override if one is set, otherwise the
    /// default logs folder. Resolved fresh on every call rather than cached, since there is nothing
    /// here a settings change would need to invalidate.
    /// </summary>
    public string LogPath => Path.Combine(ResolvedDirectory(), "diagnostics.log");

    public string ResolvedDirectory() =>
        string.IsNullOrWhiteSpace(_settings.DiagnosticLogDirectory) ? AppPaths.LogsDir : _settings.DiagnosticLogDirectory;

    /// <summary>
    /// Records one cycle's results, filtered by <see cref="AppSettings.DiagnosticLogLevel"/>.
    ///
    /// Only a step whose state actually changed is "new information" at the Basic level - the
    /// pipeline re-publishes every step's result on every cycle, so logging all of them
    /// unconditionally would turn a quiet, healthy connection into thousands of identical lines a
    /// day.
    /// </summary>
    public void LogCycle(StepSnapshot snapshot, RootCauseResult diagnosis, string? incidentId)
    {
        DiagnosticLogLevel level = _settings.DiagnosticLogLevel;
        if (level == DiagnosticLogLevel.Off)
        {
            return;
        }

        List<object> included = [];
        lock (_lock)
        {
            foreach (StepResult result in snapshot.All.OrderBy(r => (int)r.Step))
            {
                bool changed = !_lastState.TryGetValue(result.Step, out StepState previous) || previous != result.State;
                _lastState[result.Step] = result.State;

                bool include = level switch
                {
                    DiagnosticLogLevel.Full => true,
                    DiagnosticLogLevel.Extended => changed || _settings.LogSuccessfulChecks || result.State != StepState.Ok,
                    _ => changed,
                };

                if (include)
                {
                    included.Add(BuildStepEntry(result, level));
                }
            }
        }

        if (included.Count == 0)
        {
            return;
        }

        WriteLine(new
        {
            ts = snapshot.TakenUtc.ToString("O"),
            type = "cycle",
            cause = diagnosis.Cause.ToString(),
            severity = diagnosis.Severity.ToString(),
            incidentId,
            steps = included,
        });
    }

    public void LogIncidentOpened(Incident incident)
    {
        if (_settings.DiagnosticLogLevel == DiagnosticLogLevel.Off)
        {
            return;
        }

        WriteLine(new
        {
            ts = incident.StartUtc.ToString("O"),
            type = "incidentOpened",
            id = incident.Id,
            cause = incident.Cause.ToString(),
            headline = incident.Headline,
        });
    }

    public void LogIncidentResolved(Incident incident)
    {
        if (_settings.DiagnosticLogLevel == DiagnosticLogLevel.Off)
        {
            return;
        }

        WriteLine(new
        {
            ts = (incident.EndUtc ?? DateTimeOffset.UtcNow).ToString("O"),
            type = "incidentResolved",
            id = incident.Id,
            durationSeconds = incident.Duration?.TotalSeconds,
        });
    }

    private static object BuildStepEntry(StepResult result, DiagnosticLogLevel level)
    {
        Dictionary<string, string>? measurements = null;
        if (level == DiagnosticLogLevel.Full && result.Measurements.Count > 0)
        {
            measurements = result.Measurements.ToDictionary(m => m.Label, m => m.Value);
        }

        return new
        {
            step = result.Step.ToString(),
            state = result.State.ToString(),
            headline = result.Headline,
            measurements,
        };
    }

    private void WriteLine(object payload)
    {
        try
        {
            string directory = ResolvedDirectory();
            if (!AppPaths.TryEnsureDirectory(directory))
            {
                return;
            }

            string path = Path.Combine(directory, "diagnostics.log");
            RotateIfNeeded(path);
            File.AppendAllText(path, JsonSerializer.Serialize(payload) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort, same as every other file write in this app - a log write must never be
            // the reason a connectivity check fails.
        }
    }

    /// <summary>
    /// Size-based rotation with a fixed number of numbered backups, oldest evicted first -
    /// diagnostics.log -> .1 -> .2 -> ... -> MaxDiagnosticLogFiles, then gone. Runs before every
    /// write rather than on a timer, so it stays correct even if the app was closed for weeks.
    /// </summary>
    private void RotateIfNeeded(string path)
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length < _settings.MaxDiagnosticLogFileSizeBytes)
        {
            return;
        }

        int maxFiles = Math.Max(1, _settings.MaxDiagnosticLogFiles);

        string oldest = $"{path}.{maxFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (int i = maxFiles - 1; i >= 1; i--)
        {
            string from = $"{path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{path}.{i + 1}");
            }
        }

        File.Move(path, $"{path}.1");
    }
}
