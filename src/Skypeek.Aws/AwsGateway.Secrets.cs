using Amazon.Runtime;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;
using Secrets = Amazon.SecretsManager.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;

namespace Skypeek.Aws;

/// <summary>
/// Changing, creating and deleting secrets and parameters: elevated key, one approval per call (a delete needs the
/// name typed), and the value never appears in the request log, the permission dialog or an error message.
/// </summary>
public sealed partial class AwsGateway
{
    public const int StandardParameterLimit = 4096;
    public const int AdvancedParameterLimit = 8192;

    public Task<CatalogItem?> DescribeParameterAsync(Target target, string name, CancellationToken ct) =>
        Call<CatalogItem?>(target, "DescribeParameters", async c => await ReadParameterAsync(c, target, name, ct));

    private static async Task<CatalogItem?> ReadParameterAsync(AwsClientSet c, Target target, string name, CancellationToken ct)
    {
        var resp = await c.Ssm.DescribeParametersAsync(new Ssm.DescribeParametersRequest
        {
            ParameterFilters = [new Ssm.ParameterStringFilter { Key = "Name", Option = "Equals", Values = [name] }],
        }, ct);
        return resp.Parameters?.FirstOrDefault() is not { } p ? null : new CatalogItem
        {
            TargetId = target.Id,
            Kind = CatalogKind.Parameter,
            Name = p.Name,
            Arn = p.ARN,
            Description = p.Description,
            Type = p.Type?.Value,
            Tier = p.Tier?.Value,
            DataType = p.DataType,
            Version = p.Version,
            KmsKeyId = p.KeyId,
            LastModified = p.LastModifiedDate,
            LastModifiedBy = p.LastModifiedUser,
        };
    }

    /// <summary>Why a parameter value cannot be saved (size, list format), or null.</summary>
    public static string? ValidateParameterValue(string value, string type, string? tier)
    {
        if (value.Length == 0)
            return "A parameter cannot be empty.";
        var limit = tier is "Advanced" or "Intelligent-Tiering" ? AdvancedParameterLimit : StandardParameterLimit;
        if (System.Text.Encoding.UTF8.GetByteCount(value) > limit)
            return $"The value is larger than {limit / 1024} KB ({(limit == StandardParameterLimit ? "use the Advanced tier for up to 8 KB" : "the Advanced tier limit")}).";
        if (type == "StringList" && value.Split(',').Any(v => v.Length == 0))
            return "A StringList is comma-separated values without empty items.";
        return null;
    }

    private static string Size(string value) => $"{System.Text.Encoding.UTF8.GetByteCount(value):N0} bytes";

    // ---------------- Secrets Manager ----------------

    public Task UpdateSecretValueAsync(Target target, CatalogItem secret, string value, CancellationToken ct) =>
        Sensitive(value, () => Call(target, "secretsmanager:PutSecretValue", async c =>
        {
            var current = await c.Secrets.DescribeSecretAsync(new Secrets.DescribeSecretRequest { SecretId = secret.Arn ?? secret.Name }, ct);
            if (secret.LastModified is { } seen && current.LastChangedDate is { } changed && Math.Abs((changed - seen).TotalSeconds) > 1)
                throw new InvalidOperationException($"{secret.Name} was changed at {changed.ToLocalTime():g} since you opened it; reload it first.");
            if (current.DeletedDate is not null)
                throw new InvalidOperationException($"{secret.Name} is scheduled for deletion; cancel the deletion first.");
            // A new version labelled AWSCURRENT; the previous one stays as AWSPREVIOUS.
            await c.Secrets.PutSecretValueAsync(new Secrets.PutSecretValueRequest { SecretId = secret.Arn ?? secret.Name, SecretString = value }, ct);
            return true;
        }, elevated: true, secret.Name,
        confirmation: $"Save a new value for the secret {secret.Name} ({Size(value)}; the value is not shown here).\n\n"
                      + "It becomes the current version (AWSCURRENT); the previous value stays available as AWSPREVIOUS. "
                      + "Applications that read the secret get the new value on their next read."));

    public Task CreateSecretAsync(Target target, string name, string value, string? description, string? kmsKeyId, CancellationToken ct) =>
        Sensitive(value, () => Call(target, "secretsmanager:CreateSecret", async c =>
        {
            await c.Secrets.CreateSecretAsync(new Secrets.CreateSecretRequest
            {
                Name = name,
                SecretString = value,
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
                KmsKeyId = string.IsNullOrWhiteSpace(kmsKeyId) ? null : kmsKeyId,
            }, ct);
            return true;
        }, elevated: true, name,
        confirmation: $"Create the secret {name} ({Size(value)}; the value is not shown here), encrypted with "
                      + $"{(string.IsNullOrWhiteSpace(kmsKeyId) ? "the default key aws/secretsmanager" : kmsKeyId)}.\n\nSecrets Manager charges $0.40 per secret per month."));

    public Task DeleteSecretAsync(Target target, CatalogItem secret, int recoveryDays, CancellationToken ct)
    {
        if (recoveryDays is < 7 or > 30)
            throw new ArgumentException("The recovery window is 7 to 30 days.");
        return Call(target, "secretsmanager:DeleteSecret", async c =>
        {
            // Never ForceDeleteWithoutRecovery: the guard refuses it.
            await c.Secrets.DeleteSecretAsync(new Secrets.DeleteSecretRequest { SecretId = secret.Arn ?? secret.Name, RecoveryWindowInDays = recoveryDays }, ct);
            return true;
        }, elevated: true, secret.Name,
        confirmation: $"Schedule the secret {secret.Name} for deletion in {recoveryDays} days.\n\n"
                      + "Until then it can be restored (Cancel deletion); reading it fails right away, so applications that use it stop working.",
        confirmPhrase: secret.Name);
    }

    public Task RestoreSecretAsync(Target target, CatalogItem secret, CancellationToken ct) =>
        Call(target, "secretsmanager:RestoreSecret", async c =>
        {
            await c.Secrets.RestoreSecretAsync(new Secrets.RestoreSecretRequest { SecretId = secret.Arn ?? secret.Name }, ct);
            return true;
        }, elevated: true, secret.Name,
        confirmation: $"Cancel the scheduled deletion of {secret.Name}; it can be read again right away.");

    // ---------------- Parameter Store ----------------

    public Task PutParameterValueAsync(Target target, CatalogItem parameter, string value, CancellationToken ct)
    {
        var type = parameter.Type ?? "String";
        if (ValidateParameterValue(value, type, parameter.Tier) is { } problem)
            throw new ArgumentException(problem);
        return Sensitive(value, () => Call(target, "ssm:PutParameter", async c =>
        {
            var current = await ReadParameterAsync(c, target, parameter.Name, ct)
                          ?? throw new InvalidOperationException($"{parameter.Name} no longer exists.");
            if (parameter.Version is { } seen && current.Version != seen)
                throw new InvalidOperationException($"{parameter.Name} is at version {current.Version} (you opened version {seen}); reload it first.");
            // Type, key and tier stay as they are (a missing KeyId would re-encrypt with the default key).
            await c.Ssm.PutParameterAsync(new Ssm.PutParameterRequest
            {
                Name = parameter.Name,
                Value = value,
                Overwrite = true,
                Type = current.Type,
                KeyId = current.Type == "SecureString" ? current.KmsKeyId : null,
                Tier = current.Tier is "Advanced" or "Intelligent-Tiering" ? current.Tier : null,
                DataType = current.DataType,
            }, ct);
            return true;
        }, elevated: true, parameter.Name,
        confirmation: $"Save version {(parameter.Version ?? 0) + 1} of the parameter {parameter.Name} ({type}, {Size(value)}; the value is not shown here).\n\n"
                      + "Type, encryption key and tier stay the same. Earlier versions stay in the parameter's history."));
    }

    public Task CreateParameterAsync(Target target, string name, string value, string type, string? description, string? tier, string? kmsKeyId, CancellationToken ct)
    {
        if (type is not ("String" or "StringList" or "SecureString"))
            throw new ArgumentException("The type is String, StringList or SecureString.");
        if (!name.StartsWith('/') && name.Contains('/'))
            throw new ArgumentException("A hierarchical parameter name starts with / (e.g. /app/prod/db-host).");
        if (ValidateParameterValue(value, type, tier) is { } problem)
            throw new ArgumentException(problem);
        return Sensitive(value, () => Call(target, "ssm:PutParameter", async c =>
        {
            if (await ReadParameterAsync(c, target, name, ct) is not null)
                throw new InvalidOperationException($"{name} already exists; edit it instead.");
            await c.Ssm.PutParameterAsync(new Ssm.PutParameterRequest
            {
                Name = name,
                Value = value,
                Type = type,
                Overwrite = false,
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
                KeyId = type == "SecureString" && !string.IsNullOrWhiteSpace(kmsKeyId) ? kmsKeyId : null,
                Tier = tier is "Advanced" ? tier : null,
            }, ct);
            return true;
        }, elevated: true, name,
        confirmation: $"Create the parameter {name} ({type}{(tier is "Advanced" ? ", Advanced tier ($0.05 per month)" : "")}, {Size(value)}; the value is not shown here)."));
    }

    public Task DeleteParameterAsync(Target target, CatalogItem parameter, CancellationToken ct) =>
        Call(target, "ssm:DeleteParameter", async c =>
        {
            await c.Ssm.DeleteParameterAsync(new Ssm.DeleteParameterRequest { Name = parameter.Name }, ct);
            return true;
        }, elevated: true, parameter.Name,
        confirmation: $"Delete the parameter {parameter.Name} and all its versions.\n\nThis cannot be undone; applications that read it get an error.",
        confirmPhrase: parameter.Name);

    /// <summary>
    /// Keeps the value out of everything logged or shown: the request log scrubs it from error messages, and an error
    /// that quotes it is rethrown without it.
    /// </summary>
    private static async Task Sensitive(string value, Func<Task<bool>> call)
    {
        using var scope = RequestScope.Redact(value);
        try
        {
            await call();
        }
        catch (Exception ex) when (value.Length >= 4 && ex.Message.Contains(value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ex.Message.Replace(value, "(value)", StringComparison.Ordinal));
        }
    }
}
