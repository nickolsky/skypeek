using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;

namespace Skypeek.Core;

/// <summary>
/// The only way the app talks to AWS. Every method is a read operation; there is deliberately no generic execute.
/// </summary>
public interface IAwsGateway
{
    Task<IReadOnlyList<CatalogItem>> ListSecretsAsync(Target target, CancellationToken ct);
    Task<IReadOnlyList<CatalogItem>> ListParametersAsync(Target target, CancellationToken ct);
    Task<CatalogItem?> DescribeSecretAsync(Target target, string secretId, CancellationToken ct);
    /// <param name="elevated">Use the target's elevated profile; the user is asked to approve the call first.</param>
    Task<SecretValueResult> GetSecretValueAsync(Target target, string secretId, bool elevated, CancellationToken ct);
    Task<SecretValueResult> GetParameterValueAsync(Target target, string name, bool decrypt, bool elevated, CancellationToken ct);

    /// <param name="onlyEnvironment">Refresh a single environment instead of all of them.</param>
    Task<IReadOnlyList<EbEnvironmentSnapshot>> GetEbEnvironmentsAsync(Target target, DateTime eventsSince, CancellationToken ct, string? onlyEnvironment = null);
    /// <param name="onlyClusterArn">With <paramref name="onlyServiceArn"/>: refresh a single service instead of listing all.</param>
    Task<IReadOnlyList<EcsServiceSnapshot>> GetEcsServicesAsync(Target target, CancellationToken ct, string? onlyClusterArn = null, string? onlyServiceArn = null);
    Task<IReadOnlyList<string>> GetStoppedTaskReasonsAsync(Target target, string cluster, string service, CancellationToken ct);

    /// <summary>All versions of an EB application, newest first.</summary>
    Task<IReadOnlyList<EbApplicationVersion>> GetEbApplicationVersionsAsync(Target target, string application, CancellationToken ct);

    /// <summary>RDS instances and clusters (Aurora / Multi-AZ clusters) with their events since <paramref name="eventsSince"/>.</summary>
    /// <param name="onlyInstance">Refresh one instance.</param>
    /// <param name="onlyCluster">Refresh one cluster and its member instances.</param>
    Task<RdsInventory> GetRdsAsync(Target target, DateTime eventsSince, CancellationToken ct, string? onlyInstance = null, string? onlyCluster = null);
    /// <summary>ElastiCache replication groups, standalone cache clusters and serverless caches.</summary>
    /// <param name="only">Refresh one cache (same kind and id as a previous snapshot).</param>
    Task<IReadOnlyList<CacheSnapshot>> GetCachesAsync(Target target, DateTime eventsSince, CancellationToken ct, CacheSnapshot? only = null);

    /// <summary>EC2 instances (not terminated) with their status checks and scheduled events.</summary>
    /// <param name="onlyIds">Refresh these instances only.</param>
    Task<IReadOnlyList<Ec2InstanceSnapshot>> GetEc2InstancesAsync(Target target, CancellationToken ct, IReadOnlyList<string>? onlyIds = null);
    /// <summary>Application/network/gateway load balancers with their target groups and target health.</summary>
    /// <param name="onlyArn">Refresh one load balancer.</param>
    Task<IReadOnlyList<LoadBalancerSnapshot>> GetLoadBalancersAsync(Target target, CancellationToken ct, string? onlyArn = null);
    /// <summary>Listeners of a load balancer with their rules (conditions and actions as text).</summary>
    Task<IReadOnlyList<LbListenerInfo>> GetLoadBalancerListenersAsync(Target target, LoadBalancerSnapshot lb, CancellationToken ct);

    Task<IReadOnlyList<VpnConnectionSnapshot>> GetVpnConnectionsAsync(Target target, CancellationToken ct, string? onlyId = null);
    /// <param name="knownLatest">Newest build id per project from the previous poll (projects without recent builds keep it).</param>
    Task<IReadOnlyList<CodeBuildProjectSnapshot>> GetCodeBuildProjectsAsync(Target target, IReadOnlyDictionary<string, string> knownLatest, CancellationToken ct, string? onlyProject = null);
    Task<IReadOnlyList<CodeBuildRun>> GetCodeBuildHistoryAsync(Target target, string project, int max, CancellationToken ct);
    Task<IReadOnlyList<StackSnapshot>> GetStacksAsync(Target target, CancellationToken ct, string? onlyStack = null);
    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<StackEventInfo>> GetStackEventsAsync(Target target, string stack, int max, CancellationToken ct);
    Task<IReadOnlyList<RedshiftSnapshot>> GetRedshiftAsync(Target target, CancellationToken ct, RedshiftSnapshot? only = null);

    /// <summary>
    /// Runs AWS Reachability Analyzer once (elevated key, approval, $0.10): creates the path, starts the analysis, waits
    /// for it and deletes both again.
    /// </summary>
    Task<CatalogItem?> DescribeParameterAsync(Target target, string name, CancellationToken ct);
    /// <summary>New value of a secret (a new AWSCURRENT version). Fails when the secret changed since <paramref name="secret"/> was read.</summary>
    Task UpdateSecretValueAsync(Target target, CatalogItem secret, string value, CancellationToken ct);
    Task CreateSecretAsync(Target target, string name, string value, string? description, string? kmsKeyId, CancellationToken ct);
    /// <summary>Schedules deletion with a 7–30 day recovery window (asks for the name to be typed).</summary>
    Task DeleteSecretAsync(Target target, CatalogItem secret, int recoveryDays, CancellationToken ct);
    Task RestoreSecretAsync(Target target, CatalogItem secret, CancellationToken ct);
    /// <summary>Overwrites a parameter, keeping its type, key and tier. Fails when its version changed since it was read.</summary>
    Task PutParameterValueAsync(Target target, CatalogItem parameter, string value, CancellationToken ct);
    Task CreateParameterAsync(Target target, string name, string value, string type, string? description, string? tier, string? kmsKeyId, CancellationToken ct);
    Task DeleteParameterAsync(Target target, CatalogItem parameter, CancellationToken ct);

    Task<AwsReachResult> VerifyReachAsync(Target target, string sourceId, string? destinationId, string? destinationIp, string protocol, int? port, CancellationToken ct);

    /// <summary>VPCs, subnets, network interfaces, Elastic IPs and security groups with their rules.</summary>
    Task<NetworkSnapshot> GetNetworkAsync(Target target, CancellationToken ct);
    /// <summary>Re-reads some security groups and their rules (after an edit).</summary>
    Task<IReadOnlyList<SecurityGroupInfo>> GetSecurityGroupsAsync(Target target, IReadOnlyList<string> groupIds, CancellationToken ct);

    /// <summary>On-demand prices from the AWS Price List API (a free, public-price read).</summary>
    Task<IReadOnlyList<PriceItem>> GetPricesAsync(Target target, PriceQuery query, CancellationToken ct);
    /// <summary>Billed cost of the target's region from Cost Explorer: by service this and last month, and per resource for 14 days when available.</summary>
    Task<ActualCosts> GetActualCostsAsync(Target target, CancellationToken ct);
    /// <summary>Major versions of an RDS engine that have billed Extended Support, with the date it starts.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetRdsExtendedSupportStartsAsync(Target target, string engine, CancellationToken ct);

    /// <summary>The instance's log files (newest first) plus log groups it exports to CloudWatch Logs.</summary>
    Task<IReadOnlyList<LogSource>> GetRdsLogSourcesAsync(Target target, RdsInstanceSnapshot db, CancellationToken ct);
    /// <summary>
    /// Reads an RDS log file: without <paramref name="marker"/> the last <paramref name="lines"/> lines, with a marker
    /// everything written after it (for live tail).
    /// </summary>
    Task<RdsLogPortion> DownloadRdsLogAsync(Target target, string instanceId, string fileName, string? marker, int? lines, CancellationToken ct);

    Task<IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>>> GetMetricDataAsync(Target target, IReadOnlyList<MetricQuery> queries, DateTime start, DateTime end, CancellationToken ct);
    Task<IReadOnlyList<MetricDescriptor>> ListMetricsAsync(Target target, string ns, string metricName, CancellationToken ct);
    Task<AlarmsResult> GetAlarmsAsync(Target target, DateTime historySince, CancellationToken ct);

    /// <summary>awslogs destinations of the service's containers (from its current task definition).</summary>
    Task<IReadOnlyList<LogSource>> GetEcsLogSourcesAsync(Target target, EcsServiceSnapshot service, CancellationToken ct);
    /// <summary>CloudWatch log groups streamed by an EB environment (/aws/elasticbeanstalk/&lt;env&gt;/…).</summary>
    Task<IReadOnlyList<LogSource>> GetEbLogSourcesAsync(Target target, string environmentName, CancellationToken ct);
    Task<LogPage> GetLogEventsAsync(Target target, LogSource source, DateTime startUtc, DateTime endUtc, string? filterPattern, string? nextToken, CancellationToken ct);

    /// <summary>
    /// Asks Elastic Beanstalk to collect instance logs (tail or full bundle) into its S3 bucket, then returns the links.
    /// Not a pure read, so it always uses the elevated profile and the user confirms every call.
    /// </summary>
    Task<IReadOnlyList<EbLogFile>> RequestEbLogsAsync(Target target, string environmentId, string environmentName, bool bundle, CancellationToken ct);
    /// <summary>Downloads a tail log text file from the pre-signed S3 link EB returned.</summary>
    Task<string> DownloadEbLogAsync(Target target, EbLogFile file, CancellationToken ct);

    // ---- Write actions: always the elevated profile, each call approved by the user. ----

    /// <summary>Deploys an existing application version to the environment (only the version changes).</summary>
    Task DeployEbVersionAsync(Target target, EbEnvironmentSnapshot env, string versionLabel, CancellationToken ct);
    /// <summary>Restarts the application server on every instance of the environment (no EC2 reboot).</summary>
    Task RestartEbAppServersAsync(Target target, EbEnvironmentSnapshot env, CancellationToken ct);
    /// <summary>Reboots one EC2 instance that belongs to the environment.</summary>
    Task RebootEbInstanceAsync(Target target, EbEnvironmentSnapshot env, string instanceId, CancellationToken ct);
    /// <summary>Terminates one EC2 instance of the environment; its Auto Scaling group launches a replacement.</summary>
    Task TerminateEbInstanceAsync(Target target, EbEnvironmentSnapshot env, string instanceId, CancellationToken ct);
    /// <summary>Starts a new deployment of an ECS service with its current task definition (only ForceNewDeployment is sent).</summary>
    Task ForceNewEcsDeploymentAsync(Target target, EcsServiceSnapshot service, CancellationToken ct);

    /// <summary>Starts one stopped EC2 instance.</summary>
    Task StartEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct);
    /// <summary>Stops one EC2 instance (normal shutdown; no force, no hibernation).</summary>
    Task StopEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct);
    /// <summary>Reboots one running EC2 instance.</summary>
    Task RebootEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct);

    /// <summary>Adds one rule with one source to a security group.</summary>
    Task AddSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleSpec rule, CancellationToken ct);
    /// <summary>Changes one existing rule in place (same direction; protocol, ports, source and description may change).</summary>
    Task UpdateSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleInfo current, SecurityGroupRuleSpec updated, CancellationToken ct);
    /// <summary>Deletes one rule of a security group by its rule id.</summary>
    Task DeleteSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleInfo rule, CancellationToken ct);
}

/// <param name="Elevated">Signed with the target's elevated profile.</param>
/// <param name="Explanation">Why confirmation is needed, for calls that are not pure reads.</param>
/// <param name="ConfirmPhrase">For irreversible actions: the user must type this text before "Allow" is enabled.</param>
public sealed record ElevationRequest(Target Target, string Profile, string? AccountId, string? RoleName, string Operation, string Resource,
    bool Elevated = true, string? Explanation = null, string? ConfirmPhrase = null);

/// <summary>
/// Asks the user to approve one call: every call with the elevated profile, and every non-read action (with any key).
/// </summary>
public interface IElevationApprover
{
    Task<bool> ApproveAsync(ElevationRequest request, CancellationToken ct);
}

public sealed class ElevationDeniedException(string operation) : Exception($"Elevated access for {operation} was not approved.");

public sealed record ValidationResult(bool Success, string? AccountId, string? ErrorCode, bool IsAuthFailure);

/// <summary>Validates a specific set of credentials (bypasses the halt check on purpose).</summary>
public interface ICredentialValidator
{
    Task<ValidationResult> ValidateAsync(ProfileCredentials credentials, string region, CancellationToken ct);
}

public interface ICredentialHaltStore
{
    IReadOnlyList<CredentialHalt> LoadHalts();
    void SaveHalt(CredentialHalt halt);
    void DeleteHalt(string profile);
}

public interface ISettingsStore
{
    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
    IReadOnlyList<Target> LoadTargets();
    Target SaveTarget(Target target);
    void DeleteTarget(long id);
}

public interface ICatalogStore
{
    IReadOnlyList<CatalogItem> LoadAll();
    void ReplaceSnapshot(long targetId, CatalogKind kind, IReadOnlyList<CatalogItem> items, DateTime now);
    void DeleteTarget(long targetId);
}

public interface ISyncRunStore
{
    void Record(SyncRun run);
    IReadOnlyList<SyncRun> LatestRuns();
}

public interface IHealthStore
{
    void Save(long targetId, TargetHealth health);
    IReadOnlyList<TargetHealth> LoadAll();
}

/// <summary>Cached Network tab data, one snapshot per target (deleted with the target).</summary>
public interface INetworkStore
{
    void Save(long targetId, NetworkSnapshot snapshot);
    IReadOnlyList<NetworkSnapshot> LoadAll();
}

/// <summary>Cached prices and billed costs, one snapshot per target (deleted with the target).</summary>
public interface ICostStore
{
    void Save(long targetId, CostSnapshot snapshot);
    IReadOnlyList<CostSnapshot> LoadAll();
}

/// <summary>Monthly counts of the calls that AWS bills (see <see cref="Models.PaidApi"/>); unaffected by the request log's retention.</summary>
public interface IApiUsageStore
{
    IReadOnlyList<ApiUsageRow> Usage(string fromMonth);
    /// <summary>The first paid call ever counted (when counting began, for projections); null before any.</summary>
    DateTime? CountingStarted();
}

public interface IRequestLogStore
{
    void Append(RequestLogEntry entry);
    IReadOnlyList<RequestLogEntry> Recent(int limit);
    int Purge(DateTime olderThanUtc);
}

public interface INotifier
{
    void Notify(string title, string message, string? detail = null);
}
