using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
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

    /// <summary>
    /// Runs one headless check cycle, writes a transcript to the path that follows, and exits.
    /// A development and support aid - see <see cref="SelfTest"/>.
    /// </summary>
    private const string SelfTestArgument = "--selftest";

    /// <summary>
    /// Comma-separated step keys to force into failure, e.g. "--simulate gateway,dns". Debug
    /// builds only - see <see cref="FaultSimulation"/>.
    /// </summary>
    private const string SimulateArgument = "--simulate";

    /// <summary>Milliseconds of artificial delay per network step, so in-flight UI can be observed.</summary>
    private const string SimulateDelayArgument = "--simulate-delay";

    /// <summary>
    /// Opens the window straight onto a named page ("history", "incidents", ...). Implies --show.
    /// Also what the tray's Settings item would want if it were a shortcut.
    /// </summary>
    private const string PageArgument = "--page";

    /// <summary>Opens the Tools page and immediately traces a route to the given target.</summary>
    private const string TraceArgument = "--trace";

    [STAThread]
    private static void Main(string[] args)
    {
        // Applies to both the headless self-test and the real UI, so the failure screens can be
        // looked at as well as transcribed. A no-op in Release builds.
        FaultSimulation.Set(SelfTest.ParseSteps(TryGetArgumentValue(args, SimulateArgument)));
        if (int.TryParse(TryGetArgumentValue(args, SimulateDelayArgument), out int delayMs) && delayMs > 0)
        {
            FaultSimulation.StepDelay = TimeSpan.FromMilliseconds(delayMs);
        }

        if (TryGetArgumentValue(args, SelfTestArgument) is { } selfTestPath)
        {
            // Deliberately before the single-instance check: running a diagnostic cycle must work
            // while the real app is sitting in the tray, which is exactly when you want it.
            LocalizationManager.Initialize(AppSettings.Load().Language);
            Environment.ExitCode = SelfTest.RunAsync(selfTestPath).GetAwaiter().GetResult();
            return;
        }

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
        string? page = TryGetArgumentValue(args, PageArgument);
        string? trace = TryGetArgumentValue(args, TraceArgument);

        ApplicationConfiguration.Initialize();
        Application.Run(new AppHost(showWindow || page is not null || trace is not null, page, trace));
    }

    /// <summary>
    /// Reads "--name value" or "--name=value" from the command line, returning null when the flag
    /// is absent.
    /// </summary>
    private static string? TryGetArgumentValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }

            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
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
