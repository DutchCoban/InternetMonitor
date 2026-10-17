using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;

namespace InternetMonitor;

/// <summary>
/// Entry point: command-line modes (self-test, trace), crash handling and the single-instance
/// guard, then <see cref="App.AppHost"/>.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Kept for shortcuts made for older versions, which only opened the window with it. Every
    /// launch now opens the window except one with <see cref="AutoStart.TrayArgument"/> - the
    /// sign-in start, where a window on every boot would be a nuisance.
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

    /// <summary>
    /// Writes one export and exits, without a window: "--export zip|ai|plain|report &lt;path&gt;".
    /// For scripts and scheduled tasks - see <see cref="HeadlessExport"/>.
    /// </summary>
    private const string ExportArgument = "--export";

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

        int exportIndex = Array.FindIndex(args, a => string.Equals(a, ExportArgument, StringComparison.OrdinalIgnoreCase));
        if (exportIndex >= 0)
        {
            if (exportIndex + 2 >= args.Length)
            {
                Console.Error.WriteLine($"usage: InternetMonitor {ExportArgument} {string.Join("|", HeadlessExport.Kinds)} <path>");
                Environment.ExitCode = 1;
                return;
            }

            // Like the self-test: before the single-instance check, so it works while the app runs.
            Environment.ExitCode = HeadlessExport.RunAsync(args[exportIndex + 1], args[exportIndex + 2]).GetAwaiter().GetResult();
            return;
        }

        if (TryGetArgumentValue(args, SelfTestArgument) is { } selfTestPath)
        {
            // Deliberately before the single-instance check: running a diagnostic cycle must work
            // while the real app is sitting in the tray, which is exactly when you want it.
            LocalizationManager.Initialize(AppSettings.Load().Language);
            Environment.ExitCode = SelfTest.RunAsync(selfTestPath).GetAwaiter().GetResult();
            return;
        }

        bool trayOnly = args.Any(a => string.Equals(a, AutoStart.TrayArgument, StringComparison.OrdinalIgnoreCase))
            && !args.Any(a => string.Equals(a, ShowWindowArgument, StringComparison.OrdinalIgnoreCase));

        using SingleInstanceManager? instance = SingleInstanceManager.TryAcquire();
        if (instance is null)
        {
            // Another copy is already running in the tray. Someone who opens the app again wants
            // to see it, so that copy is asked to bring its window up; this one just exits.
            if (!trayOnly)
            {
                SingleInstanceManager.RequestShow();
            }

            return;
        }

        Application.ThreadException += (_, e) => ReportFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception);

        string? page = TryGetArgumentValue(args, PageArgument);
        string? trace = TryGetArgumentValue(args, TraceArgument);

        ApplicationConfiguration.Initialize();
        AutoStart.UpgradeLegacyEntry();
        var host = new AppHost(!trayOnly || page is not null || trace is not null, page, trace);
        instance.ListenForShow(host.ShowFromAnotherLaunch);
        Application.Run(host);
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
