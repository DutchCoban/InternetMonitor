using System.Runtime.InteropServices;
using System.Text;

namespace InternetMonitor.Configuration;

/// <summary>
/// Protects the credentials in settings.json (the webhook URL, the Uptime Kuma push URL, endpoint
/// header values) with Windows' Data Protection API, bound to the signed-in user: the file can be
/// read by anyone who can read the folder, the values only by this user on this machine.
///
/// A protected value is written as <c>dpapi:&lt;base64&gt;</c>, so a value without that prefix is
/// plain text. That is deliberate: a user may always type a credential into settings.json by
/// hand, and the app tells the two apart by looking rather than by a flag that could be wrong.
/// Whether plain values are then encrypted on the next save is the user's choice
/// (<see cref="AppSettings.ProtectSecrets"/>).
///
/// Classic DllImport on crypt32 rather than the ProtectedData NuGet package: two functions, and
/// the package is not part of the shared framework this app ships.
/// </summary>
internal static class SecretProtection
{
    public const string Prefix = "dpapi:";

    private const uint CryptProtectUiForbidden = 0x1;

    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The protected form of <paramref name="plaintext"/>; the input itself when it is empty, already protected, or DPAPI is unavailable.</summary>
    public static string Protect(string plaintext)
    {
        if (plaintext.Length == 0 || IsProtected(plaintext))
        {
            return plaintext;
        }

        byte[]? protectedBytes = Transform(Encoding.UTF8.GetBytes(plaintext), protect: true);
        return protectedBytes is null ? plaintext : Prefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// The plain text behind <paramref name="value"/>: the value itself when it is not protected,
    /// null when it is protected but cannot be read here - another user's or another machine's
    /// settings file, or a damaged value.
    /// </summary>
    public static string? Unprotect(string value)
    {
        if (!IsProtected(value))
        {
            return value;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value[Prefix.Length..]);
        }
        catch (FormatException)
        {
            return null;
        }

        byte[]? plain = Transform(bytes, protect: false);
        return plain is null ? null : Encoding.UTF8.GetString(plain);
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        GCHandle pin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var inBlob = new DataBlob { cbData = input.Length, pbData = pin.AddrOfPinnedObject() };
        var outBlob = default(DataBlob);
        try
        {
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Internet Monitor", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outBlob);
            if (!ok || outBlob.pbData == IntPtr.Zero)
            {
                return null;
            }

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, result.Length);
            return result;
        }
        finally
        {
            pin.Free();
            if (outBlob.pbData != IntPtr.Zero)
            {
                LocalFree(outBlob.pbData);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy, IntPtr reserved, IntPtr promptStruct, uint flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
