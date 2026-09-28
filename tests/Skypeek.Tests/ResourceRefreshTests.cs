using Skypeek.Core;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Tests;

public class ResourceRefreshTests
{
    private sealed class MemoryStores : ISettingsStore, IHealthStore
    {
        public List<Target> Targets { get; } = [new Target { Id = 1, ProfileName = "p", Region = "us-east-1", Alias = "QA", EbEnabled = false }];
        public AppSettings LoadSettings() => new();
        public void SaveSettings(AppSettings settings) { }
        public IReadOnlyList<Target> LoadTargets() => Targets;
        public Target SaveTarget(Target target) => target;
        public void DeleteTarget(long id) { }
        public void Save(long targetId, TargetHealth health) { }
        public IReadOnlyList<TargetHealth> LoadAll() => [];
    }

    private sealed class Quiet : INotifier
    {
        public void Notify(string title, string message, string? detail = null) { }
    }

    /// <summary>Only the calls a single refresh makes; anything else would mean it asked for too much.</summary>
    private sealed class FakeGateway : IAwsGateway
    {
        public int Running { get; set; } = 2;
        public string DbStatus { get; set; } = "available";
        public double FreeStorageGiB { get; set; } = 25;
        /// <summary>One point by default; more (one per minute) to exceed the sustained-minutes window.</summary>
        public int PointsPerMetric { get; set; } = 1;
        public List<string> Calls { get; } = [];
        public List<MetricQuery> LastQueries { get; } = [];

        public Task<RdsInventory> GetRdsAsync(Target target, DateTime eventsSince, CancellationToken ct, string? onlyInstance = null, string? onlyCluster = null)
        {
            Calls.Add($"rds:{onlyInstance}:{onlyCluster}");
            var inventory = new RdsInventory();
            inventory.Instances.Add(new RdsInstanceSnapshot
            {
                Identifier = "orders-db", Engine = "postgres", InstanceClass = "db.m5.large", Status = DbStatus,
                AllocatedStorageGb = 100, MaxConnections = 200, Address = "orders-db.abc.us-east-1.rds.amazonaws.com", Port = 5432,
            });
            return Task.FromResult(inventory);
        }

        public Task<IReadOnlyList<CacheSnapshot>> GetCachesAsync(Target target, DateTime eventsSince, CancellationToken ct, CacheSnapshot? only = null)
        {
            Calls.Add($"cache:{only?.Id}");
            return Task.FromResult<IReadOnlyList<CacheSnapshot>>([]);
        }

        public Task<IReadOnlyList<EcsServiceSnapshot>> GetEcsServicesAsync(Target target, CancellationToken ct, string? onlyClusterArn = null, string? onlyServiceArn = null)
        {
            Calls.Add($"ecs:{onlyClusterArn}:{onlyServiceArn}");
            IReadOnlyList<EcsServiceSnapshot> list =
            [
                new EcsServiceSnapshot { ClusterName = "prod", ClusterArn = "arn:c/prod", ServiceName = "api", ServiceArn = "arn:s/api", Status = "ACTIVE", Desired = 2, Running = Running },
            ];
            return Task.FromResult(list);
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>>> GetMetricDataAsync(Target target, IReadOnlyList<MetricQuery> queries, DateTime start, DateTime end, CancellationToken ct)
        {
            Calls.Add($"metrics:{queries.Count}");
            LastQueries.Clear();
            LastQueries.AddRange(queries);
            // 55 for percentages and connections; 25 GiB free of 100 for storage.
            IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> data = queries.ToDictionary(q => q.Id,
                q => (IReadOnlyList<MetricPoint>)Enumerable.Range(1, PointsPerMetric)
                    .Select(i => new MetricPoint(end.AddMinutes(-i), q.MetricName == "FreeStorageSpace" ? FreeStorageGiB * (1L << 30) : 55))
                    .ToList());
            return Task.FromResult(data);
        }

        public Task<IReadOnlyList<string>> GetStoppedTaskReasonsAsync(Target target, string cluster, string service, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["OutOfMemory"]);

        public Task<IReadOnlyList<CatalogItem>> ListSecretsAsync(Target target, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<CatalogItem>> ListParametersAsync(Target target, CancellationToken ct) => throw new NotSupportedException();
        public Task<CatalogItem?> DescribeSecretAsync(Target target, string secretId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SecretValueResult> GetSecretValueAsync(Target target, string secretId, bool elevated, CancellationToken ct) => throw new NotSupportedException();
        public Task<SecretValueResult> GetParameterValueAsync(Target target, string name, bool decrypt, bool elevated, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EbEnvironmentSnapshot>> GetEbEnvironmentsAsync(Target target, DateTime eventsSince, CancellationToken ct, string? onlyEnvironment = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<MetricDescriptor>> ListMetricsAsync(Target target, string ns, string metricName, CancellationToken ct) => throw new NotSupportedException();
        public Task<AlarmsResult> GetAlarmsAsync(Target target, DateTime historySince, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LogSource>> GetEcsLogSourcesAsync(Target target, EcsServiceSnapshot service, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LogSource>> GetEbLogSourcesAsync(Target target, string environmentName, CancellationToken ct) => throw new NotSupportedException();
        public Task<LogPage> GetLogEventsAsync(Target target, LogSource source, DateTime startUtc, DateTime endUtc, string? filterPattern, string? nextToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EbLogFile>> RequestEbLogsAsync(Target target, string environmentId, string environmentName, bool bundle, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> DownloadEbLogAsync(Target target, EbLogFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task DeployEbVersionAsync(Target target, EbEnvironmentSnapshot env, string versionLabel, CancellationToken ct) => throw new NotSupportedException();
        public Task RestartEbAppServersAsync(Target target, EbEnvironmentSnapshot env, CancellationToken ct) => throw new NotSupportedException();
        public Task RebootEbInstanceAsync(Target target, EbEnvironmentSnapshot env, string instanceId, CancellationToken ct) => throw new NotSupportedException();
        public Task TerminateEbInstanceAsync(Target target, EbEnvironmentSnapshot env, string instanceId, CancellationToken ct) => throw new NotSupportedException();
        public Task ForceNewEcsDeploymentAsync(Target target, EcsServiceSnapshot service, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<EbApplicationVersion>> GetEbApplicationVersionsAsync(Target target, string application, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<LogSource>> GetRdsLogSourcesAsync(Target target, RdsInstanceSnapshot db, CancellationToken ct) => throw new NotSupportedException();
        public Task<RdsLogPortion> DownloadRdsLogAsync(Target target, string instanceId, string fileName, string? marker, int? lines, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Refreshing_one_database_reads_only_that_instance_and_its_metrics()
    {
        var stores = new MemoryStores();
        var settings = new SettingsService(stores);
        var gateway = new FakeGateway();
        var health = new HealthService(gateway, stores, settings, new Quiet());
        var target = stores.Targets[0];
        target.EcsEnabled = false;

        await health.PollHealthAsync(target, CancellationToken.None);
        var before = health.Get(1)!.Rds.Single();
        Assert.Equal(HealthLevel.Ok, before.Level);

        gateway.Calls.Clear();
        gateway.DbStatus = "storage-full";
        await health.RefreshResourceAsync(target, before, CancellationToken.None);

        // CPU, connections, freeable memory, free storage.
        Assert.Equal(["rds:orders-db:", "metrics:4"], gateway.Calls);
        var after = health.Get(1)!.Rds.Single();
        Assert.Equal(HealthLevel.Critical, after.Level);
        Assert.Contains("Status storage-full", after.Reasons);
        Assert.Equal(55, after.Connections?.Current);
        // 55 of max_connections 200 = 27.5%.
        Assert.Equal(27.5, after.Memory!.Current!.Value, precision: 6);
        Assert.Equal(75, after.StorageUsed?.Current);
    }

    [Fact]
    public async Task Per_database_storage_thresholds_override_the_global_ones()
    {
        var stores = new MemoryStores();
        var settings = new SettingsService(stores);
        var gateway = new FakeGateway { FreeStorageGiB = 11, PointsPerMetric = 20 };
        var health = new HealthService(gateway, stores, settings, new Quiet());
        var target = stores.Targets[0];
        target.EcsEnabled = false;
        target.CacheEnabled = false;

        await health.PollHealthAsync(target, CancellationToken.None);
        await health.RefreshResourceAsync(target, health.Get(1)!.Rds.Single(), CancellationToken.None);
        var db = health.Get(1)!.Rds.Single();
        // 89% used for 20 min, over the global 80% warning.
        Assert.Equal(HealthLevel.Warn, db.Level);

        settings.Settings.ResourceThresholds[db.ResourceKey] = new ThresholdSettings(80, 90, 80, 95) { StorageWarn = 92, StorageCritical = 97 };
        health.Reevaluate();
        Assert.Equal(HealthLevel.Ok, health.Get(1)!.Rds.Single().Level);
        Assert.Equal(HealthLevel.Ok, health.Get(1)!.Rds.Single().StorageUsed!.Level);
    }

    [Fact]
    public async Task Usage_analysis_reads_hourly_stats_and_turns_free_memory_into_used_percent()
    {
        var stores = new MemoryStores();
        var settings = new SettingsService(stores);
        var gateway = new FakeGateway { PointsPerMetric = 48 };
        var health = new HealthService(gateway, stores, settings, new Quiet());
        var target = stores.Targets[0];
        target.EcsEnabled = false;
        target.CacheEnabled = false;
        await health.PollHealthAsync(target, CancellationToken.None);
        var db = health.Get(1)!.Rds.Single();

        var report = await health.AnalyzeUsageAsync(target, db, 30, CancellationToken.None);

        // CPU, memory (from FreeableMemory) and connections (max_connections known), each as hourly Average/Minimum/Maximum.
        Assert.Equal(9, gateway.LastQueries.Count);
        Assert.All(gateway.LastQueries, q => Assert.Equal(3600, q.PeriodSeconds));
        Assert.Equal(["CPU", "Memory", "Connections"], report.Series.Select(s => s.Metric));
        var memory = report.Series.Single(s => s.Metric == "Memory");
        // db.m5.large = 8 GiB; the fake reports 55 bytes free, i.e. ~100% used, flagged as an estimate.
        Assert.True(memory.Estimated);
        Assert.Equal(100, memory.Average!.Value, precision: 3);
        Assert.Equal(48, memory.Hours);
        Assert.Equal(Provisioning.Under, report.Verdict);
    }

    [Fact]
    public async Task Refreshing_one_service_reads_only_that_service_and_keeps_the_rest()
    {
        var stores = new MemoryStores();
        var settings = new SettingsService(stores);
        var gateway = new FakeGateway();
        var health = new HealthService(gateway, stores, settings, new Quiet());
        var target = stores.Targets[0];
        target.RdsEnabled = false;
        target.CacheEnabled = false;

        // Start with a full health poll: api is fine (2/2).
        await health.PollHealthAsync(target, CancellationToken.None);
        var before = health.Get(1)!.Ecs.Single();
        Assert.Equal(HealthLevel.Ok, before.Level);

        gateway.Calls.Clear();
        gateway.Running = 0;
        await health.RefreshResourceAsync(target, before, CancellationToken.None);

        Assert.Equal(["ecs:arn:c/prod:arn:s/api", "metrics:2"], gateway.Calls);
        var after = health.Get(1)!.Ecs.Single();
        Assert.Equal(HealthLevel.Critical, after.Level);
        Assert.Contains("OutOfMemory", after.StoppedReasons);
        Assert.Equal(55, after.Cpu?.Current);
        Assert.NotNull(after.RefreshedUtc);
    }
}
