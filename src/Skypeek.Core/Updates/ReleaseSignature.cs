using System.Security.Cryptography;
using System.Text;

namespace Skypeek.Core.Updates;

/// <summary>
/// Signatures on the update feeds (releases.{channel}.json). The app only installs what a signed feed lists: the feed
/// carries each package's SHA-256, so one signature covers every file, and a compromised download site cannot push an
/// update. ECDSA P-256 with SHA-256; the signed message also names the feed file, so a feed for one platform cannot be
/// passed off as another's.
/// </summary>
/// <remarks>
/// Signature file (next to the feed, "&lt;feed&gt;.sig"):
/// <code>
/// skypeek-signature-v1
/// key: 3f2a9c0d17b4e6a1
/// sig: base64
/// </code>
/// The private key stays with whoever publishes releases (a password-protected PKCS#8 file); the app carries only
/// public keys, so an old key can be retired by shipping a build that no longer has it.
/// </remarks>
public static class ReleaseSignature
{
    public const string Header = "skypeek-signature-v1";
    public const string Extension = ".sig";

    /// <summary>Short stable id of a public key: the first 8 bytes (hex) of SHA-256 over its SubjectPublicKeyInfo.</summary>
    public static string KeyId(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

    public static string Sign(ECDsa privateKey, string feedFileName, byte[] feed)
    {
        var signature = privateKey.SignData(Message(feedFileName, feed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{Header}\nkey: {KeyId(privateKey)}\nsig: {Convert.ToBase64String(signature)}\n";
    }

    /// <summary>
    /// Checks <paramref name="signatureFile"/> against the trusted public keys (PEM). Returns null when the feed is
    /// authentic, otherwise why not (for the log and the status line; the feed must then be ignored).
    /// </summary>
    public static string? Verify(IReadOnlyCollection<string> trustedPublicKeysPem, string feedFileName, byte[] feed, string? signatureFile)
    {
        if (trustedPublicKeysPem.Count == 0)
            return "this build has no update signing key";
        if (string.IsNullOrWhiteSpace(signatureFile))
            return "the update feed is not signed";
        var lines = signatureFile.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 3 || lines[0] != Header)
            return "the update signature has an unknown format";
        var keyId = Value(lines, "key");
        var sig = Value(lines, "sig");
        if (keyId is null || sig is null)
            return "the update signature has an unknown format";
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(sig);
        }
        catch (FormatException)
        {
            return "the update signature has an unknown format";
        }

        foreach (var pem in trustedPublicKeysPem)
        {
            using var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(pem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                continue;
            }
            if (KeyId(key) != keyId)
                continue;
            return key.VerifyData(Message(feedFileName, feed), signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                ? null
                : "the update feed does not match its signature (it was changed after signing)";
        }
        return $"the update feed is signed with an unknown key ({keyId})";
    }

    /// <summary>A new P-256 key pair: the private key encrypted with <paramref name="password"/>, and the public key.</summary>
    public static (string EncryptedPrivateKeyPem, string PublicKeyPem, string KeyId) CreateKey(string password)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        return (key.ExportEncryptedPkcs8PrivateKeyPem(password, pbe), key.ExportSubjectPublicKeyInfoPem(), KeyId(key));
    }

    public static ECDsa LoadPrivateKey(string encryptedPem, string password)
    {
        var key = ECDsa.Create();
        key.ImportFromEncryptedPem(encryptedPem, password);
        return key;
    }

    private static byte[] Message(string feedFileName, byte[] feed)
    {
        var prefix = Encoding.UTF8.GetBytes($"{Header}\n{Path.GetFileName(feedFileName)}\n");
        return [.. prefix, .. feed];
    }

    private static string? Value(string[] lines, string name) =>
        lines.Where(l => l.StartsWith(name + ":", StringComparison.Ordinal)).Select(l => l[(name.Length + 1)..].Trim()).FirstOrDefault();
}
