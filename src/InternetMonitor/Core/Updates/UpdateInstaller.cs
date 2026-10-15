using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace InternetMonitor.Core.Updates;

/// <summary>
/// Downloads a new build of this app and swaps it in for the one currently running.
///
/// A running .exe cannot overwrite its own file, so the actual swap-and-restart happens in a tiny
/// PowerShell command spawned just before this process exits: wait for this process (and with it
/// the named mutex <see cref="App.SingleInstanceManager"/> holds) to be fully gone, move the
/// downloaded file over the current one, then start it again. PowerShell is an OS tool, not a new
/// project dependency - the same "hand off to the shell" approach <c>SettingsPage</c> already uses
/// to open a release page in the browser. The script is passed as a base64 <c>-EncodedCommand</c>
/// rather than quoted arguments, which sidesteps every quoting hazard a file path could otherwise
/// trigger.
///
/// No signature check: there is no code-signing infrastructure for this project, and this trusts
/// exactly the same unsigned asset a user who downloads and replaces the exe by hand already
/// would. A simple size sanity check guards only against a truncated or empty download.
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>A real build is tens of MB; anything smaller than this is treated as a failed/truncated download rather than applied.</summary>
    private const long MinimumAssetBytes = 1_000_000;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient = new() { Timeout = DownloadTimeout };

    public UpdateInstaller() => _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("InternetMonitor");

    /// <summary>
    /// Downloads <paramref name="assetUrl"/> and queues it to replace <paramref name="currentExePath"/>
    /// once this process exits. Returns true once the download has succeeded and the replace/restart
    /// helper has been launched - the caller is then free to shut the app down on its own schedule
    /// (e.g. after showing a countdown). Returns false on any failure, having started nothing.
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(string assetUrl, string currentExePath, CancellationToken cancellationToken)
    {
        // A unique name per attempt, so a half-written file from an earlier failed download can
        // never be mistaken for this one.
        string downloadPath = Path.Combine(Path.GetTempPath(), $"InternetMonitor-update-{Guid.NewGuid():N}.exe");

        try
        {
            // ResponseHeadersRead streams the ~50 MB body to disk instead of buffering it in memory.
            using HttpResponseMessage response = await _httpClient.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream destination = File.Create(downloadPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            var downloaded = new FileInfo(downloadPath);
            if (!downloaded.Exists || downloaded.Length < MinimumAssetBytes)
            {
                TryDelete(downloadPath);
                return false;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            TryDelete(downloadPath);
            return false;
        }

        try
        {
            LaunchSwapHelper(downloadPath, currentExePath);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            TryDelete(downloadPath);
            return false;
        }
    }

    /// <summary>
    /// Spawns the hidden PowerShell process that performs the actual file swap and restart once
    /// this process has exited - see the class doc comment for why this indirection is necessary.
    /// </summary>
    private static void LaunchSwapHelper(string downloadedExePath, string currentExePath)
    {
        string script =
            $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; " +
            "Start-Sleep -Milliseconds 750; " +
            $"Move-Item -Force -LiteralPath '{EscapeSingleQuotes(downloadedExePath)}' -Destination '{EscapeSingleQuotes(currentExePath)}'; " +
            $"Start-Process -FilePath '{EscapeSingleQuotes(currentExePath)}'";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process.Start(startInfo);
    }

    /// <summary>PowerShell's single-quoted strings only need the quote itself escaped (doubled), which is all a Windows file path could ever contain from this set.</summary>
    private static string EscapeSingleQuotes(string value) => value.Replace("'", "''");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a failed download - leaving a stray temp file behind is not worth surfacing further.
        }
    }
}
