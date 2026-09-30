using System.Text;
using Skypeek.Core.Updates;

namespace Skypeek.Tests;

public class ReleaseSignatureTests
{
    private static readonly byte[] Feed = Encoding.UTF8.GetBytes("""{"Assets":[{"PackageId":"SkypeekApp","Version":"1.2.0","Type":"Full","FileName":"SkypeekApp-1.2.0-full.nupkg","SHA256":"ab"}]}""");

    private static (string Private, string Public) NewKey() =>
        ReleaseSignature.CreateKey("test-password") is var (priv, pub, _) ? (priv, pub) : default;

    private static string SignWith(string privatePem, string fileName, byte[] feed)
    {
        using var key = ReleaseSignature.LoadPrivateKey(privatePem, "test-password");
        return ReleaseSignature.Sign(key, fileName, feed);
    }

    [Fact]
    public void Signed_feed_verifies_with_its_public_key()
    {
        var (priv, pub) = NewKey();
        var sig = SignWith(priv, "releases.win.json", Feed);
        Assert.Null(ReleaseSignature.Verify([pub], "releases.win.json", Feed, sig));
    }

    [Fact]
    public void Changed_feed_is_rejected()
    {
        var (priv, pub) = NewKey();
        var sig = SignWith(priv, "releases.win.json", Feed);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Feed).Replace("\"ab\"", "\"cd\""));
        Assert.Contains("does not match", ReleaseSignature.Verify([pub], "releases.win.json", tampered, sig));
    }

    [Fact]
    public void Feed_signed_for_another_platform_is_rejected()
    {
        var (priv, pub) = NewKey();
        var linuxSig = SignWith(priv, "releases.linux.json", Feed);
        Assert.NotNull(ReleaseSignature.Verify([pub], "releases.win.json", Feed, linuxSig));
    }

    [Fact]
    public void Unknown_key_missing_or_malformed_signature_and_no_trusted_keys_are_rejected()
    {
        var (priv, _) = NewKey();
        var (_, otherPub) = NewKey();
        var sig = SignWith(priv, "releases.win.json", Feed);
        Assert.Contains("unknown key", ReleaseSignature.Verify([otherPub], "releases.win.json", Feed, sig));
        Assert.Contains("not signed", ReleaseSignature.Verify([otherPub], "releases.win.json", Feed, null));
        Assert.Contains("unknown format", ReleaseSignature.Verify([otherPub], "releases.win.json", Feed, "hello"));
        Assert.Contains("unknown format", ReleaseSignature.Verify([otherPub], "releases.win.json", Feed, $"{ReleaseSignature.Header}\nkey: 00\nsig: not-base64!\n"));
        Assert.Contains("no update signing key", ReleaseSignature.Verify([], "releases.win.json", Feed, sig));
    }

    [Fact]
    public void Any_of_several_trusted_keys_can_sign_so_keys_can_be_rotated()
    {
        var (oldPriv, oldPub) = NewKey();
        var (_, newPub) = NewKey();
        Assert.Null(ReleaseSignature.Verify([newPub, oldPub], "releases.win.json", Feed, SignWith(oldPriv, "releases.win.json", Feed)));
    }

    [Fact]
    public void Private_key_needs_its_password()
    {
        var (priv, _) = NewKey();
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => ReleaseSignature.LoadPrivateKey(priv, "wrong"));
    }
}
