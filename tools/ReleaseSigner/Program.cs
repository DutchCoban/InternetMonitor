using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace InternetMonitor.Tools.ReleaseSigner;

/// <summary>
/// The maintainer's half of release signing, plus the release's check at VirusTotal. The app ships
/// the public key and refuses to install an update whose signature does not verify against it;
/// this tool holds the other end.
///
/// <code>
///   keygen     &lt;private-key.pem&gt;           makes a new ECDSA P-256 key pair; prints the public key to embed
///   sign       &lt;private-key.pem&gt; &lt;file&gt;    writes &lt;file&gt;.sig (base64, IEEE P1363, SHA-256)
///   verify     &lt;public-key-base64&gt; &lt;file&gt;  checks &lt;file&gt;.sig the way the app does
///   virustotal &lt;api-key-file&gt; &lt;file&gt;       has VirusTotal scan &lt;file&gt; (or reads its existing report) and prints the verdict and the report link
/// </code>
///
/// The private key and the VirusTotal API key stay with the maintainer, outside the repository.
/// Losing the private key means the next release needs a new pair and users have to download that
/// release by hand once.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["keygen", var keyPath] => KeyGen(keyPath),
                ["sign", var keyPath, var file] => Sign(keyPath, file),
                ["verify", var publicKey, var file] => Verify(publicKey, file),
                ["virustotal", var apiKeyFile, var file] => VirusTotalScan.RunAsync(apiKeyFile, file).GetAwaiter().GetResult(),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException
            or HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: ReleaseSigner keygen <private-key.pem>");
        Console.Error.WriteLine("       ReleaseSigner sign <private-key.pem> <file>");
        Console.Error.WriteLine("       ReleaseSigner verify <public-key-base64> <file>");
        Console.Error.WriteLine("       ReleaseSigner virustotal <api-key-file> <file>");
        return 1;
    }

    private static int KeyGen(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            Console.Error.WriteLine($"Refusing to overwrite {keyPath}");
            return 2;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem() + Environment.NewLine);
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static int Sign(string keyPath, string file)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));

        byte[] signature;
        using (FileStream stream = File.OpenRead(file))
        {
            signature = key.SignData(stream, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        string signaturePath = file + ".sig";
        File.WriteAllText(signaturePath, Convert.ToBase64String(signature) + Environment.NewLine);
        Console.WriteLine(signaturePath);
        return 0;
    }

    private static int Verify(string publicKeyBase64, string file)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        byte[] signature = Convert.FromBase64String(File.ReadAllText(file + ".sig").Trim());

        bool valid;
        using (FileStream stream = File.OpenRead(file))
        {
            valid = key.VerifyData(stream, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        Console.WriteLine(valid ? "valid" : "INVALID");
        return valid ? 0 : 3;
    }
}

/// <summary>
/// Has VirusTotal look at a release executable before it is published. A report is keyed by the
/// file's SHA-256, so the link printed here is the one a user of the published asset lands on;
/// a file VirusTotal has already seen is not uploaded again. One heuristic hit on an unsigned
/// single-file .NET executable happens and is only reported; from two engines calling the file
/// malicious the step fails (exit code 4) and the maintainer decides.
///
/// Uploading makes the file available to VirusTotal and its partners - fine for an executable
/// that is about to be public anyway, but not a step to run on anything private. The free API
/// allows four requests a minute and five hundred a day; a release needs a handful.
/// </summary>
internal static class VirusTotalScan
{
    private const string Api = "https://www.virustotal.com/api/v3";
    private const int MaliciousToFail = 2;
    private const long DirectUploadLimit = 32 * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AnalysisBudget = TimeSpan.FromMinutes(15);

    private sealed record Verdict(int Engines, int Malicious, int Suspicious, IReadOnlyList<string> Flags);

    public static async Task<int> RunAsync(string apiKeyFile, string file)
    {
        if (!File.Exists(apiKeyFile))
        {
            Console.Error.WriteLine($"No VirusTotal API key at {apiKeyFile}. Create a free account at virustotal.com, copy its API key into that file (one line), and keep the file outside the repository.");
            return 2;
        }

        string apiKey = File.ReadAllText(apiKeyFile).Trim();
        if (apiKey.Length == 0)
        {
            Console.Error.WriteLine($"{apiKeyFile} is empty.");
            return 2;
        }

        string sha256 = Sha256Of(file);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Add("x-apikey", apiKey);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        JsonDocument? report = await TryGetReportAsync(http, sha256).ConfigureAwait(false);
        if (report is null)
        {
            Console.WriteLine($"VirusTotal has not seen this file yet; uploading {Path.GetFileName(file)} ({new FileInfo(file).Length / (1024 * 1024)} MB)...");
            string analysisId = await UploadAsync(http, file).ConfigureAwait(false);
            await WaitForAnalysisAsync(http, analysisId).ConfigureAwait(false);
            report = await TryGetReportAsync(http, sha256).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The analysis finished but VirusTotal has no report for the file.");
        }

        Verdict verdict;
        using (report)
        {
            JsonElement attributes = report.RootElement.GetProperty("data").GetProperty("attributes");
            verdict = Summarise(attributes.GetProperty("last_analysis_stats"), attributes.GetProperty("last_analysis_results"));
        }

        string link = $"https://www.virustotal.com/gui/file/{sha256}";
        int flagged = verdict.Malicious + verdict.Suspicious;
        Console.WriteLine($"VirusTotal: {flagged} of {verdict.Engines} engines flag {Path.GetFileName(file)} (malicious {verdict.Malicious}, suspicious {verdict.Suspicious})");
        foreach (string flag in verdict.Flags)
        {
            Console.WriteLine("  " + flag);
        }

        Console.WriteLine(link);
        Console.WriteLine();
        Console.WriteLine($"- **Checked on VirusTotal.** The `{Path.GetFileName(file)}` below scores {flagged} of {verdict.Engines} engines - [file report]({link}), tied to the file's SHA-256 `{sha256}`.");

        if (verdict.Malicious >= MaliciousToFail)
        {
            Console.Error.WriteLine($"{verdict.Malicious} engines call the file malicious: not releasing this without a look.");
            return 4;
        }

        return 0;
    }

    /// <summary>The verdict as numbers and as one line per engine that objected, e.g. "Bkav: W32.AIDetectMalware (malicious)".</summary>
    private static Verdict Summarise(JsonElement stats, JsonElement results)
    {
        int Count(string name) => stats.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

        int malicious = Count("malicious");
        int suspicious = Count("suspicious");
        int engines = malicious + suspicious + Count("undetected") + Count("harmless") + Count("timeout") + Count("type-unsupported") + Count("failure") + Count("confirmed-timeout");

        var flags = new List<string>();
        foreach (JsonProperty engine in results.EnumerateObject())
        {
            string category = engine.Value.TryGetProperty("category", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
            if (category is "malicious" or "suspicious")
            {
                string label = engine.Value.TryGetProperty("result", out JsonElement r) ? r.GetString() ?? "-" : "-";
                flags.Add($"{engine.Name}: {label} ({category})");
            }
        }

        flags.Sort(StringComparer.OrdinalIgnoreCase);
        return new Verdict(engines, malicious, suspicious, flags);
    }

    private static async Task<JsonDocument?> TryGetReportAsync(HttpClient http, string sha256)
    {
        using HttpResponseMessage response = await http.GetAsync($"{Api}/files/{sha256}").ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureOkAsync(response, "fetching the file report").ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    /// <summary>Uploads the file, directly or - above 32 MB - through the one-time upload URL the API hands out, and returns the analysis id.</summary>
    private static async Task<string> UploadAsync(HttpClient http, string file)
    {
        string target = $"{Api}/files";
        if (new FileInfo(file).Length > DirectUploadLimit)
        {
            using HttpResponseMessage urlResponse = await http.GetAsync($"{Api}/files/upload_url").ConfigureAwait(false);
            await EnsureOkAsync(urlResponse, "requesting an upload URL").ConfigureAwait(false);
            using JsonDocument url = JsonDocument.Parse(await urlResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
            target = url.RootElement.GetProperty("data").GetString() ?? throw new InvalidOperationException("VirusTotal returned no upload URL.");
        }

        await using FileStream stream = File.OpenRead(file);
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", Path.GetFileName(file));

        using HttpResponseMessage response = await http.PostAsync(target, form).ConfigureAwait(false);
        await EnsureOkAsync(response, "uploading the file").ConfigureAwait(false);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        return json.RootElement.GetProperty("data").GetProperty("id").GetString() ?? throw new InvalidOperationException("VirusTotal returned no analysis id.");
    }

    private static async Task WaitForAnalysisAsync(HttpClient http, string analysisId)
    {
        DateTime deadline = DateTime.UtcNow + AnalysisBudget;
        while (true)
        {
            await Task.Delay(PollInterval).ConfigureAwait(false);
            using HttpResponseMessage response = await http.GetAsync($"{Api}/analyses/{analysisId}").ConfigureAwait(false);
            await EnsureOkAsync(response, "polling the analysis").ConfigureAwait(false);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            string status = json.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("status").GetString() ?? string.Empty;
            Console.WriteLine($"  analysis {status}...");
            if (status == "completed")
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"The analysis did not finish within {AnalysisBudget.TotalMinutes:F0} minutes; look it up later at the report link.");
            }
        }
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "the API key is wrong",
            HttpStatusCode.TooManyRequests => "the free quota is used up for now (4 requests a minute, 500 a day)",
            _ => await response.Content.ReadAsStringAsync().ConfigureAwait(false),
        };
        throw new HttpRequestException($"VirusTotal answered {(int)response.StatusCode} while {what}: {hint}");
    }

    private static string Sha256Of(string file)
    {
        using FileStream stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
