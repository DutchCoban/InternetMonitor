namespace InternetMonitor.Logging;

/// <summary>
/// Minimal append-only file logger with a hard size cap. No sensitive data is ever logged -
/// only outage start/end timestamps and durations.
/// </summary>
public sealed class SimpleFileLogger
{
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;

    private readonly string _logPath;
    private readonly object _lock = new();

    public SimpleFileLogger()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "InternetMonitor",
            "logs");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "log.txt");
    }

    public void Log(string message)
    {
        lock (_lock)
        {
            try
            {
                RollOverIfNeeded();
                string line = $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {message}{Environment.NewLine}";
                File.AppendAllText(_logPath, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging is best-effort; never let it crash the app.
            }
        }
    }

    private void RollOverIfNeeded()
    {
        if (!File.Exists(_logPath))
        {
            return;
        }

        var info = new FileInfo(_logPath);
        if (info.Length <= MaxFileSizeBytes)
        {
            return;
        }

        string rolledPath = Path.Combine(info.DirectoryName!, "log.1.txt");
        File.Copy(_logPath, rolledPath, overwrite: true);
        File.Delete(_logPath);
    }
}
