using System.Security.Cryptography;

namespace InternetMonitor.Tools.ReleaseSigner;

/// <summary>
/// The maintainer's half of release signing. The app ships the public key and refuses to install an
/// update whose signature does not verify against it; this tool holds the other end.
///
/// <code>
///   keygen &lt;private-key.pem&gt;         makes a new ECDSA P-256 key pair; prints the public key to embed
///   sign   &lt;private-key.pem&gt; &lt;file&gt;  writes &lt;file&gt;.sig (base64, IEEE P1363, SHA-256)
///   verify &lt;public-key-base64&gt; &lt;file&gt; checks &lt;file&gt;.sig the way the app does
/// </code>
///
/// The private key stays with the maintainer, outside the repository. Losing it means the next
/// release needs a new pair and users have to download that release by hand once.
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
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or FormatException)
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
