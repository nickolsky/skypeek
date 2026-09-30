using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

public enum HealthLevel
{
    Unknown = 0,
    Ok = 1,
    Warn = 2,
    Critical = 3,
}

public sealed record MetricPoint(DateTime Timestamp, double Value);

public sealed class MetricEvaluation
{
    public string MetricName { get; init; } = "";
    public double? Current { get; init; }
    public double? Average { get; init; }
    public double? Peak { get; init; }
    public int PeriodSeconds { get; init; }
    public HealthLevel Level { get; init; } = HealthLevel.Unknown;
    public IReadOnlyList<MetricPoint> Points { get; init; } = [];

    [JsonIgnore]
    public string Display => Current is null ? "n/a" : $"{Current:0}% (last hour: avg {Average:0}, peak {Peak:0})";
}

public sealed class AlarmInfo
{
    public string Name { get; init; } = "";
    public string? Arn { get; init; }
    public string State { get; init; } = "";
    public string? StateReason { get; init; }
    public string? MetricName { get; init; }
    public string? Namespace { get; init; }
    public Dictionary<string, string> Dimensions { get; init; } = new();
    public DateTime? StateUpdated { get; init; }

    /// <summary>Time of the most recent transition into ALARM inside the history window (null if none).</summary>
    public DateTime? RecentAlarmAt { get; set; }
    public string? RecentAlarmSummary { get; set; }

    public bool Suppressed { get; set; }
    public string? SuppressedReason { get; set; }

    [JsonIgnore] public bool IsActive => State == "ALARM";
    [JsonIgnore] public bool CountsAsProblem => IsActive && !Suppressed;
    [JsonIgnore] public bool IsRecent => !IsActive && RecentAlarmAt is not null;
    [JsonIgnore]
    public string Badge => (IsActive ? "ALARM" : IsRecent ? $"recent {RecentAlarmAt!.Value.ToLocalTime():g}" : State) + (Suppressed ? " · suppressed" : "");
}

public sealed class EbEvent
{
    public DateTime Date { get; init; }
    public string Severity { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class EbEnvironmentSnapshot
{
    public string EnvironmentId { get; init; } = "";
    public string EnvironmentName { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    public string Status { get; init; } = "";
    public string Health { get; init; } = "";
    public string? HealthStatus { get; init; }
    public string? VersionLabel { get; init; }
    public DateTime? DateUpdated { get; init; }
    public string? Cname { get; init; }
    public List<string> InstanceIds { get; init; } = [];
    public List<string> AutoScalingGroups { get; init; } = [];
    public List<EbEvent> NewEvents { get; init; } = [];

    /// <summary>Enhanced-health causes for the environment (only fetched when it is not Green).</summary>
    public List<string> Causes { get; init; } = [];
    public List<EbInstanceHealth> InstanceHealth { get; init; } = [];
    /// <summary>E.g. "1,200 req/10s · 5xx 12% · 4xx 1% · p90 850 ms".</summary>
    public string? RequestSummary { get; init; }

    /// <summary>When the deployed application version was created, and the newest version of the application.</summary>
    public DateTime? VersionCreated { get; init; }
    public string? LatestVersionLabel { get; init; }
    public DateTime? LatestVersionCreated { get; init; }

    [JsonIgnore] public bool? IsLatestVersion => LatestVersionLabel is null ? null : LatestVersionLabel == VersionLabel;
    [JsonIgnore] public bool CanDeployLatest => IsLatestVersion == false;
    [JsonIgnore]
    public string VersionStatus => IsLatestVersion switch
    {
        true => "latest version",
        false => $"not the latest — newest is {LatestVersionLabel} ({LatestVersionCreated?.ToLocalTime():g})",
        null => "latest version unknown",
    };

    [JsonIgnore] public bool HasRootCause => Causes.Count > 0 || InstanceHealth.Any(i => i.Causes.Count > 0) || RequestSummary is not null;
    [JsonIgnore] public IEnumerable<EbInstanceHealth> UnhealthyInstances => InstanceHealth.Where(i => i.Causes.Count > 0 || i.Color is "Red" or "Yellow");
}

public sealed class EcsDeploymentInfo
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public string? RolloutState { get; init; }
    public string? RolloutStateReason { get; init; }
    public int Desired { get; init; }
    public int Running { get; init; }
    public int Pending { get; init; }
    public int Failed { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? TaskDefinition { get; init; }
}

public sealed class EcsServiceSnapshot
{
    /// <summary>awsvpc services (Fargate and awsvpc EC2 tasks): their tasks' security groups and subnets.</summary>
    public List<SecurityGroupRef> SecurityGroups { get; init; } = [];
    public List<string> SubnetIds { get; init; } = [];
    public string ClusterName { get; init; } = "";
    public string ClusterArn { get; init; } = "";
    public string ServiceName { get; init; } = "";
    public string? ServiceArn { get; init; }
    public string Status { get; init; } = "";
    public int Desired { get; init; }
    public int Running { get; init; }
    public int Pending { get; init; }
    public string? LaunchType { get; init; }
    public List<EcsDeploymentInfo> Deployments { get; init; } = [];
    public List<string> Events { get; init; } = [];
    /// <summary>Running tasks of the service, with their containers.</summary>
    public List<EcsTaskInfo> Tasks { get; init; } = [];
}

public sealed class EcsContainerInfo
{
    public string Name { get; init; } = "";
    public string? Image { get; init; }
    public string? ImageDigest { get; init; }
    public string LastStatus { get; init; } = "";
    public string? HealthStatus { get; init; }
    public int? ExitCode { get; init; }
    public string? Reason { get; init; }
    public string? RuntimeId { get; init; }
    public string? Cpu { get; init; }
    public string? Memory { get; init; }

    [JsonIgnore]
    public HealthLevel Level => LastStatus switch
    {
        "RUNNING" when HealthStatus == "UNHEALTHY" => HealthLevel.Critical,
        "RUNNING" => HealthLevel.Ok,
        "STOPPED" when ExitCode is not (null or 0) => HealthLevel.Critical,
        _ => HealthLevel.Warn,
    };
    [JsonIgnore]
    public string Summary => string.Join(" · ", new[]
    {
        LastStatus.ToLowerInvariant(),
        HealthStatus is { } h and not "UNKNOWN" ? h.ToLowerInvariant() : null,
        ExitCode is { } code ? $"exit {code}" : null,
        Reason,
    }.Where(s => !string.IsNullOrEmpty(s)));
    /// <summary>Image without the registry host, e.g. "shop/api:1.4.2".</summary>
    [JsonIgnore] public string ImageShort => Image is null ? "" : Image.Contains('/') && Image.Split('/')[0].Contains('.') ? Image[(Image.IndexOf('/') + 1)..] : Image;
}

public sealed class EcsTaskInfo
{
    public string TaskArn { get; init; } = "";
    public string LastStatus { get; init; } = "";
    public string DesiredStatus { get; init; } = "";
    public string? HealthStatus { get; init; }
    public string? TaskDefinition { get; init; }
    public string? LaunchType { get; init; }
    public string? CapacityProvider { get; init; }
    public string? Cpu { get; init; }
    public string? Memory { get; init; }
    public string? AvailabilityZone { get; init; }
    public string? PrivateIp { get; init; }
    public string? ContainerInstanceArn { get; init; }
    public string? PlatformVersion { get; init; }
    public string? StartedBy { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public List<EcsContainerInfo> Containers { get; init; } = [];

    [JsonIgnore] public string TaskId => TaskArn.Contains('/') ? TaskArn[(TaskArn.LastIndexOf('/') + 1)..] : TaskArn;
    [JsonIgnore]
    public HealthLevel Level => LastStatus != "RUNNING" ? HealthLevel.Warn
        : HealthStatus == "UNHEALTHY" ? HealthLevel.Critical
        : Containers.Count == 0 ? HealthLevel.Ok : Containers.Max(c => c.Level);
    [JsonIgnore]
    public string Uptime => StartedAt is not { } started ? "" : (DateTime.UtcNow - started.ToUniversalTime()) switch
    {
        var t when t.TotalDays >= 1 => $"up {t.TotalDays:0}d {t.Hours}h",
        var t when t.TotalHours >= 1 => $"up {t.TotalHours:0}h {t.Minutes}m",
        var t => $"up {Math.Max(0, t.TotalMinutes):0}m",
    };
    [JsonIgnore]
    public string Summary => string.Join(" · ", new[]
    {
        LastStatus.ToLowerInvariant(),
        HealthStatus is { } h and not "UNKNOWN" ? h.ToLowerInvariant() : null,
        TaskDefinition,
        PrivateIp,
        AvailabilityZone,
        Uptime,
    }.Where(s => !string.IsNullOrEmpty(s)));
    [JsonIgnore] public string SizeText => Cpu is null && Memory is null ? "" : $"{Cpu} CPU units · {Memory} MiB";
}

public abstract class ResourceStatus
{
    public long TargetId { get; init; }
    public string TargetName { get; init; } = "";
    public string Region { get; init; } = "";

    /// <summary>When this resource's own state was last read from AWS (target poll or single refresh).</summary>
    public DateTime? RefreshedUtc { get; set; }

    [JsonIgnore]
    public string RefreshedText => RefreshedUtc is { } t ? $"as of {t.ToLocalTime():T}" : "not refreshed yet";

    /// <summary>Level from the service's own state (EB health / ECS deployments), before metrics and alarms.</summary>
    public HealthLevel BaseLevel { get; set; } = HealthLevel.Unknown;
    public List<string> BaseReasons { get; set; } = [];

    /// <summary>Combined level: base + metric thresholds + active alarms.</summary>
    public HealthLevel Level { get; set; } = HealthLevel.Unknown;
    public List<string> Reasons { get; set; } = [];
    public MetricEvaluation? Cpu { get; set; }
    public MetricEvaluation? Memory { get; set; }
    public List<AlarmInfo> Alarms { get; set; } = [];

    [JsonIgnore] public abstract string ResourceKey { get; }
    [JsonIgnore] public abstract string DisplayName { get; }
    [JsonIgnore] public abstract string ConsoleUrl { get; }
    /// <summary>What the <see cref="Memory"/> slot measures (RDS uses it for connections as % of max_connections).</summary>
    [JsonIgnore] public virtual string MemoryLabel => "Memory";

    /// <summary>The user hid this resource from the dashboard (set from the settings on every recompute).</summary>
    [JsonIgnore] public bool IsHidden { get; set; }
    /// <summary>The alert cap in effect (resource override, else the target's; set on every recompute).</summary>
    [JsonIgnore] public AlertCap Cap { get; set; }
    /// <summary>The level before the <see cref="Cap"/> was applied.</summary>
    [JsonIgnore] public HealthLevel UncappedLevel { get; set; } = HealthLevel.Unknown;
    /// <summary>Capped to info: shown, but never a problem.</summary>
    [JsonIgnore] public bool IsMuted => Cap == AlertCap.Info;
    [JsonIgnore]
    public string? CapBadge => Cap switch
    {
        AlertCap.Info => "info only",
        AlertCap.Warning => "max warning",
        _ => null,
    };
    /// <summary>Counts as a problem in the tray, the tree and toasts.</summary>
    [JsonIgnore] public bool IsProblem => !IsHidden && !IsMuted && Level >= HealthLevel.Warn;

    [JsonIgnore] public int ActiveAlarmCount => Alarms.Count(a => a.CountsAsProblem);
    [JsonIgnore] public int SuppressedAlarmCount => Alarms.Count(a => a.IsActive && a.Suppressed);
    [JsonIgnore] public int RecentAlarmCount => Alarms.Count(a => a.IsRecent);
    [JsonIgnore] public string ReasonText => string.Join("; ", Reasons);
    [JsonIgnore] public string CpuDisplay => Cpu?.Display ?? "n/a";
    [JsonIgnore] public string MemoryDisplay => Memory?.Display ?? "n/a";
}

public sealed record EbNode(string InstanceId, EbInstanceHealth? Health, InstanceMetric? Metric, string? EnvironmentVersion = null)
{
    public string Title => $"{InstanceId} · {Health?.State ?? "?"} · {Health?.InstanceType ?? "?"} · {Health?.AvailabilityZone ?? "?"}";
    public string HealthText => Health?.HealthStatus is { } s ? $"{s} ({Health.Color})" : "health n/a";
    public string Detail => string.Join(" · ", new[]
    {
        Health?.PrivateIp,
        Health?.LaunchTime is { } l ? $"launched {l.ToLocalTime():g}" : null,
        Health?.VersionLabel is { } v ? $"version {v} ({Health.DeploymentStatus}){(EnvironmentVersion is not null && v != EnvironmentVersion ? " — differs from the environment" : "")}" : null,
    }.Where(s => s is not null));
    public string? Causes => Health is { Causes.Count: > 0 } h ? h.CauseText : null;
    public HealthLevel Level => Health?.Color switch
    {
        "Red" => HealthLevel.Critical,
        "Yellow" => HealthLevel.Warn,
        "Green" => HealthLevel.Ok,
        _ => HealthLevel.Unknown,
    };
    public bool IsRunning => Health?.State is null or "running";
}

/// <summary>One enhanced-health cause as shown to the user, with whether a suppression rule hides it.</summary>
/// <param name="Instance">Identifies this occurrence (build id, stack update) for "only this failure" suppressions.</param>
/// <param name="InstanceLabel">How to name the occurrence, e.g. "build #42".</param>
public sealed record CauseItem(string Text, bool Suppressed, string? Rule, string? Instance = null, string? InstanceLabel = null)
{
    public double Opacity => Suppressed ? 0.55 : 1.0;
}

public sealed class EbEnvironmentStatus : ResourceStatus
{
    public EbEnvironmentSnapshot Snapshot { get; init; } = new();

    /// <summary>Snapshot causes with their suppression state (recomputed with the current settings).</summary>
    public List<CauseItem> CauseItems { get; set; } = [];
    public List<EbEvent> RecentEvents { get; set; } = [];
    public List<InstanceMetric> Instances { get; set; } = [];

    /// <summary>One row per EC2 instance: EC2 state, EB per-instance health and CPU/memory, for the nodes view.</summary>
    [JsonIgnore]
    public IReadOnlyList<EbNode> Nodes => Snapshot.InstanceIds
        .Union(Snapshot.InstanceHealth.Select(h => h.InstanceId))
        .Select(id => new EbNode(id, Snapshot.InstanceHealth.FirstOrDefault(h => h.InstanceId == id), Instances.FirstOrDefault(i => i.InstanceId == id), Snapshot.VersionLabel))
        .ToList();

    public override string ResourceKey => ResourceKeys.Eb(TargetId, Snapshot.EnvironmentName);
    public override string DisplayName => Snapshot.EnvironmentName;
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/elasticbeanstalk/home?region={Region}#/environment/dashboard?environmentId={Snapshot.EnvironmentId}";
}

public sealed class InstanceMetric
{
    public string InstanceId { get; init; } = "";
    public MetricEvaluation? Cpu { get; set; }
    public MetricEvaluation? Memory { get; set; }
}

public sealed class EcsServiceStatus : ResourceStatus
{
    public EcsServiceSnapshot Snapshot { get; init; } = new();
    public List<string> StoppedReasons { get; set; } = [];

    public override string ResourceKey => ResourceKeys.Ecs(TargetId, Snapshot.ClusterName, Snapshot.ServiceName);
    public override string DisplayName => $"{Snapshot.ClusterName}/{Snapshot.ServiceName}";
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/ecs/v2/clusters/{Uri.EscapeDataString(Snapshot.ClusterName)}/services/{Uri.EscapeDataString(Snapshot.ServiceName)}/health?region={Region}";
}

public sealed class TargetHealth
{
    public long TargetId { get; init; }
    public List<EbEnvironmentStatus> Eb { get; set; } = [];
    public List<EcsServiceStatus> Ecs { get; set; } = [];
    public List<RdsInstanceStatus> Rds { get; set; } = [];
    public List<RdsClusterStatus> RdsClusters { get; set; } = [];
    public List<CacheStatus> Caches { get; set; } = [];
    public List<Ec2InstanceStatus> Ec2 { get; set; } = [];
    public List<LoadBalancerStatus> LoadBalancers { get; set; } = [];
    public List<VpnConnectionStatus> Vpns { get; set; } = [];
    public List<CodeBuildStatus> Builds { get; set; } = [];
    public List<StackStatus> Stacks { get; set; } = [];
    public List<RedshiftStatus> Redshift { get; set; } = [];
    public List<AlarmInfo> OtherAlarms { get; set; } = [];
    public DateTime? HealthUpdated { get; set; }
    public DateTime? MetricsUpdated { get; set; }
    public string? HealthError { get; set; }
    public string? MetricsError { get; set; }

    /// <summary>Every monitored resource of the target.</summary>
    [JsonIgnore]
    public IEnumerable<ResourceStatus> AllResources =>
        Eb.Cast<ResourceStatus>().Concat(Ecs).Concat(Rds).Concat(RdsClusters).Concat(Caches).Concat(Ec2).Concat(LoadBalancers)
            .Concat(Vpns).Concat(Builds).Concat(Stacks).Concat(Redshift);

    /// <summary>
    /// Groups that could not be read for lack of permission (e.g. "CodeBuild: AccessDenied …"): shown on the dashboard
    /// without failing the whole health poll.
    /// </summary>
    public List<string> AccessNotes { get; set; } = [];

    /// <summary>
    /// Resource kinds polled at least once. The first poll of a kind (e.g. right after an upgrade adds it) records
    /// what is already broken without a notification for each.
    /// </summary>
    public HashSet<string> SeenKinds { get; set; } = [];
}

public sealed record MetricQuery(string Id, string Namespace, string MetricName, IReadOnlyDictionary<string, string> Dimensions, int PeriodSeconds = 60, string Stat = "Average");

public sealed record MetricDescriptor(string Namespace, string MetricName, IReadOnlyDictionary<string, string> Dimensions);

public sealed class AlarmsResult
{
    public List<AlarmInfo> Alarms { get; init; } = [];
}

/// <param name="Capped">Lowered by an alert cap: never turns the tray icon red.</param>
public sealed record Problem(long TargetId, string TargetName, string Resource, string Message, HealthLevel Level, string? ConsoleUrl = null, bool Capped = false)
{
    public string Display => $"{TargetName}: {Resource} — {Message}";
}
