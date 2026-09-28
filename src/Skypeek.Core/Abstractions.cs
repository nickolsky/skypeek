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
