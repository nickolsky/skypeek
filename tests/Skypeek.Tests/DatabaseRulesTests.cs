using Skypeek.Core.Health;
using Skypeek.Core.Models;

namespace Skypeek.Tests;

public class DatabaseRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
    private const double GiB = 1L << 30;

    [Theory]
    [InlineData("db.t3.micro", 1)]
    [InlineData("db.t4g.medium", 4)]
    [InlineData("db.m5.large", 8)]
    [InlineData("db.m6i.2xlarge", 32)]
    [InlineData("db.r6g.large", 16)]
    [InlineData("db.r5.xlarge", 32)]
    [InlineData("db.x2g.large", 32)]
    [InlineData("db.x2iedn.xlarge", 128)]
    public void Instance_class_memory(string instanceClass, double expectedGiB) =>
        Assert.Equal(expectedGiB * GiB, HealthRules.InstanceClassMemoryBytes(instanceClass, null));

    [Fact]
    public void Serverless_memory_uses_the_maximum_capacity() =>
        Assert.Equal(16 * 2 * GiB, HealthRules.InstanceClassMemoryBytes("db.serverless", 16));

    [Theory]
    [InlineData("{DBInstanceClassMemory/12582880}", 8, 682)]
    [InlineData("LEAST({DBInstanceClassMemory/9531392},5000)", 16, 1802)]
    [InlineData("LEAST({DBInstanceClassMemory/9531392},5000)", 512, 5000)]
    [InlineData("GREATEST({log(DBInstanceClassMemory/805306368)*45},{log(DBInstanceClassMemory/8187281408)*1000})", 16, 1069)]
    [InlineData("150", 8, 150)]
    public void Parameter_formulas(string formula, double memoryGiB, double expected) =>
        Assert.Equal(expected, HealthRules.EvaluateFormula(formula, memoryGiB * GiB));

    [Fact]
    public void Unknown_formulas_are_not_guessed() =>
        Assert.Null(HealthRules.EvaluateFormula("{DBInstanceVCPU*2}", 8 * GiB));

    [Fact]
    public void Max_connections_prefers_the_parameter_group()
    {
        Assert.Equal((500, "parameter group"), HealthRules.EstimateMaxConnections("postgres", "db.m5.large", "500", null));

        var (fromDefault, source) = HealthRules.EstimateMaxConnections("postgres", "db.m5.large", null, null);
        Assert.Equal(901, fromDefault);
        Assert.StartsWith("≈ engine default", source);

        // SQL Server has no connection limit formula; nothing is estimated.
        Assert.Equal((null, null), HealthRules.EstimateMaxConnections("sqlserver-se", "db.m5.large", null, null));
    }

    [Theory]
    [InlineData("available", HealthLevel.Ok)]
    [InlineData("backing-up", HealthLevel.Ok)]
    [InlineData("stopped", HealthLevel.Ok)]
    [InlineData("modifying", HealthLevel.Warn)]
    [InlineData("rebooting", HealthLevel.Warn)]
    [InlineData("storage-full", HealthLevel.Critical)]
    [InlineData("incompatible-parameters", HealthLevel.Critical)]
    public void Rds_status_levels(string status, HealthLevel expected) =>
        Assert.Equal(expected, HealthRules.EvaluateRdsStatus(status, [], Now).Level);

    [Fact]
    public void Broken_replication_is_critical()
    {
        var db = new RdsInstanceSnapshot { Identifier = "r1", Status = "available", ReplicaSource = "p1", ReplicationNormal = false, ReplicationState = "error: IO thread stopped" };
        var (level, reasons) = HealthRules.EvaluateRdsInstance(db, [], Now);
        Assert.Equal(HealthLevel.Critical, level);
        Assert.Contains(reasons, r => r.Contains("IO thread stopped"));
    }

    [Fact]
    public void Recent_failure_events_warn_and_old_ones_do_not()
    {
        ServiceEvent Failure(int minutesAgo) => new() { Date = Now.AddMinutes(-minutesAgo), Source = "db", Category = "failure", Message = "The database instance has failed over." };
        Assert.Equal(HealthLevel.Warn, HealthRules.EvaluateRdsStatus("available", [Failure(5)], Now).Level);
        Assert.Equal(HealthLevel.Ok, HealthRules.EvaluateRdsStatus("available", [Failure(120)], Now).Level);
    }

    [Fact]
    public void Cache_nodes_and_shards()
    {
        CacheNode Node(string id, string? role, string status = "available") => new() { ClusterId = id, Role = role, Status = status };

        var healthy = new CacheSnapshot
        {
            Id = "sessions", Kind = CacheKind.ReplicationGroup, Status = "available",
            Shards = [new CacheShard { Id = "0001", Nodes = [Node("sessions-001", "primary"), Node("sessions-002", "replica")] }],
        };
        Assert.Equal(HealthLevel.Ok, HealthRules.EvaluateCache(healthy, [], Now).Level);

        var noPrimary = new CacheSnapshot
        {
            Id = "sessions", Kind = CacheKind.ReplicationGroup, Status = "available",
            Shards = [new CacheShard { Id = "0001", Nodes = [Node("sessions-001", "replica"), Node("sessions-002", "replica")] }],
        };
        Assert.Equal(HealthLevel.Critical, HealthRules.EvaluateCache(noPrimary, [], Now).Level);

        // Cluster mode does not report roles; that is not a missing primary.
        var clusterMode = new CacheSnapshot
        {
            Id = "big", Kind = CacheKind.ReplicationGroup, Status = "available", ClusterMode = true,
            Shards = [new CacheShard { Id = "0001", Nodes = [Node("big-0001-001", null), Node("big-0001-002", null)] }],
        };
        Assert.Equal(HealthLevel.Ok, HealthRules.EvaluateCache(clusterMode, [], Now).Level);

        var rebooting = new CacheSnapshot
        {
            Id = "c", Kind = CacheKind.Cluster, Status = "available",
            Shards = [new CacheShard { Id = "c", Nodes = [Node("c", null, "rebooting")] }],
        };
        Assert.Equal(HealthLevel.Warn, HealthRules.EvaluateCache(rebooting, [], Now).Level);
    }

    [Fact]
    public void Alarms_match_databases_and_caches_by_dimension()
    {
        var dbAlarm = new AlarmInfo { Name = "orders-cpu", Namespace = "AWS/RDS", MetricName = "CPUUtilization", Dimensions = new() { ["DBInstanceIdentifier"] = "orders-db" } };
        Assert.True(HealthRules.AlarmMatchesRds(dbAlarm, new RdsInstanceSnapshot { Identifier = "orders-db" }));
        Assert.False(HealthRules.AlarmMatchesRds(dbAlarm, new RdsInstanceSnapshot { Identifier = "other" }));

        var storage = new AlarmInfo { Name = "orders-disk", Namespace = "AWS/RDS", MetricName = "FreeStorageSpace" };
        Assert.True(HealthRules.IsRelevantAlarm(storage));

        var cache = new CacheSnapshot { Id = "sessions", Shards = [new CacheShard { Nodes = [new CacheNode { ClusterId = "sessions-002" }] }] };
        var nodeAlarm = new AlarmInfo { Name = "evictions", Namespace = "AWS/ElastiCache", MetricName = "Evictions", Dimensions = new() { ["CacheClusterId"] = "sessions-002" } };
        Assert.True(HealthRules.AlarmMatchesCache(nodeAlarm, cache));
    }
}
