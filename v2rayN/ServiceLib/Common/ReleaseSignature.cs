using System.Security.Cryptography;

namespace ServiceLib.Common;

/// <summary>
/// Signatures of release archives. The private key lives with the maintainer, outside GitHub, encrypted with a
/// password (tools/detour-sign.cs); the public key is built in here. The updater installs an archive only when
/// "&lt;archive&gt;.sig" from the same release verifies, so a hijacked GitHub account or a replaced asset (with its
/// SHA-256 replaced too) can't push code to users. ECDSA P-256 over SHA-256; the .sig file holds the
/// IEEE P1363 signature in base64.
/// </summary>
public static class ReleaseSignature
{
    /// <summary>Base64 SubjectPublicKeyInfo of the release key (created 2026-11-19).</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIIhNJBc6K/We/ki2CgozBJNj6Sah95UbzX5DfaLYnOGeprIwTIOBroB2ZE3ifyXj0ad/2FlSBGBBwLoyprVXRA==";

    public const string Suffix = ".sig";

    public static bool HasKey => PublicKey.Length > 0;

    /// <summary>The signature bytes from a .sig file's text, or null when it isn't one.</summary>
    public static byte[]? Parse(string? text)
    {
        var line = text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
        if (line is null || line.Length > 200)
        {
            return null;
        }
        try
        {
            var bytes = Convert.FromBase64String(line);
            return bytes.Length == 64 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool Verify(Stream data, byte[] signature, string publicKey)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="sigText"/> is a valid signature of the file by the built-in (or given) key.</summary>
    public static bool VerifyFile(string file, string? sigText, string? publicKey = null)
    {
        publicKey ??= PublicKey;
        var signature = Parse(sigText);
        if (signature is null || publicKey.Length == 0)
        {
            return false;
        }
        using var fs = File.OpenRead(file);
        return Verify(fs, signature, publicKey);
    }
}
