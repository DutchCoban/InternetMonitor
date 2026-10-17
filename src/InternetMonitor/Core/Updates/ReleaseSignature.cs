using System.Security.Cryptography;

namespace InternetMonitor.Core.Updates;

/// <summary>
/// Checks that a downloaded build was signed by the maintainer's release key before the self-update
/// installs it. Every release ships <c>InternetMonitor.exe.sig</c> next to the executable: an ECDSA
/// P-256 signature over the file's SHA-256, made with <c>tools/ReleaseSigner</c>, whose private key
/// never enters the repository. The public half is embedded here.
///
/// This is what makes the self-update trust something stronger than "the bytes came over TLS from
/// GitHub": a release uploaded by anyone else - a stolen token, a compromised account - does not
/// carry a valid signature and is not installed. A user who downloads by hand can run the same
/// check with the tool's <c>verify</c> command.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>The release key's public half (SubjectPublicKeyInfo, base64). Changing it orphans every earlier release's self-update, so it is not to be rotated lightly.</summary>
    public const string PublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAECf8lgzCMp9yzOlm9ArI71yQrCkrVwlrAWBujsFk/ICVRyCmBbiSw4NSeXPriDGCPoy9dANqm8b+YUE17yB7xDg==";

    /// <summary>The release asset that carries the signature for <c>InternetMonitor.exe</c>.</summary>
    public const string SignatureAssetName = "InternetMonitor.exe.sig";

    /// <summary>IEEE P1363 for P-256: r and s, 32 bytes each.</summary>
    private const int SignatureLength = 64;

    public static bool Verify(string filePath, byte[] signature) => Verify(filePath, signature, PublicKeyBase64);

    /// <summary>
    /// True only when <paramref name="signature"/> is a valid signature of the file's contents under
    /// <paramref name="publicKeyBase64"/>. Any failure to read or parse anything is "not valid".
    /// </summary>
    public static bool Verify(string filePath, byte[] signature, string publicKeyBase64)
    {
        if (signature.Length != SignatureLength)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            using FileStream stream = File.OpenRead(filePath);
            return key.VerifyData(stream, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Decodes the text of a <c>.sig</c> asset; null when it is not a base64 signature of the right size.</summary>
    public static byte[]? Parse(string signatureText)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(signatureText.Trim());
            return bytes.Length == SignatureLength ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
