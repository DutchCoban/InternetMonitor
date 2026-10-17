using System.Security.Cryptography;
using InternetMonitor.Core.Updates;
using Xunit;

namespace InternetMonitor.Tests.Core.Updates;

/// <summary>
/// The self-update's trust decision: a download is installed only when it verifies against the
/// embedded release key. These run against a throwaway key pair made here, the same way
/// tools/ReleaseSigner makes the real one.
/// </summary>
public sealed class ReleaseSignatureTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"im-sig-{Guid.NewGuid():N}.bin");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKey;

    public ReleaseSignatureTests()
    {
        _publicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        File.WriteAllBytes(_file, Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray());
    }

    public void Dispose()
    {
        _key.Dispose();
        File.Delete(_file);
    }

    private byte[] Sign() => _key.SignData(File.ReadAllBytes(_file), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    [Fact]
    public void A_signature_from_the_matching_key_verifies()
    {
        Assert.True(ReleaseSignature.Verify(_file, Sign(), _publicKey));
    }

    [Fact]
    public void A_changed_file_does_not_verify()
    {
        byte[] signature = Sign();
        byte[] bytes = File.ReadAllBytes(_file);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(_file, bytes);

        Assert.False(ReleaseSignature.Verify(_file, signature, _publicKey));
    }

    [Fact]
    public void A_signature_from_another_key_does_not_verify()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] signature = other.SignData(File.ReadAllBytes(_file), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.False(ReleaseSignature.Verify(_file, signature, _publicKey));
    }

    [Fact]
    public void A_signature_of_the_wrong_size_or_a_missing_file_is_simply_invalid()
    {
        Assert.False(ReleaseSignature.Verify(_file, new byte[10], _publicKey));
        Assert.False(ReleaseSignature.Verify(_file + ".missing", Sign(), _publicKey));
        Assert.False(ReleaseSignature.Verify(_file, Sign(), "not a key"));
    }

    [Fact]
    public void Parse_accepts_the_sig_asset_text_and_rejects_anything_else()
    {
        byte[] signature = Sign();

        Assert.Equal(signature, ReleaseSignature.Parse(Convert.ToBase64String(signature) + "\r\n"));
        Assert.Null(ReleaseSignature.Parse("not base64!"));
        Assert.Null(ReleaseSignature.Parse(Convert.ToBase64String(new byte[32])));
    }

    [Fact]
    public void The_embedded_release_key_is_a_p256_public_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(ReleaseSignature.PublicKeyBase64), out int read);

        Assert.Equal(256, key.KeySize);
        Assert.Equal(Convert.FromBase64String(ReleaseSignature.PublicKeyBase64).Length, read);
    }
}
