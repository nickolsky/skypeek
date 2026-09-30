using System.Diagnostics;
using Skypeek.Core.Credentials;

namespace Skypeek.Desktop.Platform;

/// <summary>
/// Lets the AWS CLI renew a renewable SSO sign-in (sso-session with a refresh token) before its token runs out.
/// Skypeek never touches the token cache itself: the CLI refreshes and saves the token as it would for any command.
/// </summary>
/// <remarks>
/// A plain <c>aws sts get-caller-identity --profile X</c> is not enough: the CLI answers from its own cached role
/// credentials without looking at the token. So the command runs with a temporary config holding a copy of the
/// [sso-session] section and one profile for a role that does not exist: the CLI has to load (and so renew) the token,
/// then the role lookup fails harmlessly. The copy holds no secrets (start URL, region, scopes) and is deleted at once.
/// </remarks>
public static class SsoRenewer
{
    private const string RenewalProfile = "skypeek-sso-renewal";
    private const string NoSuchRole = "skypeek-token-renewal-no-such-role";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static Func<string, SsoProfile, Task> For(string configPath) => (_, sso) => RenewAsync(configPath, sso);

    public static async Task RenewAsync(string configPath, SsoProfile sso)
    {
        if (sso.SessionName is not { } session || ProfileReader.SsoSessionSection(configPath, session) is not { } section)
            return;
        var dir = Path.Combine(Path.GetTempPath(), $"skypeek-sso-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = Path.Combine(dir, "config");
            await File.WriteAllTextAsync(config, $"""
                [profile {RenewalProfile}]
                sso_session = {session}
                sso_account_id = {sso.AccountId}
                sso_role_name = {NoSuchRole}
                region = {sso.Region}

                {section}

                """);

            var start = new ProcessStartInfo("aws")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "sts", "get-caller-identity", "--profile", RenewalProfile, "--output", "json", "--no-cli-pager" })
                start.ArgumentList.Add(a);
            start.Environment["AWS_CONFIG_FILE"] = config;
            start.Environment["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(dir, "credentials"); // none
            foreach (var name in new[] { "AWS_PROFILE", "AWS_DEFAULT_PROFILE", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" })
                start.Environment.Remove(name);

            using var process = Process.Start(start);
            if (process is null)
                return;
            // The output is the expected "no access to the role" error; nothing in it is kept.
            var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
            using var cts = new CancellationTokenSource(Timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                await drain;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No AWS CLI: nothing renews the sign-in, the user is asked to sign in when it ends.
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp folder */ }
        }
    }
}
