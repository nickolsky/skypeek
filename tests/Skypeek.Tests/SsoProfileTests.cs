using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skypeek.Aws;
using Skypeek.Core.Credentials;

namespace Skypeek.Tests;

public class SsoProfileTests
{
    private const string Config = """
        [profile 111122223333_ReadOnlyAccess]
        sso_session = company
        sso_account_id = 111122223333
        sso_role_name = ReadOnlyAccess
        region = us-east-1
        output = json

        [sso-session company]
        sso_start_url = https://example.awsapps.com/start
        sso_region = us-east-1
        sso_registration_scopes = sso:account:access

        [profile legacy-admin]
        sso_start_url = https://legacy.awsapps.com/start
        sso_region = eu-west-1
        sso_account_id = 444455556666
        sso_role_name = AdministratorAccess

        [profile plain]
        region = us-west-2
        """;

    private static string CacheFile(string dir, string key) =>
        Path.Combine(dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant() + ".json");

    private static void WriteToken(string dir, string key, string token, DateTime expiresUtc) =>
        File.WriteAllText(CacheFile(dir, key), JsonSerializer.Serialize(new { startUrl = "x", region = "us-east-1", accessToken = token, expiresAt = expiresUtc.ToString("yyyy-MM-ddTHH:mm:ssZ") }));

    [Fact]
    public void Sso_profiles_from_both_config_layouts()
    {
        using var dir = new TempDir();
        WriteToken(dir.Path, "company", "token-1", DateTime.UtcNow.AddHours(8));

        var profiles = ProfileReader.ParseSso(Config, dir.Path);

        Assert.Equal(2, profiles.Count); // "plain" has no SSO settings
        var ro = profiles["111122223333_ReadOnlyAccess"];
        Assert.True(ro.IsSso);
        Assert.True(ro.IsReadOnly);
        Assert.Equal("111122223333", ro.AccountId);
        Assert.Equal("us-east-1", ro.DefaultRegion);
        Assert.Equal("https://example.awsapps.com/start", ro.Sso!.StartUrl);
        Assert.Equal("token-1", ro.Sso.AccessToken);
        Assert.True(ro.Sso.IsSignedIn(DateTime.UtcNow));
        Assert.DoesNotContain("token-1", ro.Sso.ToString());

        // Old layout: start URL on the profile, cache file named after the start URL; no token yet.
        var legacy = profiles["legacy-admin"];
        Assert.Equal("eu-west-1", legacy.Sso!.Region);
        Assert.False(legacy.IsReadOnly);
        Assert.Null(legacy.Sso.AccessToken);
        Assert.False(legacy.Sso.IsSignedIn(DateTime.UtcNow));
        Assert.Equal(CacheFile(dir.Path, "https://legacy.awsapps.com/start"), legacy.Sso.TokenCacheFile);
    }

    [Fact]
    public void New_sign_in_changes_the_fingerprint()
    {
        using var dir = new TempDir();
        WriteToken(dir.Path, "company", "token-1", DateTime.UtcNow.AddHours(8));
        var first = ProfileReader.ParseSso(Config, dir.Path)["111122223333_ReadOnlyAccess"].Fingerprint;
        WriteToken(dir.Path, "company", "token-2", DateTime.UtcNow.AddHours(8));
        var second = ProfileReader.ParseSso(Config, dir.Path)["111122223333_ReadOnlyAccess"].Fingerprint;
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("token", second, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Same_name_in_both_uses_sso_while_signed_in_and_the_file_keys_otherwise()
    {
        using var dir = new TempDir();
        var credentials = dir.File("credentials");
        await File.WriteAllTextAsync(credentials, "[111122223333_ReadOnlyAccess]\naws_access_key_id = AKIA1\naws_secret_access_key = s\n\n[static-only]\naws_access_key_id = AKIA2\naws_secret_access_key = s\n");
        var config = dir.File("config");
        await File.WriteAllTextAsync(config, Config);
        WriteToken(dir.Path, "company", "token-1", DateTime.UtcNow.AddHours(8));

        var signedIn = ProfileReader.ReadAll(credentials, config, dir.Path);
        Assert.True(signedIn["111122223333_ReadOnlyAccess"].IsSso);
        Assert.True(signedIn.ContainsKey("static-only"));
        // Never signed in and not in the credentials file: listed, needing sign-in.
        Assert.True(signedIn["legacy-admin"].IsSso);

        // After the sign-in expires the credentials-file keys are used again.
        var later = ProfileReader.ReadAll(credentials, config, dir.Path, DateTime.UtcNow.AddHours(9));
        Assert.False(later["111122223333_ReadOnlyAccess"].IsSso);
        Assert.Equal("AKIA1", later["111122223333_ReadOnlyAccess"].AccessKeyId);
    }

    [Fact]
    public async Task Expired_sign_in_needs_sign_in_and_resumes_when_the_token_is_renewed()
    {
        using var dir = new TempDir();
        var credentials = dir.File("credentials");
        await File.WriteAllTextAsync(credentials, "");
        var config = dir.File("config");
        await File.WriteAllTextAsync(config, Config);
        WriteToken(dir.Path, "company", "old", DateTime.UtcNow.AddHours(-1));
        var monitor = new CredentialMonitor(credentials, new MemoryHaltStore(), new FakeValidator(), _ => "us-east-1", config, dir.Path);
        await monitor.StartAsync(watch: false);
        const string profile = "111122223333_ReadOnlyAccess";

        Assert.Equal(CredentialState.SignInRequired, monitor.GetStatus(profile).State);
        var ex = Assert.Throws<CredentialsUnavailableException>(() => monitor.Acquire(profile));
        Assert.Contains("aws sso login --profile 111122223333_ReadOnlyAccess", ex.Reason);

        // `aws sso login` writes a fresh token.
        WriteToken(dir.Path, "company", "new", DateTime.UtcNow.AddHours(8));
        await monitor.ReloadAsync();
        Assert.NotEqual(CredentialState.SignInRequired, monitor.GetStatus(profile).State);
        Assert.Equal("new", monitor.Acquire(profile).Sso!.AccessToken);
    }

    [Fact]
    public void Role_credential_exchange_is_a_logged_read_that_never_records_the_token()
    {
        var request = new Amazon.SSO.Model.GetRoleCredentialsRequest { AccessToken = "SECRET-SSO-TOKEN", AccountId = "111122223333", RoleName = "ReadOnlyAccess" };
        Assert.True(ReadOnlyGuard.IsAllowed(request));
        var logged = RequestParameterRedactor.Describe(request);
        Assert.Contains("RoleName=ReadOnlyAccess", logged);
        Assert.DoesNotContain("SECRET-SSO-TOKEN", logged);
        Assert.True(AwsErrorClassifier.IsAuthFailure("UnauthorizedException"));
        // Signing out or listing all accounts/roles is not needed and stays blocked.
        Assert.False(ReadOnlyGuard.IsAllowed(new Amazon.SSO.Model.LogoutRequest { AccessToken = "x" }));
    }
}
