using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

/// <summary>An RDS or ElastiCache event (maintenance, failover, failure, low storage, …).</summary>
public sealed class ServiceEvent
{
    public DateTime Date { get; init; }
    public string Source { get; init; } = "";
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";

    [JsonIgnore] public bool IsFailure => Category.Contains("failure", StringComparison.OrdinalIgnoreCase)
                                          || Category.Contains("low storage", StringComparison.OrdinalIgnoreCase)
                                          || Message.Contains("failed", StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string Text => $"{Date.ToLocalTime():g}  {(Category.Length > 0 ? $"[{Category}] " : "")}{Message}";
}

// ---------------- RDS ----------------

public sealed class RdsInstanceSnapshot
{
    public string Identifier { get; init; } = "";
    public string? Arn { get; init; }
    public string? ClusterIdentifier { get; init; }
    public string Engine { get; init; } = "";
    public string? EngineVersion { get; init; }
    public string InstanceClass { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Address { get; init; }
    public int? Port { get; init; }
    public bool MultiAz { get; init; }
    public string? AvailabilityZone { get; init; }
    public string? SecondaryAvailabilityZone { get; init; }
    public int? AllocatedStorageGb { get; init; }
    public int? MaxAllocatedStorageGb { get; init; }
    public string? StorageType { get; init; }
    /// <summary>Provisioned IOPS and gp3 throughput (MiB/s); for gp3 the baseline is included in the storage price.</summary>
    public int? Iops { get; init; }
    public int? StorageThroughputMbps { get; init; }
    /// <summary>Storage type of the Aurora or Multi-AZ DB cluster, e.g. "aurora-iopt1" for I/O-Optimized.</summary>
    public string? ClusterStorageType { get; init; }
    /// <summary>"open-source-rds-extended-support" (default: billed Extended Support after standard support ends) or "…-disabled".</summary>
    public string? EngineLifecycleSupport { get; init; }
    public bool PubliclyAccessible { get; init; }
    public bool PerformanceInsights { get; init; }
    public DateTime? Created { get; init; }

    /// <summary>Source of a read replica (instance id, or an ARN when it lives in another region/cluster).</summary>
    public string? ReplicaSource { get; init; }
    /// <summary>Read replicas of this instance (instance ids, ARNs for cross-region, or cluster ids).</summary>
    public List<string> Replicas { get; init; } = [];
    /// <summary>Only for cluster members: writer or reader.</summary>
    public bool? IsClusterWriter { get; init; }
    /// <summary>Read replication state from StatusInfos, e.g. "replicating" or "error: …".</summary>
    public string? ReplicationState { get; init; }
    public bool ReplicationNormal { get; init; } = true;

    public string? ParameterGroup { get; init; }
    /// <summary>e.g. "pending-reboot" when parameter changes wait for a reboot.</summary>
    public string? ParameterApplyStatus { get; init; }
    public List<string> PendingChanges { get; init; } = [];
    public List<string> LogExports { get; init; } = [];

    public int? MaxConnections { get; init; }
    /// <summary>Where <see cref="MaxConnections"/> comes from, e.g. "parameter group" or "≈ engine default for 8 GiB".</summary>
    public string? MaxConnectionsSource { get; init; }

    public List<ServiceEvent> NewEvents { get; init; } = [];

    [JsonIgnore] public bool IsAurora => Engine.StartsWith("aurora", StringComparison.OrdinalIgnoreCase);
    /// <summary>A member of a Multi-AZ DB cluster (one writer, two readable standbys; not Aurora).</summary>
    [JsonIgnore] public bool IsMultiAzClusterMember => ClusterIdentifier is not null && !IsAurora;
    [JsonIgnore] public bool IsIoOptimized => IsAurora && (StorageType == "aurora-iopt1" || ClusterStorageType == "aurora-iopt1");
    [JsonIgnore] public bool IsReplica => ReplicaSource is not null || IsClusterWriter == false;
    [JsonIgnore] public string Endpoint => Address is null ? "(no endpoint yet)" : Port is { } p ? $"{Address}:{p}" : Address;
    [JsonIgnore] public string EngineText => $"{Engine} {EngineVersion}".Trim();
    [JsonIgnore]
    public string AvailabilityText => string.Join(" · ", new[]
    {
        AvailabilityZone,
        MultiAz ? $"Multi-AZ (standby in {SecondaryAvailabilityZone ?? "another zone"})" : "single-AZ",
        PubliclyAccessible ? "publicly accessible" : "private",
    }.Where(s => s is not null));
    [JsonIgnore]
    public string Role => IsClusterWriter switch
    {
        true => "writer",
        false => "reader",
        null => ReplicaSource is not null ? "read replica" : Replicas.Count > 0 ? "primary" : "instance",
    };
}

public sealed record RdsClusterMember(string InstanceId, bool IsWriter);

public sealed class RdsClusterSnapshot
{
    public string Identifier { get; init; } = "";
    public string? Arn { get; init; }
    public string Engine { get; init; } = "";
    public string? EngineVersion { get; init; }
    public string? EngineMode { get; init; }
    public string Status { get; init; } = "";
    public string? WriterEndpoint { get; init; }
    public string? ReaderEndpoint { get; init; }
    public int? Port { get; init; }
    public List<string> CustomEndpoints { get; init; } = [];
    public List<RdsClusterMember> Members { get; init; } = [];
    public bool MultiAz { get; init; }
    public string? ReplicationSource { get; init; }
    public List<string> ReadReplicas { get; init; } = [];
    public string? GlobalCluster { get; init; }
    public double? ServerlessMinAcu { get; init; }
    public double? ServerlessMaxAcu { get; init; }
    public List<ServiceEvent> NewEvents { get; init; } = [];

    [JsonIgnore] public string EngineText => $"{Engine} {EngineVersion}".Trim();
}

public sealed class RdsInventory
{
    public List<RdsInstanceSnapshot> Instances { get; init; } = [];
    public List<RdsClusterSnapshot> Clusters { get; init; } = [];
}

public sealed class RdsInstanceStatus : ResourceStatus
{
    public RdsInstanceSnapshot Snapshot { get; init; } = new();
    public List<ServiceEvent> RecentEvents { get; set; } = [];

    /// <summary>DatabaseConnections (count). <see cref="ResourceStatus.Memory"/> holds the same as % of max_connections when known.</summary>
    public MetricEvaluation? Connections { get; set; }
    /// <summary>Used storage in % of allocated (non-Aurora only).</summary>
    public MetricEvaluation? StorageUsed { get; set; }
    public double? FreeStorageBytes { get; set; }
    public double? FreeableMemoryBytes { get; set; }
    public double? ReplicaLagSeconds { get; set; }

    public override string ResourceKey => ResourceKeys.Rds(TargetId, Snapshot.Identifier);
    public override string DisplayName => Snapshot.Identifier;
    public override string MemoryLabel => "Connections";
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/rds/home?region={Region}#database:id={Uri.EscapeDataString(Snapshot.Identifier)};is-cluster=false";

    [JsonIgnore]
    public string ConnectionsText => Connections?.Current is not { } c ? "n/a"
        : Snapshot.MaxConnections is { } max ? $"{c:0} of {max:N0} ({c / max * 100:0}%) · peak {Connections.Peak:0}" : $"{c:0} · peak {Connections.Peak:0} (max unknown)";
    [JsonIgnore] public double? AllocatedBytes => Snapshot.AllocatedStorageGb is { } gb ? gb * (double)(1L << 30) : null;
    [JsonIgnore] public double? UsedStorageBytes => AllocatedBytes is { } total && FreeStorageBytes is { } free ? Math.Max(0, total - free) : null;
    [JsonIgnore] public string SizeText => Snapshot.IsAurora ? "Aurora" : FormatBytes(AllocatedBytes);
    [JsonIgnore]
    public string StorageText => Snapshot.IsAurora ? "Aurora (grows automatically)"
        : StorageUsed?.Current is { } used ? $"{used:0}% used · {FormatBytes(UsedStorageBytes)} of {FormatBytes(AllocatedBytes)} · {FormatBytes(FreeStorageBytes)} free"
        : AllocatedBytes is { } total ? $"{FormatBytes(total)} allocated" : "n/a";
    /// <summary>Storage autoscaling limit, when set above the allocated size.</summary>
    [JsonIgnore]
    public string? StorageAutoscaling => Snapshot.MaxAllocatedStorageGb is { } max && max > (Snapshot.AllocatedStorageGb ?? 0)
        ? $"autoscaling up to {FormatBytes(max * (double)(1L << 30))}{(Snapshot.StorageType is { } type ? $" · {type}" : "")}"
        : Snapshot.StorageType is { } t ? $"{t} · no storage autoscaling" : null;
    [JsonIgnore] public string FreeableMemoryText => FreeableMemoryBytes is null ? "n/a" : $"{FormatBytes(FreeableMemoryBytes)} free";
    [JsonIgnore] public string? ReplicaLagText => ReplicaLagSeconds is { } s ? $"{s:0.#} s" : null;

    public static string FormatBytes(double? bytes) => bytes switch
    {
        null => "n/a",
        >= 1L << 40 => $"{bytes / (1L << 40):0.#} TiB",
        >= 1 << 30 => $"{bytes / (1 << 30):0.#} GiB",
        >= 1 << 20 => $"{bytes / (1 << 20):0} MiB",
        _ => $"{bytes:0} B",
    };
}

public sealed class RdsClusterStatus : ResourceStatus
{
    public RdsClusterSnapshot Snapshot { get; init; } = new();
    public List<ServiceEvent> RecentEvents { get; set; } = [];

    public override string ResourceKey => ResourceKeys.RdsCluster(TargetId, Snapshot.Identifier);
    public override string DisplayName => Snapshot.Identifier;
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/rds/home?region={Region}#database:id={Uri.EscapeDataString(Snapshot.Identifier)};is-cluster=true";
}

/// <summary>An RDS log file (DescribeDBLogFiles) or a chunk of it.</summary>
public sealed record RdsLogPortion(string Data, string? Marker, bool AdditionalDataPending);

// ---------------- ElastiCache ----------------

public enum CacheKind
{
    /// <summary>Redis/Valkey replication group (primary + replicas, optionally sharded).</summary>
    ReplicationGroup,
    /// <summary>Cache cluster outside a replication group (single Redis node or Memcached).</summary>
    Cluster,
    Serverless,
}

public sealed class CacheNode
{
    public string ClusterId { get; init; } = "";
    public string NodeId { get; init; } = "0001";
    /// <summary>primary / replica (replication groups), or null.</summary>
    public string? Role { get; init; }
    public string Status { get; init; } = "";
    public string? AvailabilityZone { get; init; }
    public string? Address { get; init; }
    public int? Port { get; init; }
    public DateTime? Created { get; init; }

    [JsonIgnore] public string Endpoint => Address is null ? "" : Port is { } p ? $"{Address}:{p}" : Address;
}

public sealed class CacheShard
{
    public string Id { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Slots { get; init; }
    public List<CacheNode> Nodes { get; init; } = [];
}

public sealed class CacheSnapshot
{
    public string Id { get; init; } = "";
    public CacheKind Kind { get; init; }
    public string? Arn { get; init; }
    public string? Description { get; init; }
    public string Engine { get; init; } = "";
    public string? EngineVersion { get; init; }
    public string? NodeType { get; init; }
    public string Status { get; init; } = "";
    public bool ClusterMode { get; init; }
    public string? PrimaryEndpoint { get; init; }
    public string? ReaderEndpoint { get; init; }
    public string? ConfigurationEndpoint { get; init; }
    public string? AutomaticFailover { get; init; }
    public string? MultiAz { get; init; }
    public bool TransitEncryption { get; init; }
    public bool AtRestEncryption { get; init; }
    public bool AuthToken { get; init; }
    public List<CacheShard> Shards { get; init; } = [];
    public List<ServiceEvent> NewEvents { get; init; } = [];

    [JsonIgnore] public IEnumerable<CacheNode> Nodes => Shards.SelectMany(s => s.Nodes);
    [JsonIgnore]
    public string KindText => Kind switch
    {
        CacheKind.ReplicationGroup => ClusterMode ? "cluster (sharded)" : "replication group",
        CacheKind.Serverless => "serverless",
        _ => "cluster",
    };
    [JsonIgnore]
    public string SecurityText => string.Join(" · ", new[]
    {
        TransitEncryption ? "TLS in transit" : "no TLS",
        AtRestEncryption ? "encrypted at rest" : "not encrypted at rest",
        AuthToken ? "AUTH token" : null,
        AutomaticFailover is { } failover ? $"failover {failover}" : null,
        MultiAz is { } multiAz ? $"Multi-AZ {multiAz}" : null,
    }.Where(s => s is not null));
    [JsonIgnore] public string EngineText => $"{Engine} {EngineVersion}".Trim();
    /// <summary>The endpoint clients should use.</summary>
    [JsonIgnore] public string? MainEndpoint => ConfigurationEndpoint ?? PrimaryEndpoint ?? Nodes.FirstOrDefault()?.Endpoint;
}

public sealed class CacheNodeMetric
{
    public string ClusterId { get; init; } = "";
    public string NodeId { get; init; } = "";
    public MetricEvaluation? Cpu { get; set; }
    public MetricEvaluation? Memory { get; set; }
    public double? Connections { get; set; }
    public double? Evictions { get; set; }
    public double? HitRate { get; set; }
    public double? ReplicationLagSeconds { get; set; }
}

public sealed class CacheStatus : ResourceStatus
{
    public CacheSnapshot Snapshot { get; init; } = new();
    public List<ServiceEvent> RecentEvents { get; set; } = [];
    public List<CacheNodeMetric> NodeMetrics { get; set; } = [];

    public override string ResourceKey => ResourceKeys.Cache(TargetId, Snapshot.Id);
    public override string DisplayName => Snapshot.Id;
    public override string ConsoleUrl => Snapshot.Kind == CacheKind.Serverless
        ? $"https://{Region}.console.aws.amazon.com/elasticache/home?region={Region}#/serverless/{Uri.EscapeDataString(Snapshot.Id)}"
        : $"https://{Region}.console.aws.amazon.com/elasticache/home?region={Region}#/{(Snapshot.Engine is "memcached" ? "memcached" : Snapshot.Engine is "valkey" ? "valkey" : "redis")}/{Uri.EscapeDataString(Snapshot.Id)}";

    [JsonIgnore] public double? TotalConnections => NodeMetrics.Any(n => n.Connections is not null) ? NodeMetrics.Sum(n => n.Connections ?? 0) : null;
    [JsonIgnore] public double? TotalEvictions => NodeMetrics.Any(n => n.Evictions is not null) ? NodeMetrics.Sum(n => n.Evictions ?? 0) : null;
    [JsonIgnore]
    public double? HitRate => NodeMetrics.Where(n => n.HitRate is not null).Select(n => n.HitRate!.Value).DefaultIfEmpty(double.NaN).Average() is var h && !double.IsNaN(h) ? h : null;

    /// <summary>One card per node with its metrics, for the details panel.</summary>
    [JsonIgnore]
    public IReadOnlyList<CacheNodeView> NodeViews => Snapshot.Shards
        .SelectMany(s => s.Nodes.Select(n => new CacheNodeView(s.Id, n, NodeMetrics.FirstOrDefault(m => m.ClusterId == n.ClusterId && m.NodeId == n.NodeId))))
        .ToList();
}

public sealed record CacheNodeView(string Shard, CacheNode Node, CacheNodeMetric? Metric)
{
    public string Title => $"{Node.ClusterId} · {Node.Role ?? "node"} · {Node.Status}{(Node.AvailabilityZone is null ? "" : $" · {Node.AvailabilityZone}")}";
    public string Detail => string.Join(" · ", new[]
    {
        Metric?.Connections is { } c ? $"{c:0} connections" : null,
        Metric?.HitRate is { } h ? $"hit rate {h:0.#}%" : null,
        Metric?.Evictions is { } e ? $"{e:0} evictions/h" : null,
        Metric?.ReplicationLagSeconds is { } l ? $"replication lag {l:0.#} s" : null,
    }.Where(s => s is not null));
    public HealthLevel Level => Node.Status is "available"
        ? (HealthLevel)Math.Max((int)(Metric?.Cpu?.Level ?? HealthLevel.Ok), (int)(Metric?.Memory?.Level ?? HealthLevel.Ok))
        : HealthLevel.Warn;
}

// ---------------- Elastic Beanstalk application versions ----------------

public sealed class EbApplicationVersion
{
    public string Label { get; init; } = "";
    public string? Description { get; init; }
    public DateTime? Created { get; init; }
    public string? Status { get; init; }
    public string? Source { get; init; }
}
