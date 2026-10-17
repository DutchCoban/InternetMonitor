using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace InternetMonitor.Core.Updates;

/// <summary>
/// Downloads a new build of this app, checks it, and swaps it in for the one currently running.
///
/// Nothing is installed that was not checked three ways first: the file must carry the
/// maintainer's signature (<see cref="ReleaseSignature"/>), its version resource must be the
/// version the release claims to be, and it must at least be the size of a real build. The file
/// is downloaded next to the running executable, as <c>InternetMonitor.exe.new</c>, rather than
/// into the temp folder: whoever can tamper with it there could tamper with the executable itself.
///
/// A running .exe cannot overwrite its own file, so the actual swap-and-restart happens in a tiny
/// PowerShell command spawned just before this process exits: wait for this process (and with it
/// the named mutex <see cref="App.SingleInstanceManager"/> holds) to be fully gone, move the
/// downloaded file over the current one, then start it again. PowerShell is an OS tool, not a new
/// project dependency, started by its full path under the system directory so nothing with the
/// same name next to the app could stand in for it. The script is passed as a base64
/// <c>-EncodedCommand</c> rather than quoted arguments, which sidesteps every quoting hazard a file
/// path could otherwise trigger.
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>A real build is tens of MB; anything smaller than this is treated as a failed/truncated download rather than applied.</summary>
    private const long DefaultMinimumAssetBytes = 1_000_000;

    /// <summary>A signature asset is under a hundred bytes of base64.</summary>
    private const long MaxSignatureBytes = 4096;

    /// <summary>What the download is called while it waits next to the executable it will replace.</summary>
    public const string PendingSuffix = ".new";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;

    public UpdateInstaller()
        : this(new HttpClientHandler())
    {
    }

    /// <summary><paramref name="handler"/> lets a test serve the download from memory.</summary>
    public UpdateInstaller(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler) { Timeout = DownloadTimeout };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("InternetMonitor");
    }

    /// <summary>Below this size a download is treated as truncated. Lowered only by tests, which sign small files.</summary>
    public long MinimumAssetBytes { get; init; } = DefaultMinimumAssetBytes;

    /// <summary>The key the download must be signed with; the release key unless a test says otherwise.</summary>
    public string PublicKeyBase64 { get; init; } = ReleaseSignature.PublicKeyBase64;

    /// <summary>
    /// Downloads and checks the release in <paramref name="update"/>, then queues it to replace
    /// <paramref name="currentExePath"/> once this process exits. Returns true once the download
    /// has been verified and the replace/restart helper has been launched - the caller is then
    /// free to shut the app down on its own schedule (e.g. after showing a countdown). Returns
    /// false on any failure, having started nothing and left no download behind.
    /// </summary>
    public async Task<bool> DownloadAndApplyAsync(UpdateCheckResult update, string currentExePath, CancellationToken cancellationToken)
    {
        string? downloaded = await DownloadAndVerifyAsync(update, currentExePath, cancellationToken).ConfigureAwait(false);
        if (downloaded is null)
        {
            return false;
        }

        try
        {
            LaunchSwapHelper(downloaded, currentExePath);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            TryDelete(downloaded);
            return false;
        }
    }

    /// <summary>
    /// The download and every check, without the swap: the path of the verified file next to
    /// <paramref name="currentExePath"/>, or null when anything about it was not right. Public so
    /// the checks can be exercised without replacing an executable.
    /// </summary>
    public async Task<string?> DownloadAndVerifyAsync(UpdateCheckResult update, string currentExePath, CancellationToken cancellationToken)
    {
        if (update.AssetDownloadUrl is not { } assetUrl || update.SignatureDownloadUrl is not { } signatureUrl || update.Latest is not { } expected
            || !UpdateChecker.IsTrustedAssetUrl(assetUrl) || !UpdateChecker.IsTrustedAssetUrl(signatureUrl))
        {
            return null;
        }

        string downloadPath = currentExePath + PendingSuffix;
        TryDelete(downloadPath);

        try
        {
            byte[]? signature = await DownloadSignatureAsync(signatureUrl, cancellationToken).ConfigureAwait(false);
            if (signature is null)
            {
                return null;
            }

            // ResponseHeadersRead streams the ~50 MB body to disk instead of buffering it in memory.
            using HttpResponseMessage response = await _httpClient.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream destination = File.Create(downloadPath))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            if (!IsAcceptable(downloadPath, signature, expected))
            {
                TryDelete(downloadPath);
                return null;
            }

            return downloadPath;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            TryDelete(downloadPath);
            return null;
        }
    }

    private async Task<byte[]?> DownloadSignatureAsync(string signatureUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(signatureUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxSignatureBytes)
        {
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxSignatureBytes + 1];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return total > MaxSignatureBytes ? null : ReleaseSignature.Parse(Encoding.ASCII.GetString(buffer, 0, total));
    }

    /// <summary>Size, signature, then the version resource - in that order, cheapest check first.</summary>
    private bool IsAcceptable(string downloadPath, byte[] signature, Version expected)
    {
        var downloaded = new FileInfo(downloadPath);
        if (!downloaded.Exists || downloaded.Length < MinimumAssetBytes)
        {
            return false;
        }

        if (!ReleaseSignature.Verify(downloadPath, signature, PublicKeyBase64))
        {
            return false;
        }

        return HasVersion(downloadPath, expected);
    }

    /// <summary>
    /// Whether the file's version resource says <paramref name="expected"/>, on the three parts a
    /// release tag has. A signed but differently versioned file is the wrong release - an old one
    /// re-uploaded, say - and is not installed.
    /// </summary>
    public static bool HasVersion(string filePath, Version expected)
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(filePath);
        return info.FileMajorPart == expected.Major
            && info.FileMinorPart == expected.Minor
            && info.FileBuildPart == Math.Max(0, expected.Build);
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
            $"Start-Process -FilePath '{EscapeSingleQuotes(currentExePath)}' -ArgumentList '{App.AutoStart.TrayArgument}'";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo(PowerShellPath)
        {
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process.Start(startInfo);
    }

    /// <summary>
    /// Windows PowerShell by its full path. A bare "powershell.exe" would be looked up in the
    /// application's own directory and the current directory first, where anyone who can write
    /// next to the app could plant a file by that name.
    /// </summary>
    public static string PowerShellPath =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

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
            // Best-effort cleanup of a failed download - leaving a stray file behind is not worth surfacing further.
        }
    }
}
