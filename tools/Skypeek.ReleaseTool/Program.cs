using System.Text;
using Skypeek.Core.Updates;

// skypeek-release keygen             create the update signing key (once; keep the private key and its password safe)
// skypeek-release sign <feed.json>   write <feed.json>.sig next to an update feed
// skypeek-release verify <feed.json> check a signed feed against the public keys the app ships with
//
// The private key file is password-protected; the password comes from SKYPEEK_SIGNING_PASSWORD or a prompt.
// Key file: SKYPEEK_SIGNING_KEY, or skypeek-release/update-signing-key.pem in the user's application data folder.
return Run(args);

static int Run(string[] args)
{
    try
    {
        return args switch
        {
            ["keygen"] => KeyGen(),
            ["sign", var feed] => Sign(feed),
            ["verify", var feed] => Verify(feed),
            _ => Usage(),
        };
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or InvalidOperationException)
    {
        Console.Error.WriteLine("error: " + ex.Message);
        return 1;
    }
}

static int Usage()
{
    Console.Error.WriteLine("usage: skypeek-release keygen | sign <releases.channel.json> | verify <releases.channel.json>");
    return 2;
}

static string KeyFile() =>
    Environment.GetEnvironmentVariable("SKYPEEK_SIGNING_KEY") is { Length: > 0 } path
        ? path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "skypeek-release", "update-signing-key.pem");

// The public keys the app embeds (src/Skypeek.Desktop/UpdateKeys), found from the repository root.
static string PublicKeyDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Skypeek.sln")))
            return Path.Combine(dir.FullName, "src", "Skypeek.Desktop", "UpdateKeys");
    throw new InvalidOperationException("run this from the Skypeek repository (Skypeek.sln not found above the tool)");
}

static int KeyGen()
{
    var keyFile = KeyFile();
    if (File.Exists(keyFile))
        throw new InvalidOperationException($"{keyFile} already exists; move it away first if you really want a new key (installed apps trust only the keys they were built with)");
    var password = Password(confirm: true);
    var (privatePem, publicPem, keyId) = ReleaseSignature.CreateKey(password);
    Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
    File.WriteAllText(keyFile, privatePem);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    var publicDir = PublicKeyDir();
    Directory.CreateDirectory(publicDir);
    var publicFile = Path.Combine(publicDir, keyId + ".pub.pem");
    File.WriteAllText(publicFile, publicPem);
    Console.WriteLine($"Private key (password-protected): {keyFile}");
    Console.WriteLine($"Public key (commit it; the app trusts it): {publicFile}");
    Console.WriteLine("Back up the private key file and its password: without them, installed copies cannot be updated.");
    return 0;
}

static int Sign(string feed)
{
    var keyFile = KeyFile();
    if (!File.Exists(keyFile))
        throw new InvalidOperationException($"no signing key at {keyFile} (create one with: skypeek-release keygen)");
    using var key = ReleaseSignature.LoadPrivateKey(File.ReadAllText(keyFile), Password(confirm: false));
    var bytes = File.ReadAllBytes(feed);
    File.WriteAllText(feed + ReleaseSignature.Extension, ReleaseSignature.Sign(key, Path.GetFileName(feed), bytes));
    // Refuse to publish with a key the app does not trust.
    var problem = ReleaseSignature.Verify(PublicKeys(), Path.GetFileName(feed), bytes, File.ReadAllText(feed + ReleaseSignature.Extension));
    if (problem is not null)
        throw new InvalidOperationException($"signed, but the app would reject it: {problem}");
    Console.WriteLine($"Signed {feed} (key {ReleaseSignature.KeyId(key)})");
    return 0;
}

static int Verify(string feed)
{
    var sig = feed + ReleaseSignature.Extension;
    var problem = ReleaseSignature.Verify(PublicKeys(), Path.GetFileName(feed), File.ReadAllBytes(feed), File.Exists(sig) ? File.ReadAllText(sig) : null);
    Console.WriteLine(problem is null ? $"OK: {feed} is signed with a trusted key" : $"REJECTED: {problem}");
    return problem is null ? 0 : 1;
}

static List<string> PublicKeys()
{
    var dir = PublicKeyDir();
    return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.pub.pem").Select(File.ReadAllText).ToList() : [];
}

static string Password(bool confirm)
{
    if (Environment.GetEnvironmentVariable("SKYPEEK_SIGNING_PASSWORD") is { Length: > 0 } fromEnv)
        return fromEnv;
    if (Console.IsInputRedirected)
        throw new InvalidOperationException("no terminal to ask for the key password; set SKYPEEK_SIGNING_PASSWORD");
    var password = Prompt("Signing key password: ");
    if (confirm)
    {
        if (password.Length < 12)
            throw new InvalidOperationException("use a password of at least 12 characters");
        if (Prompt("Repeat the password: ") != password)
            throw new InvalidOperationException("the passwords differ");
    }
    return password;
}

static string Prompt(string label)
{
    Console.Write(label);
    var text = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
            break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (text.Length > 0)
                text.Length--;
        }
        else if (!char.IsControl(key.KeyChar))
            text.Append(key.KeyChar);
    }
    Console.WriteLine();
    return text.ToString();
}
