using Amazon.CloudWatch;
using Amazon.ECS;
using Amazon.ElasticBeanstalk;
using Amazon.Runtime;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;
using CloudWatch = Amazon.CloudWatch.Model;
using Logs = Amazon.CloudWatchLogs.Model;
using Ec2 = Amazon.EC2.Model;
using Ecs = Amazon.ECS.Model;
using Eb = Amazon.ElasticBeanstalk.Model;
using Secrets = Amazon.SecretsManager.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;

namespace Skypeek.Aws;

/// <summary>
/// Typed, read-only access to AWS. Every call first checks the credential monitor (halted profiles are skipped without
/// any network call) and reports auth failures back so the profile can be halted.
/// </summary>
public sealed partial class AwsGateway : IAwsGateway
{
    private const int MaxPages = 200;

    private readonly CredentialMonitor _monitor;
    private readonly AwsClientFactory _clients;
    private readonly IRequestLogSink _log;
    private readonly IElevationApprover? _approver;

    public AwsGateway(CredentialMonitor monitor, AwsClientFactory clients, IRequestLogSink log, IElevationApprover? approver = null)
    {
        _monitor = monitor;
        _clients = clients;
        _log = log;
        _approver = approver;
    }

    private Task<T> Call<T>(Target target, string operation, Func<AwsClientSet, Task<T>> action) =>
        Call(target, operation, action, elevated: false, resource: null);

    /// <summary>
    /// Runs one logical AWS operation. Elevated calls use the target's elevated profile, and calls with a
    /// <paramref name="confirmation"/> text are not pure reads; both require the user's approval for every call. The
    /// allowlist in the SDK pipeline applies to every call; confirmed-only actions pass it only inside an approved scope.
    /// </summary>
    private async Task<T> Call<T>(Target target, string operation, Func<AwsClientSet, Task<T>> action, bool elevated, string? resource,
        string? confirmation = null, string? confirmPhrase = null)
    {
        // The read-only key is only ever used for pure reads; anything else always goes through the elevated key.
        if (confirmation is not null)
            elevated = true;

        var profile = target.ProfileName;
        if (elevated)
        {
            profile = target.ElevatedProfileName is { Length: > 0 } p
                ? p
                : throw new InvalidOperationException(confirmation is null
                    ? $"No elevated profile is configured for {target.DisplayName}."
                    : $"This action needs an elevated profile, and none is configured for {target.DisplayName}. Set one in Settings → Accounts & regions.");
        }

        ProfileCredentials creds;
        try
        {
            creds = _monitor.Acquire(profile);
        }
        catch (CredentialsUnavailableException ex)
        {
            WriteSkip(target, profile, operation, elevated, $"skipped: {ex.Reason}");
            throw;
        }

        var needsApproval = elevated || confirmation is not null;
        if (needsApproval)
        {
            var approved = _approver is not null && await _approver.ApproveAsync(
                new ElevationRequest(target, profile, creds.AccountId, creds.RoleName, operation, resource ?? "", elevated, confirmation, confirmPhrase),
                CancellationToken.None).ConfigureAwait(false);
            if (!approved)
            {
                WriteSkip(target, profile, operation, elevated, "skipped: not approved by the user");
                throw new ElevationDeniedException(operation);
            }
            // The file may have changed while the dialog was open.
            creds = _monitor.Acquire(profile);
        }

        using var scope = RequestScope.Begin(new RequestScopeInfo(profile, creds.AccountId, target.Region, elevated, Approved: needsApproval));
        try
        {
            var result = await action(_clients.Get(creds, target.Region)).ConfigureAwait(false);
            _monitor.ReportSuccess(profile);
            return result;
        }
        catch (AmazonServiceException ex) when (AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
        {
            _monitor.ReportAuthFailure(profile, creds.Fingerprint, ex.ErrorCode);
            throw new CredentialsUnavailableException(profile, $"credentials rejected by AWS ({ex.ErrorCode})");
        }
    }

    private void WriteSkip(Target target, string profile, string operation, bool elevated, string message) =>
        _log.Write(new RequestLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Profile = profile,
            Region = target.Region,
            Service = "gateway",
            Operation = operation,
            Outcome = RequestOutcome.Skipped,
            Message = message,
            Elevated = elevated,
        });

    // ---------------- Secrets Manager / SSM ----------------

    public Task<IReadOnlyList<CatalogItem>> ListSecretsAsync(Target target, CancellationToken ct) =>
        Call<IReadOnlyList<CatalogItem>>(target, "ListSecrets", async c =>
        {
            var items = new List<CatalogItem>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.Secrets.ListSecretsAsync(new Secrets.ListSecretsRequest { MaxResults = 100, NextToken = token, IncludePlannedDeletion = false }, ct);
                foreach (var s in resp.SecretList ?? [])
                    items.Add(MapSecret(target.Id, s.Name, s.ARN, s.Description, s.KmsKeyId, s.RotationEnabled, s.LastChangedDate, s.LastAccessedDate, s.CreatedDate, s.Tags));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            return items;
        });

    public Task<CatalogItem?> DescribeSecretAsync(Target target, string secretId, CancellationToken ct) =>
        Call<CatalogItem?>(target, "DescribeSecret", async c =>
        {
            var s = await c.Secrets.DescribeSecretAsync(new Secrets.DescribeSecretRequest { SecretId = secretId }, ct);
            return MapSecret(target.Id, s.Name, s.ARN, s.Description, s.KmsKeyId, s.RotationEnabled, s.LastChangedDate, s.LastAccessedDate, s.CreatedDate, s.Tags);
        });

    private static CatalogItem MapSecret(long targetId, string name, string? arn, string? description, string? kms, bool? rotation,
        DateTime? changed, DateTime? accessed, DateTime? created, List<Secrets.Tag>? tags) => new()
    {
        TargetId = targetId,
        Kind = CatalogKind.Secret,
        Name = name,
        Arn = arn,
        Description = description,
        KmsKeyId = kms,
        RotationEnabled = rotation,
        LastModified = changed,
        LastAccessed = accessed,
        Created = created,
        Tags = (tags ?? []).Where(t => t.Key is not null).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First().Value ?? ""),
    };

    public Task<IReadOnlyList<CatalogItem>> ListParametersAsync(Target target, CancellationToken ct) =>
        Call<IReadOnlyList<CatalogItem>>(target, "DescribeParameters", async c =>
        {
            var items = new List<CatalogItem>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.Ssm.DescribeParametersAsync(new Ssm.DescribeParametersRequest { MaxResults = 50, NextToken = token }, ct);
                foreach (var p in resp.Parameters ?? [])
                {
                    items.Add(new CatalogItem
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
                    });
                }
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages * 5);
            return items;
        });

    public Task<SecretValueResult> GetSecretValueAsync(Target target, string secretId, bool elevated, CancellationToken ct) =>
        Call(target, "secretsmanager:GetSecretValue", async c =>
        {
            var resp = await c.Secrets.GetSecretValueAsync(new Secrets.GetSecretValueRequest { SecretId = secretId }, ct);
            if (resp.SecretString is { } text)
                return new SecretValueResult(text, resp.VersionId, string.Join(", ", resp.VersionStages ?? []));
            var binary = resp.SecretBinary is null ? "" : Convert.ToBase64String(resp.SecretBinary.ToArray());
            return new SecretValueResult(binary, resp.VersionId, "binary secret (base64)");
        }, elevated, secretId);

    public Task<SecretValueResult> GetParameterValueAsync(Target target, string name, bool decrypt, bool elevated, CancellationToken ct) =>
        Call(target, decrypt ? "ssm:GetParameter (with decryption)" : "ssm:GetParameter", async c =>
        {
            var resp = await c.Ssm.GetParameterAsync(new Ssm.GetParameterRequest { Name = name, WithDecryption = decrypt }, ct);
            return new SecretValueResult(resp.Parameter?.Value ?? "", resp.Parameter?.Version?.ToString(), resp.Parameter?.Type?.Value);
        }, elevated, name);

    // ---------------- Elastic Beanstalk ----------------

    public Task<IReadOnlyList<EbEnvironmentSnapshot>> GetEbEnvironmentsAsync(Target target, DateTime eventsSince, CancellationToken ct, string? onlyEnvironment = null) =>
        Call<IReadOnlyList<EbEnvironmentSnapshot>>(target, "DescribeEnvironments", async c =>
        {
            var envs = new List<Eb.EnvironmentDescription>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.ElasticBeanstalk.DescribeEnvironmentsAsync(new Eb.DescribeEnvironmentsRequest
                {
                    IncludeDeleted = false,
                    EnvironmentNames = onlyEnvironment is null ? null : [onlyEnvironment],
                    NextToken = token,
                }, ct);
                envs.AddRange(resp.Environments ?? []);
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);

            var events = new List<Eb.EventDescription>();
            if (envs.Count > 0)
            {
                token = null;
                pages = 0;
                do
                {
                    var resp = await c.ElasticBeanstalk.DescribeEventsAsync(new Eb.DescribeEventsRequest
                    {
                        EnvironmentName = onlyEnvironment,
                        StartTime = eventsSince,
                        Severity = EventSeverity.INFO,
                        MaxRecords = 1000,
                        NextToken = token,
                    }, ct);
                    events.AddRange(resp.Events ?? []);
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < 5);
            }

            // EC2 details for every instance of every environment, in as few calls as possible.
            var resources = new Dictionary<string, (List<string> Instances, List<string> Groups)>();
            foreach (var env in envs)
            {
                var instances = new List<string>();
                var groups = new List<string>();
                try
                {
                    var res = await c.ElasticBeanstalk.DescribeEnvironmentResourcesAsync(new Eb.DescribeEnvironmentResourcesRequest { EnvironmentId = env.EnvironmentId }, ct);
                    instances.AddRange((res.EnvironmentResources?.Instances ?? []).Select(i => i.Id).Where(id => id is not null));
                    groups.AddRange((res.EnvironmentResources?.AutoScalingGroups ?? []).Select(g => g.Name).Where(n => n is not null));
                }
                catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
                {
                    // Resources are optional detail (e.g. environment is terminating).
                }
                resources[env.EnvironmentId ?? ""] = (instances, groups);
            }
            var ec2 = await DescribeEc2Async(c, resources.Values.SelectMany(r => r.Instances).Distinct().ToList(), ct);
            var versions = await GetLatestVersionsAsync(c, envs, ct);

            var result = new List<EbEnvironmentSnapshot>();
            foreach (var env in envs)
            {
                var (instances, groups) = resources[env.EnvironmentId ?? ""];
                var nonGreen = env.Health?.Value is not ("Green" or null);
                var (causes, requests) = nonGreen && env.EnvironmentName is not null ? await GetEbCausesAsync(c, env.EnvironmentName, ct) : ([], null);
                var health = env.EnvironmentName is null ? [] : await GetEbInstanceHealthAsync(c, env.EnvironmentName, ct);
                versions.TryGetValue(env.ApplicationName ?? "", out var appVersions);

                result.Add(new EbEnvironmentSnapshot
                {
                    Causes = causes,
                    InstanceHealth = MergeNodes(instances, health, ec2),
                    RequestSummary = requests,
                    EnvironmentId = env.EnvironmentId ?? "",
                    EnvironmentName = env.EnvironmentName ?? "",
                    ApplicationName = env.ApplicationName ?? "",
                    Status = env.Status?.Value ?? "",
                    Health = env.Health?.Value ?? "",
                    HealthStatus = env.HealthStatus?.Value,
                    VersionLabel = env.VersionLabel,
                    VersionCreated = env.VersionLabel is not null && appVersions?.Created.TryGetValue(env.VersionLabel, out var created) == true ? created : null,
                    LatestVersionLabel = appVersions?.LatestLabel,
                    LatestVersionCreated = appVersions?.LatestCreated,
                    DateUpdated = env.DateUpdated,
                    Cname = env.CNAME,
                    InstanceIds = instances,
                    AutoScalingGroups = groups,
                    NewEvents = events
                        .Where(e => e.EnvironmentName == env.EnvironmentName)
                        .Select(e => new EbEvent { Date = e.EventDate ?? DateTime.UtcNow, Severity = e.Severity?.Value ?? "", Message = e.Message ?? "" })
                        .ToList(),
                });
            }
            return result;
        });

    private sealed record AppVersions(string? LatestLabel, DateTime? LatestCreated, Dictionary<string, DateTime> Created);

    /// <summary>Newest version per application, plus creation dates of the versions environments run.</summary>
    private static async Task<Dictionary<string, AppVersions>> GetLatestVersionsAsync(AwsClientSet c, List<Eb.EnvironmentDescription> envs, CancellationToken ct)
    {
        var result = new Dictionary<string, AppVersions>();
        foreach (var app in envs.Where(e => e.ApplicationName is not null).GroupBy(e => e.ApplicationName!))
        {
            try
            {
                var resp = await c.ElasticBeanstalk.DescribeApplicationVersionsAsync(new Eb.DescribeApplicationVersionsRequest { ApplicationName = app.Key, MaxRecords = 50 }, ct);
                var list = (resp.ApplicationVersions ?? []).Where(v => v.VersionLabel is not null && v.DateCreated is not null).ToList();
                var created = list.GroupBy(v => v.VersionLabel!).ToDictionary(g => g.Key, g => g.First().DateCreated!.Value);

                // Deployed versions older than the first page: ask for them explicitly.
                var missing = app.Select(e => e.VersionLabel).Where(l => l is not null && !created.ContainsKey(l)).Distinct().ToList();
                if (missing.Count > 0)
                {
                    var more = await c.ElasticBeanstalk.DescribeApplicationVersionsAsync(new Eb.DescribeApplicationVersionsRequest { ApplicationName = app.Key, VersionLabels = missing! }, ct);
                    foreach (var v in more.ApplicationVersions ?? [])
                        if (v.VersionLabel is not null && v.DateCreated is not null)
                            created[v.VersionLabel] = v.DateCreated.Value;
                }

                var latest = list.OrderByDescending(v => v.DateCreated).FirstOrDefault();
                result[app.Key] = new AppVersions(latest?.VersionLabel, latest?.DateCreated, created);
            }
            catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
            {
                // Version status is optional detail.
            }
        }
        return result;
    }

    private static async Task<Dictionary<string, Ec2.Instance>> DescribeEc2Async(AwsClientSet c, List<string> instanceIds, CancellationToken ct)
    {
        var map = new Dictionary<string, Ec2.Instance>();
        foreach (var chunk in instanceIds.Chunk(100))
        {
            try
            {
                var resp = await c.Ec2.DescribeInstancesAsync(new Ec2.DescribeInstancesRequest { InstanceIds = chunk.ToList() }, ct);
                foreach (var i in (resp.Reservations ?? []).SelectMany(r => r.Instances ?? []))
                    if (i.InstanceId is not null)
                        map[i.InstanceId] = i;
            }
            catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
            {
                // e.g. an instance terminated between calls; nodes then show without EC2 details.
            }
        }
        return map;
    }

    private static List<EbInstanceHealth> MergeNodes(List<string> instanceIds, List<EbInstanceHealth> health, Dictionary<string, Ec2.Instance> ec2) =>
        instanceIds.Union(health.Select(h => h.InstanceId))
            .Select(id =>
            {
                var h = health.FirstOrDefault(x => x.InstanceId == id);
                ec2.TryGetValue(id, out var e);
                return new EbInstanceHealth
                {
                    InstanceId = id,
                    HealthStatus = h?.HealthStatus,
                    Color = h?.Color,
                    Causes = h?.Causes ?? [],
                    DeploymentStatus = h?.DeploymentStatus,
                    VersionLabel = h?.VersionLabel,
                    InstanceType = e?.InstanceType?.Value,
                    AvailabilityZone = e?.Placement?.AvailabilityZone,
                    State = e?.State?.Name?.Value,
                    LaunchTime = e?.LaunchTime,
                    PrivateIp = e?.PrivateIpAddress,
                };
            })
            .ToList();

    /// <summary>
    /// Enhanced health explains a non-green environment (causes and request error rates).
    /// Environments with basic health reject these calls; that is not an error for the poll.
    /// </summary>
    private static async Task<(List<string> Causes, string? Requests)> GetEbCausesAsync(AwsClientSet c, string environmentName, CancellationToken ct)
    {
        var causes = new List<string>();
        string? requests = null;
        try
        {
            var health = await c.ElasticBeanstalk.DescribeEnvironmentHealthAsync(new Eb.DescribeEnvironmentHealthRequest
            {
                EnvironmentName = environmentName,
                AttributeNames = [EnvironmentHealthAttribute.All],
            }, ct);
            causes.AddRange(health.Causes ?? []);
            if (health.ApplicationMetrics is { RequestCount: > 0 } m)
            {
                var total = (double)m.RequestCount.Value;
                string Pct(int? n) => n is null ? "?" : $"{n.Value / total * 100:0.#}%";
                var parts = new List<string> { $"{m.RequestCount} req/{m.Duration ?? 10}s" };
                if (m.StatusCodes is { } s)
                    parts.Add($"5xx {Pct(s.Status5xx)} · 4xx {Pct(s.Status4xx)}");
                if (m.Latency?.P90 is { } p90)
                    parts.Add($"p90 {p90 * 1000:0} ms");
                requests = string.Join(" · ", parts);
            }
        }
        catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
        {
            causes.Add(ex.ErrorCode == "InvalidRequestException"
                ? "Enhanced health reporting is off for this environment, so EB gives no cause. Check recent events and logs."
                : $"Could not read health causes ({ex.ErrorCode}).");
        }
        return (causes, requests);
    }

    /// <summary>Per-instance health (enhanced health only; basic-health environments return nothing).</summary>
    private static async Task<List<EbInstanceHealth>> GetEbInstanceHealthAsync(AwsClientSet c, string environmentName, CancellationToken ct)
    {
        var instances = new List<EbInstanceHealth>();
        try
        {
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.ElasticBeanstalk.DescribeInstancesHealthAsync(new Eb.DescribeInstancesHealthRequest
                {
                    EnvironmentName = environmentName,
                    AttributeNames = [InstancesHealthAttribute.All],
                    NextToken = token,
                }, ct);
                instances.AddRange((resp.InstanceHealthList ?? []).Select(i => new EbInstanceHealth
                {
                    InstanceId = i.InstanceId ?? "",
                    HealthStatus = i.HealthStatus,
                    Color = i.Color,
                    Causes = i.Causes ?? [],
                    DeploymentStatus = i.Deployment?.Status,
                    VersionLabel = i.Deployment?.VersionLabel,
                }));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 10);
        }
        catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
        {
            // Basic health: no per-instance data.
        }
        return instances;
    }

    // ---------------- Logs ----------------

    public Task<IReadOnlyList<LogSource>> GetEcsLogSourcesAsync(Target target, EcsServiceSnapshot service, CancellationToken ct) =>
        Call<IReadOnlyList<LogSource>>(target, "DescribeTaskDefinition", async c =>
        {
            var taskDefinition = service.Deployments.FirstOrDefault(d => d.Status == "PRIMARY")?.TaskDefinition
                                 ?? service.Deployments.FirstOrDefault()?.TaskDefinition;
            if (taskDefinition is null)
                return [];

            var resp = await c.Ecs.DescribeTaskDefinitionAsync(new Ecs.DescribeTaskDefinitionRequest { TaskDefinition = taskDefinition }, ct);
            var sources = new List<LogSource>();
            foreach (var container in resp.TaskDefinition?.ContainerDefinitions ?? [])
            {
                var config = container.LogConfiguration;
                var name = container.Name ?? "?";
                if (config?.LogDriver?.Value != "awslogs" || config.Options is null || !config.Options.TryGetValue("awslogs-group", out var group))
                {
                    sources.Add(new LogSource($"{name} (log driver {config?.LogDriver?.Value ?? "none"} — not in CloudWatch)", "", null,
                        "This container does not log to CloudWatch Logs with the awslogs driver."));
                    continue;
                }
                config.Options.TryGetValue("awslogs-stream-prefix", out var prefix);
                // awslogs stream names are <prefix>/<container>/<task id>; without a prefix they are just the task id.
                sources.Add(new LogSource($"{name} · {group}", group, string.IsNullOrEmpty(prefix) ? null : $"{prefix}/{name}/"));
            }
            return sources;
        });

    public Task<IReadOnlyList<LogSource>> GetEbLogSourcesAsync(Target target, string environmentName, CancellationToken ct) =>
        Call<IReadOnlyList<LogSource>>(target, "DescribeLogGroups", async c =>
        {
            var sources = new List<LogSource>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.Logs.DescribeLogGroupsAsync(new Logs.DescribeLogGroupsRequest
                {
                    LogGroupNamePrefix = $"/aws/elasticbeanstalk/{environmentName}/",
                    NextToken = token,
                }, ct);
                foreach (var g in resp.LogGroups ?? [])
                    if (g.LogGroupName is { } name)
                        sources.Add(new LogSource(name[$"/aws/elasticbeanstalk/{environmentName}/".Length..], name, null));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 10);
            return sources.OrderBy(s => s.Label).ToList();
        });

    public Task<LogPage> GetLogEventsAsync(Target target, LogSource source, DateTime startUtc, DateTime endUtc, string? filterPattern, string? nextToken, CancellationToken ct) =>
        Call(target, "FilterLogEvents", async c =>
        {
            var resp = await c.Logs.FilterLogEventsAsync(new Logs.FilterLogEventsRequest
            {
                LogGroupName = source.LogGroup,
                LogStreamNamePrefix = source.StreamPrefix,
                StartTime = new DateTimeOffset(startUtc).ToUnixTimeMilliseconds(),
                EndTime = new DateTimeOffset(endUtc).ToUnixTimeMilliseconds(),
                FilterPattern = string.IsNullOrWhiteSpace(filterPattern) ? null : filterPattern,
                NextToken = nextToken,
                Limit = 1000,
            }, ct);
            var events = (resp.Events ?? [])
                .Select(e => new LogEvent(DateTimeOffset.FromUnixTimeMilliseconds(e.Timestamp ?? 0).UtcDateTime, e.LogStreamName ?? "", (e.Message ?? "").TrimEnd('\r', '\n')))
                .ToList();
            return new LogPage(events, resp.NextToken);
        });

    public Task<IReadOnlyList<EbLogFile>> RequestEbLogsAsync(Target target, string environmentId, string environmentName, bool bundle, CancellationToken ct) =>
        Call<IReadOnlyList<EbLogFile>>(target, bundle ? "elasticbeanstalk:RequestEnvironmentInfo (full log bundle)" : "elasticbeanstalk:RequestEnvironmentInfo (last 100 lines)", async c =>
        {
            var infoType = bundle ? EnvironmentInfoType.Bundle : EnvironmentInfoType.Tail;
            var requestedAt = DateTime.UtcNow.AddSeconds(-5);
            await c.ElasticBeanstalk.RequestEnvironmentInfoAsync(new Eb.RequestEnvironmentInfoRequest { EnvironmentId = environmentId, InfoType = infoType }, ct);

            // EB collects asynchronously; poll until every instance reported (or give up after ~2 minutes).
            var deadline = DateTime.UtcNow.AddMinutes(bundle ? 3 : 2);
            List<EbLogFile> files = [];
            var lastCount = -1;
            var stableRounds = 0;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                var resp = await c.ElasticBeanstalk.RetrieveEnvironmentInfoAsync(new Eb.RetrieveEnvironmentInfoRequest { EnvironmentId = environmentId, InfoType = infoType }, ct);
                files = (resp.EnvironmentInfo ?? [])
                    .Where(i => i.SampleTimestamp is { } t && t.ToUniversalTime() >= requestedAt && i.Message is not null)
                    .GroupBy(i => i.Ec2InstanceId)
                    .Select(g => g.OrderByDescending(i => i.SampleTimestamp).First())
                    .Select(i => new EbLogFile(i.Ec2InstanceId ?? "?", i.SampleTimestamp!.Value.ToUniversalTime(), i.Message!, bundle))
                    .ToList();
                // Instances report one by one; stop once the count has settled.
                stableRounds = files.Count > 0 && files.Count == lastCount ? stableRounds + 1 : 0;
                lastCount = files.Count;
                if (stableRounds >= 2)
                    break;
            }
            return files;
        }, elevated: true, environmentName,
        confirmation: "Asks Elastic Beanstalk to copy the instances' logs into its own S3 bucket (the same as \"Request logs\" in the console). No resources are changed. The logs are shown here and never stored by Skypeek.");

    private static readonly HttpClient LogDownloads = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> DownloadEbLogAsync(Target target, EbLogFile file, CancellationToken ct)
    {
        // Pre-signed S3 links carry their own short-lived signature; no AWS credentials are sent.
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Elastic Beanstalk returned an unexpected log link; not downloading it.");

        var started = DateTime.UtcNow;
        try
        {
            var text = await LogDownloads.GetStringAsync(uri, ct);
            WriteDownload(target, file, RequestOutcome.Success, 200, started, null);
            return text;
        }
        catch (HttpRequestException ex)
        {
            WriteDownload(target, file, RequestOutcome.Error, (int?)ex.StatusCode, started, ex.Message);
            throw;
        }
    }

    // ---------------- Write actions (elevated key + per-call approval) ----------------

    public Task DeployEbVersionAsync(Target target, EbEnvironmentSnapshot env, string versionLabel, CancellationToken ct) =>
        Call(target, $"elasticbeanstalk:UpdateEnvironment (deploy version {versionLabel})", async c =>
        {
            // Only EnvironmentId + VersionLabel; the pipeline guard rejects any other change.
            await c.ElasticBeanstalk.UpdateEnvironmentAsync(new Eb.UpdateEnvironmentRequest { EnvironmentId = env.EnvironmentId, VersionLabel = versionLabel }, ct);
            return true;
        }, elevated: true, env.EnvironmentName,
        confirmation: (versionLabel == env.VersionLabel
                          ? $"Redeploys the current version {versionLabel} to {env.EnvironmentName}."
                          : $"Deploys version {versionLabel} to {env.EnvironmentName}, replacing {env.VersionLabel}.")
                      + " Instances are updated according to the environment's deployment policy; the application can be briefly unavailable.");

    public Task RestartEbAppServersAsync(Target target, EbEnvironmentSnapshot env, CancellationToken ct) =>
        Call(target, "elasticbeanstalk:RestartAppServer", async c =>
        {
            await c.ElasticBeanstalk.RestartAppServerAsync(new Eb.RestartAppServerRequest { EnvironmentId = env.EnvironmentId }, ct);
            return true;
        }, elevated: true, env.EnvironmentName,
        confirmation: $"Restarts the application server on all {env.InstanceIds.Count} instance(s) of {env.EnvironmentName} at the same time (the EC2 instances are not rebooted). Requests can fail for a few seconds.");

    public Task RebootEbInstanceAsync(Target target, EbEnvironmentSnapshot env, string instanceId, CancellationToken ct) =>
        Call(target, "ec2:RebootInstances", async c =>
        {
            await EnsureInstanceInEnvironmentAsync(c, env, instanceId, ct);
            await c.Ec2.RebootInstancesAsync(new Ec2.RebootInstancesRequest { InstanceIds = [instanceId] }, ct);
            return true;
        }, elevated: true, $"{instanceId} ({env.EnvironmentName})",
        confirmation: $"Reboots EC2 instance {instanceId} of {env.EnvironmentName}. It stops serving traffic for a few minutes while it restarts.");

    public Task TerminateEbInstanceAsync(Target target, EbEnvironmentSnapshot env, string instanceId, CancellationToken ct) =>
        Call(target, "ec2:TerminateInstances", async c =>
        {
            await EnsureInstanceInEnvironmentAsync(c, env, instanceId, ct);
            await c.Ec2.TerminateInstancesAsync(new Ec2.TerminateInstancesRequest { InstanceIds = [instanceId] }, ct);
            return true;
        }, elevated: true, $"{instanceId} ({env.EnvironmentName})",
        confirmation: $"PERMANENTLY terminates EC2 instance {instanceId} of {env.EnvironmentName}. The environment's Auto Scaling group launches a replacement, but anything stored on this instance is lost. This cannot be undone.",
        confirmPhrase: instanceId);

    /// <summary>Checks right before acting that the instance still belongs to the environment (it may have been replaced).</summary>
    private static async Task EnsureInstanceInEnvironmentAsync(AwsClientSet c, EbEnvironmentSnapshot env, string instanceId, CancellationToken ct)
    {
        var res = await c.ElasticBeanstalk.DescribeEnvironmentResourcesAsync(new Eb.DescribeEnvironmentResourcesRequest { EnvironmentId = env.EnvironmentId }, ct);
        if (!(res.EnvironmentResources?.Instances ?? []).Any(i => i.Id == instanceId))
            throw new InvalidOperationException($"{instanceId} is no longer part of {env.EnvironmentName}; nothing was changed. Refresh health and try again.");
    }
    private void WriteDownload(Target target, EbLogFile file, RequestOutcome outcome, int? status, DateTime started, string? message) =>
        _log.Write(new RequestLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Profile = target.ProfileName,
            Region = target.Region,
            Service = "s3 (pre-signed link)",
            Operation = "Download EB tail log",
            // The link itself contains a signature, so only the instance is logged.
            Parameters = $"InstanceId={file.InstanceId}",
            Outcome = outcome,
            HttpStatus = status,
            DurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
            Message = message,
        });

    // ---------------- ECS ----------------

    public Task<IReadOnlyList<EcsServiceSnapshot>> GetEcsServicesAsync(Target target, CancellationToken ct, string? onlyClusterArn = null, string? onlyServiceArn = null) =>
        Call<IReadOnlyList<EcsServiceSnapshot>>(target, onlyServiceArn is null ? "ListClusters" : "DescribeServices", async c =>
        {
            var single = onlyClusterArn is not null && onlyServiceArn is not null;
            var clusters = new List<string>();
            string? token = null;
            var pages = 0;
            if (single)
            {
                clusters.Add(onlyClusterArn!);
            }
            else
            {
                do
                {
                    var resp = await c.Ecs.ListClustersAsync(new Ecs.ListClustersRequest { NextToken = token }, ct);
                    clusters.AddRange(resp.ClusterArns ?? []);
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            }

            var result = new List<EcsServiceSnapshot>();
            foreach (var clusterArn in clusters)
            {
                var serviceArns = new List<string>();
                token = null;
                pages = 0;
                if (single)
                {
                    serviceArns.Add(onlyServiceArn!);
                }
                else
                {
                    do
                    {
                        var resp = await c.Ecs.ListServicesAsync(new Ecs.ListServicesRequest { Cluster = clusterArn, MaxResults = 100, NextToken = token }, ct);
                        serviceArns.AddRange(resp.ServiceArns ?? []);
                        token = resp.NextToken;
                    } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
                }

                foreach (var chunk in serviceArns.Chunk(10))
                {
                    var resp = await c.Ecs.DescribeServicesAsync(new Ecs.DescribeServicesRequest { Cluster = clusterArn, Services = chunk.ToList() }, ct);
                    foreach (var s in resp.Services ?? [])
                    {
                        result.Add(new EcsServiceSnapshot
                        {
                            ClusterArn = clusterArn,
                            ClusterName = NameFromArn(clusterArn),
                            ServiceName = s.ServiceName ?? "",
                            ServiceArn = s.ServiceArn,
                            Status = s.Status ?? "",
                            Desired = s.DesiredCount ?? 0,
                            Running = s.RunningCount ?? 0,
                            Pending = s.PendingCount ?? 0,
                            LaunchType = s.LaunchType?.Value,
                            Deployments = (s.Deployments ?? []).Select(d => new EcsDeploymentInfo
                            {
                                Id = d.Id ?? "",
                                Status = d.Status ?? "",
                                RolloutState = d.RolloutState?.Value,
                                RolloutStateReason = d.RolloutStateReason,
                                Desired = d.DesiredCount ?? 0,
                                Running = d.RunningCount ?? 0,
                                Pending = d.PendingCount ?? 0,
                                Failed = d.FailedTasks ?? 0,
                                CreatedAt = d.CreatedAt,
                                UpdatedAt = d.UpdatedAt,
                                TaskDefinition = d.TaskDefinition is { } td ? NameFromArn(td) : null,
                            }).ToList(),
                            Events = (s.Events ?? []).Take(8).Select(e => $"{e.CreatedAt?.ToLocalTime():g}  {e.Message}").ToList(),
                        });
                    }
                }
            }
            return result;
        });

    public Task<IReadOnlyList<string>> GetStoppedTaskReasonsAsync(Target target, string cluster, string service, CancellationToken ct) =>
        Call<IReadOnlyList<string>>(target, "ListTasks", async c =>
        {
            var list = await c.Ecs.ListTasksAsync(new Ecs.ListTasksRequest { Cluster = cluster, ServiceName = service, DesiredStatus = DesiredStatus.STOPPED, MaxResults = 10 }, ct);
            var arns = list.TaskArns ?? [];
            if (arns.Count == 0)
                return [];
            var tasks = await c.Ecs.DescribeTasksAsync(new Ecs.DescribeTasksRequest { Cluster = cluster, Tasks = arns }, ct);
            return (tasks.Tasks ?? [])
                .OrderByDescending(t => t.StoppedAt)
                .Select(t =>
                {
                    var containers = string.Join("; ", (t.Containers ?? [])
                        .Where(x => !string.IsNullOrEmpty(x.Reason) || x.ExitCode is not null and not 0)
                        .Select(x => $"{x.Name}: {x.Reason ?? $"exit {x.ExitCode}"}"));
                    return $"{t.StoppedAt?.ToLocalTime():g}  {t.StoppedReason}{(containers.Length > 0 ? $" — {containers}" : "")}";
                })
                .ToList();
        });

    // ---------------- CloudWatch ----------------

    public Task<IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>>> GetMetricDataAsync(Target target, IReadOnlyList<MetricQuery> queries, DateTime start, DateTime end, CancellationToken ct) =>
        Call<IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>>>(target, "GetMetricData", async c =>
        {
            var result = new Dictionary<string, List<MetricPoint>>();
            foreach (var chunk in queries.Chunk(500))
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.CloudWatch.GetMetricDataAsync(new CloudWatch.GetMetricDataRequest
                    {
                        StartTime = start,
                        EndTime = end,
                        ScanBy = ScanBy.TimestampAscending,
                        NextToken = token,
                        MetricDataQueries = chunk.Select(q => new CloudWatch.MetricDataQuery
                        {
                            Id = q.Id,
                            ReturnData = true,
                            MetricStat = new CloudWatch.MetricStat
                            {
                                Period = q.PeriodSeconds,
                                Stat = q.Stat,
                                Metric = new CloudWatch.Metric
                                {
                                    Namespace = q.Namespace,
                                    MetricName = q.MetricName,
                                    Dimensions = q.Dimensions.Select(d => new CloudWatch.Dimension { Name = d.Key, Value = d.Value }).ToList(),
                                },
                            },
                        }).ToList(),
                    }, ct);

                    foreach (var r in resp.MetricDataResults ?? [])
                    {
                        if (r.Id is null)
                            continue;
                        if (!result.TryGetValue(r.Id, out var points))
                            result[r.Id] = points = [];
                        var timestamps = r.Timestamps ?? [];
                        var values = r.Values ?? [];
                        for (var i = 0; i < Math.Min(timestamps.Count, values.Count); i++)
                            points.Add(new MetricPoint(timestamps[i].ToUniversalTime(), values[i]));
                    }
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < 20);
            }
            return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<MetricPoint>)kv.Value.OrderBy(p => p.Timestamp).ToList());
        });

    public Task<IReadOnlyList<MetricDescriptor>> ListMetricsAsync(Target target, string ns, string metricName, CancellationToken ct) =>
        Call<IReadOnlyList<MetricDescriptor>>(target, "ListMetrics", async c =>
        {
            var list = new List<MetricDescriptor>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.CloudWatch.ListMetricsAsync(new CloudWatch.ListMetricsRequest { Namespace = ns, MetricName = metricName, NextToken = token }, ct);
                foreach (var m in resp.Metrics ?? [])
                    list.Add(new MetricDescriptor(m.Namespace ?? ns, m.MetricName ?? metricName,
                        (m.Dimensions ?? []).Where(d => d.Name is not null).ToDictionary(d => d.Name, d => d.Value ?? "")));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 20);
            return list;
        });

    public Task<AlarmsResult> GetAlarmsAsync(Target target, DateTime historySince, CancellationToken ct) =>
        Call(target, "DescribeAlarms", async c =>
        {
            var alarms = new Dictionary<string, AlarmInfo>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.CloudWatch.DescribeAlarmsAsync(new CloudWatch.DescribeAlarmsRequest
                {
                    AlarmTypes = [AlarmType.MetricAlarm],
                    MaxRecords = 100,
                    NextToken = token,
                }, ct);
                foreach (var a in resp.MetricAlarms ?? [])
                {
                    if (a.AlarmName is null)
                        continue;
                    // Metric-math alarms: take the first underlying metric for matching.
                    var metric = (a.Metrics ?? []).Select(m => m.MetricStat?.Metric).FirstOrDefault(m => m is not null);
                    var dims = a.Dimensions is { Count: > 0 } ? a.Dimensions : metric?.Dimensions ?? [];
                    alarms[a.AlarmName] = new AlarmInfo
                    {
                        Name = a.AlarmName,
                        Arn = a.AlarmArn,
                        State = a.StateValue?.Value ?? "",
                        StateReason = a.StateReason,
                        MetricName = a.MetricName ?? metric?.MetricName,
                        Namespace = a.Namespace ?? metric?.Namespace,
                        Dimensions = dims.Where(d => d.Name is not null).GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.First().Value ?? ""),
                        StateUpdated = a.StateUpdatedTimestamp,
                    };
                }
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 50);

            token = null;
            pages = 0;
            do
            {
                var resp = await c.CloudWatch.DescribeAlarmHistoryAsync(new CloudWatch.DescribeAlarmHistoryRequest
                {
                    HistoryItemType = HistoryItemType.StateUpdate,
                    AlarmTypes = [AlarmType.MetricAlarm],
                    StartDate = historySince,
                    EndDate = DateTime.UtcNow,
                    ScanBy = ScanBy.TimestampDescending,
                    MaxRecords = 100,
                    NextToken = token,
                }, ct);
                foreach (var item in resp.AlarmHistoryItems ?? [])
                {
                    if (item.AlarmName is null || !alarms.TryGetValue(item.AlarmName, out var alarm))
                        continue;
                    if (item.HistorySummary?.Contains("to ALARM", StringComparison.Ordinal) != true)
                        continue;
                    var at = item.Timestamp?.ToUniversalTime();
                    if (at is not null && (alarm.RecentAlarmAt is null || at > alarm.RecentAlarmAt))
                    {
                        alarm.RecentAlarmAt = at;
                        alarm.RecentAlarmSummary = item.HistorySummary;
                    }
                }
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 20);

            return new AlarmsResult { Alarms = alarms.Values.ToList() };
        });

    private static string NameFromArn(string arn)
    {
        var slash = arn.LastIndexOf('/');
        return slash >= 0 ? arn[(slash + 1)..] : arn;
    }
}
