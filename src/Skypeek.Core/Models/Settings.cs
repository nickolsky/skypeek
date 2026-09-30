using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

public sealed record ThresholdSettings(double CpuWarn, double CpuCritical, double MemWarn, double MemCritical)
{
    /// <summary>RDS only: used storage in %; null keeps the global RDS storage thresholds.</summary>
    public double? StorageWarn { get; init; }
    public double? StorageCritical { get; init; }

    public static ThresholdSettings Default => new(80, 90, 80, 90);
}

public sealed class AppSettings
{
    public bool ShowNonReadOnlyProfiles { get; set; }
    public int LockoutMinutes { get; set; } = 15;
    public bool LockOnWindowsLock { get; set; } = true;
    public ThresholdSettings EcsThresholds { get; set; } = ThresholdSettings.Default;
    public ThresholdSettings EbThresholds { get; set; } = ThresholdSettings.Default;
    /// <summary>RDS: CPU, and connections in % of max_connections (the "memory" pair).</summary>
    public ThresholdSettings RdsThresholds { get; set; } = new(80, 90, 80, 95);
    /// <summary>RDS used storage in % of allocated (non-Aurora).</summary>
    public double RdsStorageWarn { get; set; } = 80;
    public double RdsStorageCritical { get; set; } = 90;
    /// <summary>ElastiCache: engine CPU and memory usage (DatabaseMemoryUsagePercentage).</summary>
    public ThresholdSettings CacheThresholds { get; set; } = ThresholdSettings.Default;
    /// <summary>Standalone EC2 instances: CPU and memory (CloudWatch agent).</summary>
    public ThresholdSettings Ec2Thresholds { get; set; } = ThresholdSettings.Default;

    /// <summary>Dashboard: show Elastic Beanstalk environments under their application.</summary>
    public bool EbGroupByApplication { get; set; }
    /// <summary>EC2 group also lists instances Elastic Beanstalk manages (they are shown under their environment anyway).</summary>
    public bool Ec2IncludeEbInstances { get; set; }
    /// <summary>CloudFormation group also lists the stacks Elastic Beanstalk creates for its environments (awseb-…).</summary>
    public bool StacksIncludeEb { get; set; }

    /// <summary>
    /// Resources (by <see cref="ResourceKeys"/> value) the user hid from the dashboard: not shown, not counted as
    /// problems, no toasts and no metric queries. They stay in the inventory so unhiding is instant.
    /// </summary>
    public HashSet<string> HiddenResources { get; set; } = new();
    public int SustainedMinutes { get; set; } = 10;
    public int AlarmHistoryHours { get; set; } = 24;
    public bool WarningsTurnIconRed { get; set; } = true;

    /// <summary>Desktop notifications when a resource breaks or recovers (also while the window is hidden).</summary>
    public bool NotifyProblems { get; set; } = true;
    /// <summary>Ask to sign in again (popup, or a notification while locked) when an AWS SSO sign-in ends.</summary>
    public bool NotifySignIn { get; set; } = true;
    /// <summary>Keep SSO sign-ins renewed by letting the AWS CLI refresh its token before it runs out.</summary>
    public bool RenewSsoWithCli { get; set; } = true;

    /// <summary>
    /// Per-resource alert level caps (by <see cref="ResourceKeys"/> value); they override the target's
    /// <see cref="Target.AlertCap"/>, in either direction.
    /// </summary>
    public Dictionary<string, AlertCap> ResourceAlertCaps { get; set; } = new();
    public int RequestLogRetentionDays { get; set; } = 30;

    /// <summary>Keyed by <see cref="ResourceKeys"/> values, e.g. "3:ecs:cluster/service".</summary>
    public Dictionary<string, ThresholdSettings> ResourceThresholds { get; set; } = new();

    /// <summary>
    /// Application Auto Scaling target-tracking alarms (TargetTracking-*) sit in ALARM whenever a service is scaled
    /// in or out; they drive scaling and are not health problems.
    /// </summary>
    public bool IgnoreTargetTrackingAlarms { get; set; } = true;

    public List<AlarmSuppression> SuppressedAlarms { get; set; } = new();

    /// <summary>Elastic Beanstalk enhanced-health causes the user considers false positives.</summary>
    public List<CauseSuppression> SuppressedCauses { get; set; } = new();
}

/// <summary>
/// An EB health cause matching <see cref="Pattern"/> (whitespace-insensitive, * and ? wildcards) no longer makes the
/// environment unhealthy. Scope: one environment, all environments of a target, or everything.
/// </summary>
/// <param name="EnvironmentName">The resource name (EB environment, load balancer, project, stack, VPN…).</param>
/// <param name="OnlyFor">
/// One occurrence only (a failed build id, a failed stack update): the suppression stops applying when the resource fails
/// again, so the next failure alerts.
/// </param>
public sealed record CauseSuppression(string Pattern, long? TargetId, string? EnvironmentName, DateTime CreatedUtc, string? OnlyFor = null)
{
    public string Scope(IReadOnlyList<Target> targets)
    {
        var target = TargetId is null ? null : targets.FirstOrDefault(t => t.Id == TargetId)?.DisplayName ?? $"target {TargetId}";
        var scope = (target, EnvironmentName) switch
        {
            (null, _) => "all targets",
            (_, null) => $"everything in {target}",
            _ => $"{EnvironmentName} ({target})",
        };
        return OnlyFor is null ? scope : $"{scope} · this failure only";
    }
}

/// <summary>Alarm names matching <see cref="Pattern"/> (wildcards * and ?) never count as problems.</summary>
public sealed record AlarmSuppression(string Pattern, long? TargetId, DateTime CreatedUtc)
{
    public string Scope(IReadOnlyList<Target> targets) =>
        TargetId is null ? "all targets" : targets.FirstOrDefault(t => t.Id == TargetId)?.DisplayName ?? $"target {TargetId}";
}

/// <summary>The most a resource may alert: dev and sandbox environments should not turn the tray red.</summary>
public enum AlertCap
{
    /// <summary>No cap: warnings and critical problems as they are.</summary>
    None,
    /// <summary>Critical problems count as warnings; the tray icon never turns red because of them.</summary>
    Warning,
    /// <summary>Shown on the dashboard only: never counted as a problem, no notifications, no tray colour.</summary>
    Info,
}

public sealed class Target
{
    public long Id { get; set; }
    /// <summary>Read-only profile used for every background and normal call.</summary>
    public string ProfileName { get; set; } = "";

    /// <summary>Optional profile with more permissions, used only on demand after the user approves each call.</summary>
    public string? ElevatedProfileName { get; set; }

    /// <summary>
    /// Exception the user confirmed in Settings: one profile serves as both keys (e.g. an account with only an admin
    /// role). Elevated calls still need approval each time; the read-only allowlist still applies to every call.
    /// </summary>
    [JsonIgnore] public bool UsesSameKey => ElevatedProfileName is { Length: > 0 } e && e == ProfileName;

    public string Region { get; set; } = "us-east-1";
    public string Alias { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Maximum alert level of everything in this target (resources can override it).</summary>
    public AlertCap AlertCap { get; set; }

    public bool SecretsEnabled { get; set; } = true;
    public bool ParamsEnabled { get; set; } = true;
    public bool EbEnabled { get; set; } = true;
    public bool EcsEnabled { get; set; } = true;
    public bool RdsEnabled { get; set; } = true;
    public bool CacheEnabled { get; set; } = true;
    public bool Ec2Enabled { get; set; } = true;
    public bool ElbEnabled { get; set; } = true;
    public bool VpnEnabled { get; set; } = true;
    public bool CodeBuildEnabled { get; set; } = true;
    public bool StacksEnabled { get; set; } = true;
    public bool RedshiftEnabled { get; set; } = true;
    /// <summary>Network tab: VPCs, subnets, interfaces and security groups (downloaded and cached).</summary>
    public bool NetworkEnabled { get; set; } = true;
    /// <summary>Monthly cost estimates from the AWS Price List (free API; prices cached for a week).</summary>
    public bool CostEnabled { get; set; }
    /// <summary>Billed cost from Cost Explorer (each request costs $0.01; refreshed at most twice a day).</summary>
    public bool CostExplorerEnabled { get; set; }

    /// <summary>Any health/metrics feature on.</summary>
    [JsonIgnore]
    public bool HealthEnabled => EbEnabled || EcsEnabled || RdsEnabled || CacheEnabled || Ec2Enabled || ElbEnabled
                                 || VpnEnabled || CodeBuildEnabled || StacksEnabled || RedshiftEnabled;

    /// <summary>0 = off.</summary>
    public int CatalogIntervalMinutes { get; set; } = 360;
    /// <summary>Network inventory download; 0 = only on demand.</summary>
    public int NetworkIntervalMinutes { get; set; } = 60;
    public int HealthIntervalMinutes { get; set; } = 5;
    public int MetricsIntervalMinutes { get; set; } = 5;

    public ThresholdSettings? EcsThresholds { get; set; }
    public ThresholdSettings? EbThresholds { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? $"{ProfileName} / {Region}" : Alias;

    public Target Clone() => (Target)MemberwiseClone();
}

public static class ResourceKeys
{
    public static string Ecs(long targetId, string cluster, string service) => $"{targetId}:ecs:{cluster}/{service}";
    public static string Eb(long targetId, string environmentName) => $"{targetId}:eb:{environmentName}";
    public static string Rds(long targetId, string instance) => $"{targetId}:rds:{instance}";
    public static string RdsCluster(long targetId, string cluster) => $"{targetId}:rdscluster:{cluster}";
    public static string Cache(long targetId, string id) => $"{targetId}:cache:{id}";
    public static string Ec2(long targetId, string instanceId) => $"{targetId}:ec2:{instanceId}";
    public static string LoadBalancer(long targetId, string name) => $"{targetId}:elb:{name}";
    public static string Vpn(long targetId, string vpnId) => $"{targetId}:vpn:{vpnId}";
    public static string CodeBuild(long targetId, string project) => $"{targetId}:codebuild:{project}";
    public static string Stack(long targetId, string stackName) => $"{targetId}:cfn:{stackName}";
    public static string Redshift(long targetId, string id) => $"{targetId}:redshift:{id}";
}

public static class AwsRegions
{
    public static readonly IReadOnlyList<string> All =
    [
        "us-east-1", "us-east-2", "us-west-1", "us-west-2",
        "ca-central-1", "ca-west-1",
        "eu-central-1", "eu-central-2", "eu-west-1", "eu-west-2", "eu-west-3", "eu-north-1", "eu-south-1", "eu-south-2",
        "ap-south-1", "ap-south-2", "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4",
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3", "ap-east-1",
        "sa-east-1", "me-south-1", "me-central-1", "af-south-1", "il-central-1",
    ];
}
