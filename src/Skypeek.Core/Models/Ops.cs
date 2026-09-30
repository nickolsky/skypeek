using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

// ---------------- Site-to-Site VPN ----------------

public sealed class VpnTunnelInfo
{
    public string OutsideIp { get; init; } = "";
    /// <summary>UP or DOWN.</summary>
    public string Status { get; init; } = "";
    public string? StatusMessage { get; init; }
    public DateTime? LastChange { get; init; }
    public int AcceptedRoutes { get; init; }

    [JsonIgnore] public bool IsUp => Status == "UP";
    [JsonIgnore] public HealthLevel Level => IsUp ? HealthLevel.Ok : HealthLevel.Critical;
    [JsonIgnore]
    public string Text => string.Join(" · ", new[]
    {
        $"{OutsideIp} {Status}",
        AcceptedRoutes > 0 ? $"{AcceptedRoutes} BGP route(s)" : null,
        StatusMessage is { Length: > 0 } m ? m : null,
        LastChange is { } c ? $"since {c.ToLocalTime():g}" : null,
    }.Where(s => s is not null));
}

public sealed class VpnConnectionSnapshot
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>pending, available, deleting, deleted.</summary>
    public string State { get; init; } = "";
    public string? Type { get; init; }
    public string? CustomerGatewayId { get; init; }
    public string? CustomerGatewayIp { get; init; }
    public string? CustomerGatewayName { get; init; }
    public string? VpnGatewayId { get; init; }
    public string? TransitGatewayId { get; init; }
    public bool StaticRoutesOnly { get; init; }
    public List<string> StaticRoutes { get; init; } = [];
    public List<VpnTunnelInfo> Tunnels { get; init; } = [];

    [JsonIgnore] public string Title => Name is { Length: > 0 } n ? $"{n} ({Id})" : Id;
    [JsonIgnore] public int UpCount => Tunnels.Count(t => t.IsUp);
    [JsonIgnore]
    public string GatewayText => TransitGatewayId is { } tgw ? $"transit gateway {tgw}" : VpnGatewayId is { } vgw ? $"virtual private gateway {vgw}" : "no AWS gateway";
    [JsonIgnore]
    public string CustomerText => $"{CustomerGatewayName ?? CustomerGatewayId ?? "customer gateway"}{(CustomerGatewayIp is { } ip ? $" ({ip})" : "")}";
    [JsonIgnore] public string RoutingText => StaticRoutesOnly ? $"static routes: {(StaticRoutes.Count == 0 ? "none" : string.Join(", ", StaticRoutes))}" : "dynamic (BGP)";
}

public sealed class VpnConnectionStatus : ResourceStatus
{
    public VpnConnectionSnapshot Snapshot { get; init; } = new();
    public List<CauseItem> CauseItems { get; set; } = [];

    public override string ResourceKey => ResourceKeys.Vpn(TargetId, Snapshot.Id);
    public override string DisplayName => Snapshot.Name ?? Snapshot.Id;
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/vpcconsole/home?region={Region}#VpnConnectionDetails:VpnConnectionId={Snapshot.Id}";
}

// ---------------- CodeBuild ----------------

public sealed record CodeBuildPhase(string Type, string? Status, long? Seconds, string? Message)
{
    public string Text => $"{Type}{(Status is { } s ? $" {s}" : "")}{(Seconds is { } sec ? $" ({sec}s)" : "")}{(Message is { Length: > 0 } m ? $": {m}" : "")}";
    public bool IsFailure => Status is "FAILED" or "FAULT" or "TIMED_OUT" or "CLIENT_ERROR";
}

public sealed class CodeBuildRun
{
    /// <summary>project:uuid.</summary>
    public string Id { get; init; } = "";
    public string? Arn { get; init; }
    public long? Number { get; init; }
    /// <summary>SUCCEEDED, FAILED, FAULT, TIMED_OUT, IN_PROGRESS, STOPPED.</summary>
    public string Status { get; init; } = "";
    public DateTime? Started { get; init; }
    public DateTime? Ended { get; init; }
    public string? Initiator { get; init; }
    public string? SourceVersion { get; init; }
    public string? ResolvedSourceVersion { get; init; }
    public string? CurrentPhase { get; init; }
    public string? LogGroup { get; init; }
    public string? LogStream { get; init; }
    public List<CodeBuildPhase> Phases { get; init; } = [];

    [JsonIgnore] public bool IsComplete => Status != "IN_PROGRESS";
    [JsonIgnore] public bool IsFailure => Status is "FAILED" or "FAULT" or "TIMED_OUT";
    [JsonIgnore] public string NumberText => Number is { } n ? $"#{n}" : Id.Split(':')[^1] is var tail && tail.Length > 8 ? tail[..8] : Id.Split(':')[^1];
    [JsonIgnore] public CodeBuildPhase? FailedPhase => Phases.FirstOrDefault(p => p.IsFailure);
    [JsonIgnore]
    public TimeSpan? Duration => Started is { } s ? (Ended ?? DateTime.UtcNow) - s : null;
    [JsonIgnore]
    public string DurationText => Duration is { } d ? d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{d.Seconds}s" : "";
    [JsonIgnore]
    public string Summary => string.Join(" · ", new[]
    {
        $"{NumberText} {Status}",
        FailedPhase is { } f ? $"in {f.Type}{(f.Message is { Length: > 0 } m ? $": {m}" : "")}" : Status == "IN_PROGRESS" && CurrentPhase is { } p ? $"in {p}" : null,
        Started is { } s ? $"started {s.ToLocalTime():g}" : null,
        DurationText is { Length: > 0 } dur ? dur : null,
    }.Where(x => x is not null));
    [JsonIgnore] public string ShortSource => (ResolvedSourceVersion ?? SourceVersion) is { Length: > 12 } v && !v.Contains('/') ? v[..12] : ResolvedSourceVersion ?? SourceVersion ?? "";
    [JsonIgnore] public HealthLevel Level => IsFailure ? HealthLevel.Critical : Status == "SUCCEEDED" ? HealthLevel.Ok : HealthLevel.Unknown;
}

public sealed class CodeBuildProjectSnapshot
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>The newest build (may still be running).</summary>
    public CodeBuildRun? LatestBuild { get; init; }
    /// <summary>The newest finished build (the one that decides health).</summary>
    public CodeBuildRun? LastCompleted { get; init; }
    /// <summary>Its builds have not been looked up yet (many projects are read a few per poll, to stay under CodeBuild's rate limit).</summary>
    public bool NotReadYet { get; init; }
}

public sealed class CodeBuildStatus : ResourceStatus
{
    public CodeBuildProjectSnapshot Snapshot { get; init; } = new();
    public List<CauseItem> CauseItems { get; set; } = [];

    [JsonIgnore] public bool IsBuilding => Snapshot.LatestBuild is { IsComplete: false };

    public override string ResourceKey => ResourceKeys.CodeBuild(TargetId, Snapshot.Name);
    public override string DisplayName => Snapshot.Name;
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/codesuite/codebuild/projects/{Uri.EscapeDataString(Snapshot.Name)}/history?region={Region}";

    public string BuildConsoleUrl(CodeBuildRun run) =>
        $"https://{Region}.console.aws.amazon.com/codesuite/codebuild/projects/{Uri.EscapeDataString(Snapshot.Name)}/build/{Uri.EscapeDataString(run.Id)}/?region={Region}";
}

// ---------------- CloudFormation ----------------

public sealed record StackOutput(string Key, string? Value, string? Description, string? ExportName)
{
    public string Text => $"{Key} = {Value}{(ExportName is { } e ? $" (export {e})" : "")}";
}

public sealed record StackEventInfo(DateTime Time, string LogicalId, string? ResourceType, string Status, string? Reason, string? PhysicalId)
{
    public bool IsFailure => Status.EndsWith("_FAILED", StringComparison.Ordinal);
    public string Text => $"{Time.ToLocalTime():g}  {LogicalId} {Status}{(Reason is { Length: > 0 } r ? $": {r}" : "")}";
    public HealthLevel Level => IsFailure ? HealthLevel.Critical : Status.Contains("ROLLBACK", StringComparison.Ordinal) ? HealthLevel.Warn : HealthLevel.Ok;
}

/// <summary>One create/update/delete of a stack, from its first to its last event.</summary>
public sealed record StackOperation(DateTime Started, DateTime Ended, string Kind, string FinalStatus, IReadOnlyList<StackEventInfo> Events)
{
    public IReadOnlyList<StackEventInfo> Failures => Events.Where(e => e.IsFailure).ToList();
    public string Title => $"{Started.ToLocalTime():g}  {Kind} → {FinalStatus}";
    public string Detail => string.Join(" · ", new[]
    {
        Ended > Started ? $"{(Ended - Started).TotalMinutes:0.#} min" : null,
        $"{Events.Count} event(s)",
        Failures.Count > 0 ? $"{Failures.Count} failed: {string.Join(", ", Failures.Select(f => f.LogicalId).Distinct().Take(4))}" : null,
    }.Where(s => s is not null));
    public HealthLevel Level => StackRules.StatusLevel(FinalStatus) is var l && l == HealthLevel.Unknown ? HealthLevel.Ok : l;
}

public sealed class StackSnapshot
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public string? StatusReason { get; init; }
    public string? Description { get; init; }
    public DateTime? Created { get; init; }
    public DateTime? LastUpdated { get; init; }
    public string? ParentId { get; init; }
    public string? RootId { get; init; }
    public string? DriftStatus { get; init; }
    public DateTime? DriftChecked { get; init; }
    public bool TerminationProtection { get; init; }
    public List<StackOutput> Outputs { get; init; } = [];
    public Dictionary<string, string> Tags { get; init; } = new();
    /// <summary>The first failed resource of the last failed operation, e.g. "MyBucket CREATE_FAILED: …".</summary>
    public string? FailedResource { get; set; }

    [JsonIgnore] public bool IsNested => RootId is { Length: > 0 };
    [JsonIgnore] public bool IsElasticBeanstalk => Name.StartsWith("awseb-", StringComparison.Ordinal);
    /// <summary>Identifies one failure, so "only this failure" suppressions end with the next update.</summary>
    [JsonIgnore] public string FailureInstance => $"{Status}@{(LastUpdated ?? Created)?.ToString("O")}";
    [JsonIgnore]
    public string DriftText => DriftStatus is null or "NOT_CHECKED" ? "drift not checked" : $"drift {DriftStatus.ToLowerInvariant().Replace('_', ' ')}{(DriftChecked is { } d ? $" ({d.ToLocalTime():g})" : "")}";
}

public sealed class StackStatus : ResourceStatus
{
    public StackSnapshot Snapshot { get; init; } = new();
    public List<CauseItem> CauseItems { get; set; } = [];

    public override string ResourceKey => ResourceKeys.Stack(TargetId, Snapshot.Name);
    public override string DisplayName => Snapshot.Name;
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/cloudformation/home?region={Region}#/stacks/stackinfo?stackId={Uri.EscapeDataString(Snapshot.Id)}";
}

public static class StackRules
{
    /// <summary>
    /// Failed, or unusable after a failed create (ROLLBACK_COMPLETE): critical. A failed update that rolled back leaves a
    /// working stack: warning. Anything in progress: unknown until it settles.
    /// </summary>
    public static HealthLevel StatusLevel(string status) => status switch
    {
        _ when status.EndsWith("_FAILED", StringComparison.Ordinal) => HealthLevel.Critical,
        "ROLLBACK_COMPLETE" => HealthLevel.Critical,
        "UPDATE_ROLLBACK_COMPLETE" or "IMPORT_ROLLBACK_COMPLETE" => HealthLevel.Warn,
        _ when status.EndsWith("_IN_PROGRESS", StringComparison.Ordinal) => HealthLevel.Unknown,
        _ => HealthLevel.Ok,
    };

    /// <summary>Groups events (newest first, as AWS returns them) into operations: each starts with a stack-level *_IN_PROGRESS event.</summary>
    public static IReadOnlyList<StackOperation> Operations(string stackName, IReadOnlyList<StackEventInfo> events)
    {
        var ordered = events.OrderBy(e => e.Time).ToList();
        var result = new List<StackOperation>();
        List<StackEventInfo>? current = null;
        foreach (var e in ordered)
        {
            var stackLevel = e.LogicalId == stackName && e.ResourceType == "AWS::CloudFormation::Stack";
            if (stackLevel && e.Status is "CREATE_IN_PROGRESS" or "UPDATE_IN_PROGRESS" or "DELETE_IN_PROGRESS" or "IMPORT_IN_PROGRESS" or "REVIEW_IN_PROGRESS")
            {
                if (current is { Count: > 0 })
                    result.Add(Close(current));
                current = [];
            }
            (current ??= []).Add(e);
        }
        if (current is { Count: > 0 })
            result.Add(Close(current));
        result.Reverse();
        return result;

        StackOperation Close(List<StackEventInfo> list)
        {
            var first = list[0];
            var last = list.LastOrDefault(x => x.LogicalId == stackName && x.ResourceType == "AWS::CloudFormation::Stack") ?? list[^1];
            var kind = first.Status.Split('_')[0] switch { "CREATE" => "Create", "UPDATE" => "Update", "DELETE" => "Delete", "IMPORT" => "Import", "REVIEW" => "Change set", _ => "Operation" };
            return new StackOperation(first.Time, list[^1].Time, kind, last.Status, list.OrderByDescending(x => x.Time).ToList());
        }
    }
}

// ---------------- Redshift ----------------

public sealed class RedshiftSnapshot
{
    /// <summary>Cluster identifier, or workgroup name for Redshift Serverless.</summary>
    public string Id { get; init; } = "";
    public bool IsServerless { get; init; }
    public string Status { get; init; } = "";
    /// <summary>Available, Unavailable, Maintenance, Modifying, Failed (provisioned clusters).</summary>
    public string? AvailabilityStatus { get; init; }
    public string? NodeType { get; init; }
    public int? NodeCount { get; init; }
    public int? BaseCapacityRpu { get; init; }
    public string? Namespace { get; init; }
    public string? DatabaseName { get; init; }
    public string? Endpoint { get; init; }
    public int? Port { get; init; }
    public string? VpcId { get; init; }
    public string? SubnetGroup { get; init; }
    public List<string> SubnetIds { get; init; } = [];
    public List<SecurityGroupRef> SecurityGroups { get; init; } = [];
    public bool PubliclyAccessible { get; init; }
    public bool Encrypted { get; init; }
    public string? MaintenanceWindow { get; init; }
    public DateTime? Created { get; init; }

    [JsonIgnore] public bool IsPaused => Status == "paused";
    [JsonIgnore] public string EndpointText => Endpoint is null ? "" : Port is { } p ? $"{Endpoint}:{p}" : Endpoint;
    [JsonIgnore]
    public string SizeText => IsServerless ? $"serverless{(BaseCapacityRpu is { } rpu ? $" · base {rpu} RPU" : "")}" : $"{NodeType} × {NodeCount}";
}

public sealed class RedshiftStatus : ResourceStatus
{
    public RedshiftSnapshot Snapshot { get; init; } = new();
    /// <summary>Provisioned clusters: used disk in % (information, not a threshold).</summary>
    public double? DiskUsedPercent { get; set; }

    public override string ResourceKey => ResourceKeys.Redshift(TargetId, Snapshot.Id);
    public override string DisplayName => Snapshot.Id;
    public override string ConsoleUrl => Snapshot.IsServerless
        ? $"https://{Region}.console.aws.amazon.com/redshiftv2/home?region={Region}#serverless-workgroup?workgroup={Uri.EscapeDataString(Snapshot.Id)}"
        : $"https://{Region}.console.aws.amazon.com/redshiftv2/home?region={Region}#cluster-details?cluster={Uri.EscapeDataString(Snapshot.Id)}";
}
