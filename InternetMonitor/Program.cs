using InternetMonitor.Startup;
using InternetMonitor.UI;

namespace InternetMonitor;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Without this, an unhandled exception on the UI thread (e.g. in an event handler)
        // takes the whole tray app down silently - no dialog, no tray icon, no trace. Catching
        // it here means a bug shows up as a message box and a line in crash.log instead of the
        // app just vanishing.
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
        System.Windows.Forms.Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        using var singleInstance = new SingleInstanceManager("InternetMonitor.SingleInstance.Mutex");
        if (!singleInstance.TryAcquire())
        {
            return;
        }

        System.Windows.Forms.Application.Run(new TrayApplicationContext());
    }

    private static void ReportCrash(Exception? exception)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InternetMonitor", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTimeOffset.UtcNow:O}\r\n{exception}\r\n\r\n");
        }
        catch (IOException)
        {
            // Crash logging must never itself throw.
        }

        System.Windows.Forms.MessageBox.Show(
            $"Er is een onverwachte fout opgetreden:\r\n\r\n{exception?.Message}",
            "Internet Monitor",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Error);
    }
}
