using Amazon.Runtime;
using Amazon.SecurityToken.Model;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;

namespace Skypeek.Aws;

/// <summary>Checks a specific set of credentials with sts:GetCallerIdentity (used to resume halted profiles).</summary>
public sealed class StsValidator : ICredentialValidator
{
    public async Task<ValidationResult> ValidateAsync(ProfileCredentials credentials, string region, CancellationToken ct)
    {
        using var scope = RequestScope.Begin(new RequestScopeInfo(credentials.Name, credentials.AccountId, region));
        using var clients = new AwsClientSet(credentials, region);
        try
        {
            var identity = await clients.Sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), ct);
            return new ValidationResult(true, identity.Account, null, false);
        }
        catch (AmazonServiceException ex)
        {
            return new ValidationResult(false, null, ex.ErrorCode, AwsErrorClassifier.IsAuthFailure(ex.ErrorCode));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ValidationResult(false, null, ex.GetType().Name, false);
        }
    }
}
