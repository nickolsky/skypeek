using Amazon;
using Amazon.Runtime;
using Amazon.SSO;
using Amazon.SSO.Model;
using Skypeek.Core.Credentials;

namespace Skypeek.Aws;

/// <summary>
/// Role credentials for an SSO profile: exchanges the cached <c>aws sso login</c> token with sso:GetRoleCredentials
/// and refreshes them before they expire. The token is only read, never refreshed or written: when it expires the
/// call fails with UnauthorizedException and the profile halts until a new sign-in updates the cache file.
/// The SSO call goes through the same pipeline as every other call (allowlist and request log).
/// </summary>
public sealed class SsoRoleCredentials : RefreshingAWSCredentials
{
    private readonly SsoProfile _sso;
    private readonly AmazonSSOClient _client;

    public SsoRoleCredentials(SsoProfile sso)
    {
        _sso = sso;
        // GetRoleCredentials is authorized by the bearer token, not by a signature.
        _client = new AmazonSSOClient(new AnonymousAWSCredentials(), new AmazonSSOConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(sso.Region),
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 2,
            Timeout = TimeSpan.FromSeconds(30),
        });
        PreemptExpiryTime = TimeSpan.FromMinutes(5);
    }

    protected override CredentialsRefreshState GenerateNewCredentials() =>
        GenerateNewCredentialsAsync().GetAwaiter().GetResult();

    protected override async Task<CredentialsRefreshState> GenerateNewCredentialsAsync()
    {
        if (_sso.AccessToken is not { } token)
            throw new InvalidOperationException("Not signed in to AWS SSO; run aws sso login.");
        var response = await _client.GetRoleCredentialsAsync(new GetRoleCredentialsRequest
        {
            AccessToken = token,
            AccountId = _sso.AccountId,
            RoleName = _sso.RoleName,
        }).ConfigureAwait(false);
        var role = response.RoleCredentials;
        var expires = role.Expiration is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow.AddMinutes(55);
        return new CredentialsRefreshState(new ImmutableCredentials(role.AccessKeyId, role.SecretAccessKey, role.SessionToken), expires);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _client.Dispose();
        base.Dispose(disposing);
    }
}
