namespace Skypeek.Core.Models;

public enum CatalogKind
{
    Secret,
    Parameter,
}

public sealed class CatalogItem
{
    public long TargetId { get; set; }
    public CatalogKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string? Arn { get; set; }
    public string? Description { get; set; }

    /// <summary>Parameter type (String/StringList/SecureString); null for secrets.</summary>
    public string? Type { get; set; }
    public string? Tier { get; set; }
    public string? DataType { get; set; }
    public long? Version { get; set; }
    public string? KmsKeyId { get; set; }
    public bool? RotationEnabled { get; set; }
    public DateTime? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTime? LastAccessed { get; set; }
    public DateTime? Created { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
    public DateTime FirstSeen { get; set; }
    public DateTime FetchedAt { get; set; }

    public bool IsSecureParameter => Kind == CatalogKind.Parameter && Type == "SecureString";
}

public sealed record SecretValueResult(string Value, string? VersionId, string? Detail);

public enum JobKind
{
    Catalog,
    Health,
    Metrics,
    /// <summary>Network tab inventory (VPCs, subnets, interfaces, security groups).</summary>
    Network,
    /// <summary>List prices for estimates and billed costs from Cost Explorer.</summary>
    Costs,
}

public sealed record SyncRun(long TargetId, string Feature, DateTime StartedAt, DateTime FinishedAt, bool Success, int ItemCount, string? Error);
