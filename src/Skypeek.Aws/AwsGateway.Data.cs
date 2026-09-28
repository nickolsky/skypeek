using System.Collections.Concurrent;
using Amazon.Runtime;
using Skypeek.Core.Health;
using Skypeek.Core.Models;
using Eb = Amazon.ElasticBeanstalk.Model;
using ElastiCache = Amazon.ElastiCache.Model;
using Logs = Amazon.CloudWatchLogs.Model;
using Rds = Amazon.RDS.Model;

namespace Skypeek.Aws;

/// <summary>RDS, ElastiCache and EB application versions (all read-only).</summary>
public sealed partial class AwsGateway
{
    private static readonly TimeSpan ParameterCacheTtl = TimeSpan.FromHours(1);

    /// <summary>max_connections set in custom parameter groups, per profile/region/group (they rarely change).</summary>
    private readonly ConcurrentDictionary<(string Profile, string Region, string Group), (DateTime At, string? Value)> _maxConnections = new();

    // ---------------- Elastic Beanstalk application versions ----------------

    public Task<IReadOnlyList<EbApplicationVersion>> GetEbApplicationVersionsAsync(Target target, string application, CancellationToken ct) =>
        Call<IReadOnlyList<EbApplicationVersion>>(target, "DescribeApplicationVersions", async c =>
        {
            var versions = new List<EbApplicationVersion>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.ElasticBeanstalk.DescribeApplicationVersionsAsync(new Eb.DescribeApplicationVersionsRequest
                {
                    ApplicationName = application,
                    MaxRecords = 100,
                    NextToken = token,
                }, ct);
                versions.AddRange((resp.ApplicationVersions ?? []).Where(v => v.VersionLabel is not null).Select(v => new EbApplicationVersion
                {
                    Label = v.VersionLabel,
                    Description = v.Description,
                    Created = v.DateCreated,
                    Status = v.Status?.Value,
                    Source = v.SourceBundle is { S3Bucket: { } bucket, S3Key: { } key } ? $"s3://{bucket}/{key}"
                        : v.SourceBuildInformation?.SourceLocation,
                }));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 5);
            return versions.OrderByDescending(v => v.Created).ToList();
        });

    // ---------------- RDS ----------------

    public Task<RdsInventory> GetRdsAsync(Target target, DateTime eventsSince, CancellationToken ct, string? onlyInstance = null, string? onlyCluster = null) =>
        Call(target, "DescribeDBInstances", async c =>
        {
            var clusters = new List<Rds.DBCluster>();
            string? marker = null;
            var pages = 0;
            if (onlyInstance is null)
            {
                do
                {
                    var resp = await c.Rds.DescribeDBClustersAsync(new Rds.DescribeDBClustersRequest { DBClusterIdentifier = onlyCluster, Marker = marker }, ct);
                    clusters.AddRange(resp.DBClusters ?? []);
                    marker = resp.Marker;
                } while (!string.IsNullOrEmpty(marker) && ++pages < MaxPages);
            }

            var instances = new List<Rds.DBInstance>();
            marker = null;
            pages = 0;
            do
            {
                var resp = await c.Rds.DescribeDBInstancesAsync(new Rds.DescribeDBInstancesRequest
                {
                    DBInstanceIdentifier = onlyInstance,
                    Filters = onlyCluster is null ? null : [new Rds.Filter { Name = "db-cluster-id", Values = [onlyCluster] }],
                    Marker = marker,
                }, ct);
                instances.AddRange(resp.DBInstances ?? []);
                marker = resp.Marker;
            } while (!string.IsNullOrEmpty(marker) && ++pages < MaxPages);

            var events = await GetRdsEventsAsync(c, eventsSince, onlyInstance, ct);
            List<ServiceEvent> EventsOf(string? id) => events.Where(e => e.Source == id).ToList();

            var writers = clusters.SelectMany(cl => (cl.DBClusterMembers ?? []).Select(m => (m.DBInstanceIdentifier, m.IsClusterWriter == true)))
                .Where(m => m.DBInstanceIdentifier is not null)
                .GroupBy(m => m.DBInstanceIdentifier!)
                .ToDictionary(g => g.Key, g => g.First().Item2);

            var result = new RdsInventory();
            foreach (var db in instances.Where(i => i.DBInstanceIdentifier is not null))
            {
                var cluster = clusters.FirstOrDefault(cl => cl.DBClusterIdentifier == db.DBClusterIdentifier);
                var group = db.DBParameterGroups?.FirstOrDefault();
                var parameter = group?.DBParameterGroupName is { } name ? await GetMaxConnectionsParameterAsync(c, target, name, db.Engine ?? "", ct) : null;
                var (maxConnections, maxSource) = HealthRules.EstimateMaxConnections(db.Engine ?? "", db.DBInstanceClass ?? "", parameter,
                    cluster?.ServerlessV2ScalingConfiguration?.MaxCapacity);
                var replication = (db.StatusInfos ?? []).FirstOrDefault(s => s.StatusType == "read replication");

                bool? writer = writers.TryGetValue(db.DBInstanceIdentifier, out var w) ? w : null;
                result.Instances.Add(new RdsInstanceSnapshot
                {
                    Identifier = db.DBInstanceIdentifier,
                    Arn = db.DBInstanceArn,
                    ClusterIdentifier = db.DBClusterIdentifier,
                    Engine = db.Engine ?? "",
                    EngineVersion = db.EngineVersion,
                    InstanceClass = db.DBInstanceClass ?? "",
                    Status = db.DBInstanceStatus ?? "",
                    Address = db.Endpoint?.Address,
                    Port = db.Endpoint?.Port ?? db.DbInstancePort,
                    MultiAz = db.MultiAZ == true,
                    AvailabilityZone = db.AvailabilityZone,
                    SecondaryAvailabilityZone = db.SecondaryAvailabilityZone,
                    AllocatedStorageGb = db.AllocatedStorage,
                    MaxAllocatedStorageGb = db.MaxAllocatedStorage,
                    StorageType = db.StorageType,
                    PubliclyAccessible = db.PubliclyAccessible == true,
                    PerformanceInsights = db.PerformanceInsightsEnabled == true,
                    Created = db.InstanceCreateTime,
                    ReplicaSource = db.ReadReplicaSourceDBInstanceIdentifier ?? db.ReadReplicaSourceDBClusterIdentifier,
                    Replicas = (db.ReadReplicaDBInstanceIdentifiers ?? []).Concat(db.ReadReplicaDBClusterIdentifiers ?? []).ToList(),
                    IsClusterWriter = writer,
                    ReplicationState = replication is null ? null : replication.Message is { Length: > 0 } m ? $"{replication.Status}: {m}" : replication.Status,
                    ReplicationNormal = replication?.Normal != false,
                    ParameterGroup = group?.DBParameterGroupName,
                    ParameterApplyStatus = group?.ParameterApplyStatus,
                    PendingChanges = PendingChanges(db.PendingModifiedValues),
                    LogExports = db.EnabledCloudwatchLogsExports ?? [],
                    MaxConnections = maxConnections,
                    MaxConnectionsSource = maxSource,
                    NewEvents = EventsOf(db.DBInstanceIdentifier),
                });
            }

            foreach (var cl in clusters.Where(cl => cl.DBClusterIdentifier is not null))
            {
                result.Clusters.Add(new RdsClusterSnapshot
                {
                    Identifier = cl.DBClusterIdentifier,
                    Arn = cl.DBClusterArn,
                    Engine = cl.Engine ?? "",
                    EngineVersion = cl.EngineVersion,
                    EngineMode = cl.EngineMode,
                    Status = cl.Status ?? "",
                    WriterEndpoint = cl.Endpoint,
                    ReaderEndpoint = cl.ReaderEndpoint,
                    Port = cl.Port,
                    CustomEndpoints = cl.CustomEndpoints ?? [],
                    Members = (cl.DBClusterMembers ?? []).Where(m => m.DBInstanceIdentifier is not null)
                        .Select(m => new RdsClusterMember(m.DBInstanceIdentifier, m.IsClusterWriter == true)).ToList(),
                    MultiAz = cl.MultiAZ == true,
                    ReplicationSource = cl.ReplicationSourceIdentifier,
                    ReadReplicas = cl.ReadReplicaIdentifiers ?? [],
                    GlobalCluster = cl.GlobalClusterIdentifier,
                    ServerlessMinAcu = cl.ServerlessV2ScalingConfiguration?.MinCapacity,
                    ServerlessMaxAcu = cl.ServerlessV2ScalingConfiguration?.MaxCapacity,
                    NewEvents = EventsOf(cl.DBClusterIdentifier),
                });
            }
            return result;
        });

    private static List<string> PendingChanges(Rds.PendingModifiedValues? p)
    {
        var list = new List<string>();
        if (p is null)
            return list;
        if (p.DBInstanceClass is { } cls) list.Add($"class → {cls}");
        if (p.AllocatedStorage is { } gb) list.Add($"storage → {gb} GiB");
        if (p.EngineVersion is { } v) list.Add($"engine → {v}");
        if (p.MultiAZ is { } multiAz) list.Add($"Multi-AZ → {(multiAz ? "on" : "off")}");
        if (p.StorageType is { } st) list.Add($"storage type → {st}");
        if (p.Iops is { } iops) list.Add($"IOPS → {iops}");
        if (p.CACertificateIdentifier is { } ca) list.Add($"CA → {ca}");
        return list;
    }

    private static async Task<List<ServiceEvent>> GetRdsEventsAsync(AwsClientSet c, DateTime since, string? onlyInstance, CancellationToken ct)
    {
        var events = new List<ServiceEvent>();
        try
        {
            string? marker = null;
            var pages = 0;
            do
            {
                var resp = await c.Rds.DescribeEventsAsync(new Rds.DescribeEventsRequest
                {
                    StartTime = since,
                    SourceIdentifier = onlyInstance,
                    SourceType = onlyInstance is null ? null : Amazon.RDS.SourceType.DbInstance,
                    MaxRecords = 100,
                    Marker = marker,
                }, ct);
                events.AddRange((resp.Events ?? []).Select(e => new ServiceEvent
                {
                    Date = e.Date?.ToUniversalTime() ?? DateTime.UtcNow,
                    Source = e.SourceIdentifier ?? "",
                    Category = string.Join(", ", e.EventCategories ?? []),
                    Message = e.Message ?? "",
                }));
                marker = resp.Marker;
            } while (!string.IsNullOrEmpty(marker) && ++pages < 5);
        }
        catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
        {
            // Events are optional context.
        }
        return events;
    }

    /// <summary>The parameter group's own max_connections (or Oracle's processes); null when it keeps the engine default.</summary>
    private async Task<string?> GetMaxConnectionsParameterAsync(AwsClientSet c, Target target, string group, string engine, CancellationToken ct)
    {
        // Default groups (default.postgres16, …) always use the engine default.
        if (group.StartsWith("default.", StringComparison.OrdinalIgnoreCase))
            return null;
        var key = (target.ProfileName, target.Region, group);
        if (_maxConnections.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ParameterCacheTtl)
            return cached.Value;

        var parameterName = engine.StartsWith("oracle", StringComparison.OrdinalIgnoreCase) ? "processes" : "max_connections";
        string? value = null;
        try
        {
            string? marker = null;
            var pages = 0;
            do
            {
                // Source=user: only parameters changed in this group, a short list.
                var resp = await c.Rds.DescribeDBParametersAsync(new Rds.DescribeDBParametersRequest { DBParameterGroupName = group, Source = "user", Marker = marker }, ct);
                value = (resp.Parameters ?? []).FirstOrDefault(p => p.ParameterName == parameterName)?.ParameterValue ?? value;
                marker = resp.Marker;
            } while (value is null && !string.IsNullOrEmpty(marker) && ++pages < 10);
        }
        catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
        {
            // Not allowed to read parameters: fall back to the engine default estimate.
        }
        _maxConnections[key] = (DateTime.UtcNow, value);
        return value;
    }

    public Task<IReadOnlyList<LogSource>> GetRdsLogSourcesAsync(Target target, RdsInstanceSnapshot db, CancellationToken ct) =>
        Call<IReadOnlyList<LogSource>>(target, "DescribeDBLogFiles", async c =>
        {
            var sources = new List<LogSource>();

            // Logs exported to CloudWatch Logs: searchable with filter patterns and cheap to tail.
            var prefixes = new List<(string Prefix, string? Streams)> { ($"/aws/rds/instance/{db.Identifier}/", null) };
            if (db.ClusterIdentifier is { } cluster)
                prefixes.Add(($"/aws/rds/cluster/{cluster}/", db.Identifier));
            foreach (var (prefix, streams) in prefixes)
            {
                try
                {
                    var resp = await c.Logs.DescribeLogGroupsAsync(new Logs.DescribeLogGroupsRequest { LogGroupNamePrefix = prefix }, ct);
                    foreach (var g in resp.LogGroups ?? [])
                        if (g.LogGroupName is { } name)
                            sources.Add(new LogSource($"CloudWatch · {name[prefix.Length..]}{(streams is null ? "" : " (cluster log, this instance)")}", name, streams));
                }
                catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
                {
                    // Not allowed to list log groups: the log files below still work.
                }
            }

            var files = new List<Rds.DescribeDBLogFilesDetails>();
            string? marker = null;
            var pages = 0;
            do
            {
                var resp = await c.Rds.DescribeDBLogFilesAsync(new Rds.DescribeDBLogFilesRequest { DBInstanceIdentifier = db.Identifier, Marker = marker }, ct);
                files.AddRange(resp.DescribeDBLogFiles ?? []);
                marker = resp.Marker;
            } while (!string.IsNullOrEmpty(marker) && ++pages < 20);

            foreach (var f in files.Where(f => f.LogFileName is not null).OrderByDescending(f => f.LastWritten ?? 0))
            {
                var written = f.LastWritten is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
                sources.Add(new LogSource($"File · {f.LogFileName} · {RdsInstanceStatus.FormatBytes(f.Size)} · {written?.ToLocalTime():g}", "", null)
                {
                    DbInstance = db.Identifier,
                    DbLogFile = f.LogFileName,
                    LastWritten = written,
                    Size = f.Size,
                });
            }
            return sources;
        });

    public Task<RdsLogPortion> DownloadRdsLogAsync(Target target, string instanceId, string fileName, string? marker, int? lines, CancellationToken ct) =>
        Call(target, "DownloadDBLogFilePortion", async c =>
        {
            var resp = await c.Rds.DownloadDBLogFilePortionAsync(new Rds.DownloadDBLogFilePortionRequest
            {
                DBInstanceIdentifier = instanceId,
                LogFileName = fileName,
                Marker = marker,
                NumberOfLines = lines,
            }, ct);
            return new RdsLogPortion(resp.LogFileData ?? "", resp.Marker, resp.AdditionalDataPending == true);
        });

    // ---------------- ElastiCache ----------------

    public Task<IReadOnlyList<CacheSnapshot>> GetCachesAsync(Target target, DateTime eventsSince, CancellationToken ct, CacheSnapshot? only = null) =>
        Call<IReadOnlyList<CacheSnapshot>>(target, "DescribeReplicationGroups", async c =>
        {
            var groups = new List<ElastiCache.ReplicationGroup>();
            string? marker = null;
            var pages = 0;
            if (only is null or { Kind: CacheKind.ReplicationGroup })
            {
                do
                {
                    var resp = await c.ElastiCache.DescribeReplicationGroupsAsync(new ElastiCache.DescribeReplicationGroupsRequest { ReplicationGroupId = only?.Id, Marker = marker }, ct);
                    groups.AddRange(resp.ReplicationGroups ?? []);
                    marker = resp.Marker;
                } while (!string.IsNullOrEmpty(marker) && ++pages < MaxPages);
            }

            // Node details (status, endpoint, engine version) live on the cache clusters.
            var clusters = new List<ElastiCache.CacheCluster>();
            if (only is null)
            {
                marker = null;
                pages = 0;
                do
                {
                    var resp = await c.ElastiCache.DescribeCacheClustersAsync(new ElastiCache.DescribeCacheClustersRequest { ShowCacheNodeInfo = true, Marker = marker }, ct);
                    clusters.AddRange(resp.CacheClusters ?? []);
                    marker = resp.Marker;
                } while (!string.IsNullOrEmpty(marker) && ++pages < MaxPages);
            }
            else if (only.Kind != CacheKind.Serverless)
            {
                var ids = only.Kind == CacheKind.Cluster ? [only.Id] : groups.SelectMany(g => g.MemberClusters ?? []).Distinct().ToList();
                foreach (var id in ids)
                {
                    var resp = await c.ElastiCache.DescribeCacheClustersAsync(new ElastiCache.DescribeCacheClustersRequest { CacheClusterId = id, ShowCacheNodeInfo = true }, ct);
                    clusters.AddRange(resp.CacheClusters ?? []);
                }
            }

            var serverless = new List<ElastiCache.ServerlessCache>();
            if (only is null or { Kind: CacheKind.Serverless })
            {
                try
                {
                    string? token = null;
                    pages = 0;
                    do
                    {
                        var resp = await c.ElastiCache.DescribeServerlessCachesAsync(new ElastiCache.DescribeServerlessCachesRequest { ServerlessCacheName = only?.Id, NextToken = token }, ct);
                        serverless.AddRange(resp.ServerlessCaches ?? []);
                        token = resp.NextToken;
                    } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
                }
                catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode) && only is null)
                {
                    // Serverless is not available everywhere; provisioned caches are still listed.
                }
            }

            var events = await GetCacheEventsAsync(c, eventsSince, ct);
            var byId = clusters.Where(cl => cl.CacheClusterId is not null).GroupBy(cl => cl.CacheClusterId).ToDictionary(g => g.Key, g => g.First());
            var result = new List<CacheSnapshot>();

            foreach (var g in groups.Where(g => g.ReplicationGroupId is not null))
            {
                var members = (g.MemberClusters ?? []).Select(id => byId.GetValueOrDefault(id)).Where(cl => cl is not null).ToList();
                var first = members.FirstOrDefault();
                var nodeGroups = g.NodeGroups ?? [];
                var clusterMode = g.ClusterEnabled == true;
                var sources = (g.MemberClusters ?? []).Append(g.ReplicationGroupId).ToHashSet();
                result.Add(new CacheSnapshot
                {
                    Id = g.ReplicationGroupId,
                    Kind = CacheKind.ReplicationGroup,
                    Arn = g.ARN,
                    Description = g.Description,
                    Engine = g.Engine ?? first?.Engine ?? "redis",
                    EngineVersion = first?.EngineVersion,
                    NodeType = g.CacheNodeType ?? first?.CacheNodeType,
                    Status = g.Status ?? "",
                    ClusterMode = clusterMode,
                    PrimaryEndpoint = clusterMode ? null : FormatEndpoint(nodeGroups.FirstOrDefault()?.PrimaryEndpoint),
                    ReaderEndpoint = clusterMode ? null : FormatEndpoint(nodeGroups.FirstOrDefault()?.ReaderEndpoint),
                    ConfigurationEndpoint = FormatEndpoint(g.ConfigurationEndpoint),
                    AutomaticFailover = g.AutomaticFailover?.Value,
                    MultiAz = g.MultiAZ?.Value,
                    TransitEncryption = g.TransitEncryptionEnabled == true,
                    AtRestEncryption = g.AtRestEncryptionEnabled == true,
                    AuthToken = g.AuthTokenEnabled == true,
                    Shards = nodeGroups.Select(ng => new CacheShard
                    {
                        Id = ng.NodeGroupId ?? "",
                        Status = ng.Status ?? "",
                        Slots = ng.Slots,
                        Nodes = (ng.NodeGroupMembers ?? []).Select(m =>
                        {
                            var cl = m.CacheClusterId is null ? null : byId.GetValueOrDefault(m.CacheClusterId);
                            var node = cl?.CacheNodes?.FirstOrDefault(n => n.CacheNodeId == m.CacheNodeId) ?? cl?.CacheNodes?.FirstOrDefault();
                            return new CacheNode
                            {
                                ClusterId = m.CacheClusterId ?? "",
                                NodeId = m.CacheNodeId ?? "0001",
                                Role = m.CurrentRole,
                                Status = node?.CacheNodeStatus ?? cl?.CacheClusterStatus ?? "",
                                AvailabilityZone = m.PreferredAvailabilityZone ?? node?.CustomerAvailabilityZone,
                                Address = m.ReadEndpoint?.Address ?? node?.Endpoint?.Address,
                                Port = m.ReadEndpoint?.Port ?? node?.Endpoint?.Port,
                                Created = node?.CacheNodeCreateTime,
                            };
                        }).ToList(),
                    }).ToList(),
                    NewEvents = events.Where(e => sources.Contains(e.Source)).ToList(),
                });
            }

            foreach (var cl in clusters.Where(cl => cl.ReplicationGroupId is null && cl.CacheClusterId is not null))
            {
                var nodes = (cl.CacheNodes ?? []).Select(n => new CacheNode
                {
                    ClusterId = cl.CacheClusterId,
                    NodeId = n.CacheNodeId ?? "0001",
                    Status = n.CacheNodeStatus ?? cl.CacheClusterStatus ?? "",
                    AvailabilityZone = n.CustomerAvailabilityZone ?? cl.PreferredAvailabilityZone,
                    Address = n.Endpoint?.Address,
                    Port = n.Endpoint?.Port,
                    Created = n.CacheNodeCreateTime,
                }).ToList();
                result.Add(new CacheSnapshot
                {
                    Id = cl.CacheClusterId,
                    Kind = CacheKind.Cluster,
                    Arn = cl.ARN,
                    Engine = cl.Engine ?? "",
                    EngineVersion = cl.EngineVersion,
                    NodeType = cl.CacheNodeType,
                    Status = cl.CacheClusterStatus ?? "",
                    PrimaryEndpoint = cl.Engine == "memcached" ? null : nodes.FirstOrDefault()?.Endpoint,
                    ConfigurationEndpoint = FormatEndpoint(cl.ConfigurationEndpoint),
                    TransitEncryption = cl.TransitEncryptionEnabled == true,
                    AtRestEncryption = cl.AtRestEncryptionEnabled == true,
                    AuthToken = cl.AuthTokenEnabled == true,
                    Shards = [new CacheShard { Id = cl.CacheClusterId, Status = cl.CacheClusterStatus ?? "", Nodes = nodes }],
                    NewEvents = events.Where(e => e.Source == cl.CacheClusterId).ToList(),
                });
            }

            foreach (var sc in serverless.Where(sc => sc.ServerlessCacheName is not null))
            {
                result.Add(new CacheSnapshot
                {
                    Id = sc.ServerlessCacheName,
                    Kind = CacheKind.Serverless,
                    Arn = sc.ARN,
                    Description = sc.Description,
                    Engine = sc.Engine ?? "",
                    EngineVersion = sc.FullEngineVersion ?? sc.MajorEngineVersion,
                    NodeType = "serverless",
                    Status = (sc.Status ?? "").ToLowerInvariant(),
                    PrimaryEndpoint = FormatEndpoint(sc.Endpoint),
                    ReaderEndpoint = FormatEndpoint(sc.ReaderEndpoint),
                    TransitEncryption = true,
                    AtRestEncryption = true,
                    NewEvents = events.Where(e => e.Source == sc.ServerlessCacheName).ToList(),
                });
            }
            return result;
        });

    private static string? FormatEndpoint(ElastiCache.Endpoint? e) =>
        e?.Address is { } address ? e.Port is { } port ? $"{address}:{port}" : address : null;

    private static async Task<List<ServiceEvent>> GetCacheEventsAsync(AwsClientSet c, DateTime since, CancellationToken ct)
    {
        var events = new List<ServiceEvent>();
        try
        {
            string? marker = null;
            var pages = 0;
            do
            {
                var resp = await c.ElastiCache.DescribeEventsAsync(new ElastiCache.DescribeEventsRequest { StartTime = since, MaxRecords = 100, Marker = marker }, ct);
                events.AddRange((resp.Events ?? []).Select(e => new ServiceEvent
                {
                    Date = e.Date?.ToUniversalTime() ?? DateTime.UtcNow,
                    Source = e.SourceIdentifier ?? "",
                    Category = e.SourceType?.Value ?? "",
                    Message = e.Message ?? "",
                }));
                marker = resp.Marker;
            } while (!string.IsNullOrEmpty(marker) && ++pages < 5);
        }
        catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
        {
            // Events are optional context.
        }
        return events;
    }
}
