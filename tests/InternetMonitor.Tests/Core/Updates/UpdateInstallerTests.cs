using System.Net;
using System.Security.Cryptography;
using InternetMonitor.Core.Updates;
using Xunit;

namespace InternetMonitor.Tests.Core.Updates;

/// <summary>
/// The download-and-check half of the self-update, against an in-memory "GitHub": the file lands
/// next to the executable only when its signature and version resource are right, and nothing is
/// left behind otherwise. The test assembly itself stands in for the release build - a real PE
/// with a version resource - signed with a throwaway key.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private const string AssetUrl = "https://github.com/DutchCoban/InternetMonitor/releases/download/v1.0.0/InternetMonitor.exe";
    private const string SignatureUrl = "https://github.com/DutchCoban/InternetMonitor/releases/download/v1.0.0/InternetMonitor.exe.sig";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"im-update-{Guid.NewGuid():N}");
    private readonly string _currentExe;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKey;
    private readonly byte[] _build;
    private readonly Version _buildVersion;

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(_directory);
        _currentExe = Path.Combine(_directory, "InternetMonitor.exe");
        _publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        string assembly = typeof(UpdateInstallerTests).Assembly.Location;
        _build = File.ReadAllBytes(assembly);
        System.Diagnostics.FileVersionInfo info = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly);
        _buildVersion = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string Sign(byte[] bytes) =>
        Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private UpdateInstaller Installer(Dictionary<string, byte[]> responses) =>
        new(new FakeHandler(responses)) { MinimumAssetBytes = 1, PublicKeyBase64 = _publicKey };

    private UpdateCheckResult Update(Version? version = null, string? assetUrl = AssetUrl, string? signatureUrl = SignatureUrl) =>
        new(true, version ?? _buildVersion, "https://github.com/DutchCoban/InternetMonitor/releases", assetUrl, signatureUrl);

    private Dictionary<string, byte[]> Responses(byte[]? build = null, string? signature = null) => new()
    {
        [AssetUrl] = build ?? _build,
        [SignatureUrl] = System.Text.Encoding.ASCII.GetBytes((signature ?? Sign(build ?? _build)) + "\n"),
    };

    [Fact]
    public async Task A_signed_build_of_the_expected_version_lands_next_to_the_executable()
    {
        string? path = await Installer(Responses()).DownloadAndVerifyAsync(Update(), _currentExe, CancellationToken.None);

        Assert.Equal(_currentExe + UpdateInstaller.PendingSuffix, path);
        Assert.Equal(_build, File.ReadAllBytes(path!));
    }

    [Fact]
    public async Task A_build_whose_bytes_do_not_match_the_signature_is_deleted()
    {
        byte[] tampered = [.. _build];
        tampered[^1] ^= 0x01;
        Dictionary<string, byte[]> responses = Responses(build: tampered, signature: Sign(_build));

        string? path = await Installer(responses).DownloadAndVerifyAsync(Update(), _currentExe, CancellationToken.None);

        Assert.Null(path);
        Assert.False(File.Exists(_currentExe + UpdateInstaller.PendingSuffix));
    }

    [Fact]
    public async Task A_signature_from_another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string signature = Convert.ToBase64String(other.SignData(_build, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        string? path = await Installer(Responses(signature: signature)).DownloadAndVerifyAsync(Update(), _currentExe, CancellationToken.None);

        Assert.Null(path);
        Assert.False(File.Exists(_currentExe + UpdateInstaller.PendingSuffix));
    }

    [Fact]
    public async Task A_build_with_a_different_version_resource_than_the_release_claims_is_refused()
    {
        var wrong = new Version(_buildVersion.Major + 1, 0, 0);

        string? path = await Installer(Responses()).DownloadAndVerifyAsync(Update(version: wrong), _currentExe, CancellationToken.None);

        Assert.Null(path);
        Assert.False(File.Exists(_currentExe + UpdateInstaller.PendingSuffix));
    }

    [Fact]
    public async Task Without_a_signature_asset_or_with_an_unparseable_one_nothing_is_downloaded()
    {
        var installer = Installer(Responses(signature: "garbage"));

        Assert.Null(await installer.DownloadAndVerifyAsync(Update(signatureUrl: null), _currentExe, CancellationToken.None));
        Assert.Null(await installer.DownloadAndVerifyAsync(Update(), _currentExe, CancellationToken.None));
        Assert.False(File.Exists(_currentExe + UpdateInstaller.PendingSuffix));
    }

    [Fact]
    public async Task An_asset_url_off_github_is_never_fetched()
    {
        var handler = new FakeHandler(Responses());
        var installer = new UpdateInstaller(handler) { MinimumAssetBytes = 1, PublicKeyBase64 = _publicKey };

        string? path = await installer.DownloadAndVerifyAsync(Update(assetUrl: "https://evil.example/InternetMonitor.exe"), _currentExe, CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public void PowerShell_is_started_by_its_full_system_path()
    {
        Assert.StartsWith(Environment.SystemDirectory, UpdateInstaller.PowerShellPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\WindowsPowerShell\v1.0\powershell.exe", UpdateInstaller.PowerShellPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Answers each known URL with its bytes and anything else with 404.</summary>
    private sealed class FakeHandler(Dictionary<string, byte[]> responses) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);

            return Task.FromResult(responses.TryGetValue(url, out byte[]? body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
