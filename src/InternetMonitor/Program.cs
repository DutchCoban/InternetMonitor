using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Localization;

namespace InternetMonitor;

internal static class Program
{
    /// <summary>
    /// Opens the main window immediately instead of starting silently in the tray. Normal
    /// launches stay quiet - a monitor that throws a window at you on every boot is a nuisance -
    /// but a shortcut the user clicks to look at the app wants the window, and so does any
    /// scripted check of the UI.
    /// </summary>
    private const string ShowWindowArgument = "--show";

    [STAThread]
    private static void Main(string[] args)
    {
        using SingleInstanceManager? instance = SingleInstanceManager.TryAcquire();
        if (instance is null)
        {
            // Another copy is already running and already has a tray icon. Saying nothing is the
            // right behaviour for a tray app: the user double-clicked a shortcut for something
            // that is, as far as they are concerned, already open.
            return;
        }

        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        bool showWindow = args.Any(a => string.Equals(a, ShowWindowArgument, StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();
        Application.Run(new AppHost(showWindow));
    }

    /// <summary>
    /// Last-resort handler: write what happened somewhere the user can find it, tell them where,
    /// and let the process end. Unlike v1 the message is localized - though it deliberately falls
    /// back to English text if the crash happened before the string table was even loaded.
    /// </summary>
    private static void ReportFatal(Exception? exception)
    {
        string path = AppPaths.CrashLog;
        try
        {
            AppPaths.TryEnsureDirectory(AppPaths.LogsDir);
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write to. The message box below is then the only record there will be.
        }

        string title = "Internet Monitor";
        string message = $"An unexpected error occurred. Details were written to:\n{path}";
        if (LocalizationManager.Instance is { } loc)
        {
            title = loc.Get("crash.title");
            message = loc.Format("crash.message", path);
        }

        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
