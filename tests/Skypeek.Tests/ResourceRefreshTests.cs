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
        public List<AlarmInfo> Alarms { get; } = [];
        public Task<AlarmsResult> GetAlarmsAsync(Target target, DateTime historySince, CancellationToken ct) => Task.FromResult(new AlarmsResult { Alarms = Alarms });
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

        public Dictionary<string, double> PriceByInstanceType { get; } = new() { ["t3.small"] = 0.0208 };
        public int CostExplorerCalls { get; private set; }

        public Task<IReadOnlyList<PriceItem>> GetPricesAsync(Target target, PriceQuery query, CancellationToken ct)
        {
            Calls.Add($"price:{query.Key}");
            IReadOnlyList<PriceItem> items = query.Filters.TryGetValue("instanceType", out var type) && PriceByInstanceType.TryGetValue(type, out var usd)
                ? [new PriceItem("BoxUsage:" + type, "Hrs", usd, "on demand", new Dictionary<string, string>())]
                : [];
            return Task.FromResult(items);
        }

        public Task<IReadOnlyDictionary<string, DateTime>> GetRdsExtendedSupportStartsAsync(Target target, string engine, CancellationToken ct)
        {
            Calls.Add($"support:{engine}");
            return Task.FromResult<IReadOnlyDictionary<string, DateTime>>(new Dictionary<string, DateTime>());
        }

        public Task<ActualCosts> GetActualCostsAsync(Target target, CancellationToken ct)
        {
            CostExplorerCalls++;
            return Task.FromResult(new ActualCosts { FetchedUtc = DateTime.UtcNow, MonthToDate = 12.5, LastMonth = 40, ResourceLast14Days = new() { ["i-web"] = 7 } });
        }

        public List<Ec2InstanceSnapshot> Instances { get; } = [];
        public List<LoadBalancerSnapshot> LoadBalancers { get; } = [];

        public Task<IReadOnlyList<Ec2InstanceSnapshot>> GetEc2InstancesAsync(Target target, CancellationToken ct, IReadOnlyList<string>? onlyIds = null)
        {
            Calls.Add($"ec2:{string.Join(",", onlyIds ?? [])}");
            return Task.FromResult<IReadOnlyList<Ec2InstanceSnapshot>>(Instances.Where(i => onlyIds is null || onlyIds.Contains(i.InstanceId)).ToList());
        }

        public Task<IReadOnlyList<LoadBalancerSnapshot>> GetLoadBalancersAsync(Target target, CancellationToken ct, string? onlyArn = null)
        {
            Calls.Add($"elb:{onlyArn}");
            return Task.FromResult<IReadOnlyList<LoadBalancerSnapshot>>(LoadBalancers.Where(l => onlyArn is null || l.Arn == onlyArn).ToList());
        }

        public Task<IReadOnlyList<LbListenerInfo>> GetLoadBalancerListenersAsync(Target target, LoadBalancerSnapshot lb, CancellationToken ct) => throw new NotSupportedException();
        public Task<NetworkSnapshot> GetNetworkAsync(Target target, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SecurityGroupInfo>> GetSecurityGroupsAsync(Target target, IReadOnlyList<string> groupIds, CancellationToken ct) => throw new NotSupportedException();
        public Task StartEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct) => throw new NotSupportedException();
        public Task StopEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct) => throw new NotSupportedException();
        public Task RebootEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct) => throw new NotSupportedException();
        public Task AddSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleSpec rule, CancellationToken ct) => throw new NotSupportedException();
        public Task UpdateSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleInfo current, SecurityGroupRuleSpec updated, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleInfo rule, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Ec2InstanceSnapshot Instance(string id, string state = "running", Dictionary<string, string>? tags = null, string systemStatus = "ok") =>
        new() { InstanceId = id, State = state, InstanceType = "t3.small", PrivateIp = "10.0.1.5", Tags = tags ?? new() { ["Name"] = id }, SystemStatus = systemStatus, InstanceStatus = "ok" };

    private static (MemoryStores, SettingsService, FakeGateway, HealthService, Target) Ec2Setup()
    {
        var stores = new MemoryStores();
        var settings = new SettingsService(stores);
        var gateway = new FakeGateway { PointsPerMetric = 20 };
        gateway.Instances.Add(Instance("i-web"));
        gateway.Instances.Add(Instance("i-eb", tags: new() { ["elasticbeanstalk:environment-name"] = "shop-prod" }));
        gateway.Instances.Add(Instance("i-off", "stopped"));
        var health = new HealthService(gateway, stores, settings, new Quiet());
        var target = stores.Targets[0];
        target.EcsEnabled = target.RdsEnabled = target.CacheEnabled = false;
        return (stores, settings, gateway, health, target);
    }

    [Fact]
    public async Task Ec2_lists_standalone_instances_and_reads_metrics_only_for_running_ones()
    {
        var (_, settings, gateway, health, target) = Ec2Setup();

        await health.PollHealthAsync(target, CancellationToken.None);
        Assert.Equal(["i-off", "i-web"], health.Get(1)!.Ec2.Select(e => e.Snapshot.InstanceId).OrderBy(x => x));
        await health.PollMetricsAsync(target, CancellationToken.None);
        Assert.Equal(["i-web"], gateway.LastQueries.Where(q => q.Namespace == "AWS/EC2").Select(q => q.Dimensions["InstanceId"]));
        var web = health.Get(1)!.Ec2.Single(e => e.Snapshot.InstanceId == "i-web");
        Assert.Equal(55, web.Cpu?.Current);
        Assert.Equal(HealthLevel.Ok, web.Level);
        Assert.Null(health.Get(1)!.Ec2.Single(e => e.Snapshot.IsStopped).Cpu);

        // The option to list EB-managed instances too.
        settings.Settings.Ec2IncludeEbInstances = true;
        await health.PollHealthAsync(target, CancellationToken.None);
        Assert.Contains(health.Get(1)!.Ec2, e => e.Snapshot.InstanceId == "i-eb");
    }

    [Fact]
    public async Task Hidden_resources_are_not_problems_and_cost_no_metric_queries()
    {
        var (_, settings, gateway, health, target) = Ec2Setup();
        gateway.Instances[0] = Instance("i-web", systemStatus: "impaired");
        await health.PollHealthAsync(target, CancellationToken.None);
        var web = health.Get(1)!.Ec2.Single(e => e.Snapshot.InstanceId == "i-web");
        Assert.Equal(HealthLevel.Critical, web.Level);
        Assert.True(web.IsProblem);

        settings.Settings.HiddenResources.Add(web.ResourceKey);
        health.Reevaluate();
        web = health.Get(1)!.Ec2.Single(e => e.Snapshot.InstanceId == "i-web");
        Assert.True(web.IsHidden);
        Assert.False(web.IsProblem);
        var tray = TrayStatusCalculator.Compute(settings.Targets, health.Snapshot(), [], (_, _) => null, warningsTurnIconRed: true);
        Assert.DoesNotContain(tray.Problems, p => p.Resource.Contains("i-web"));

        await health.PollMetricsAsync(target, CancellationToken.None);
        Assert.DoesNotContain(gateway.LastQueries, q => q.Dimensions.TryGetValue("InstanceId", out var id) && id == "i-web");

        // A single refresh of a hidden resource still reads its metrics.
        gateway.Calls.Clear();
        await health.RefreshResourceAsync(target, web, CancellationToken.None);
        Assert.Equal(["ec2:i-web", "metrics:1"], gateway.Calls);
    }

    private sealed class MemoryCostStore : ICostStore, INetworkStore
    {
        public List<CostSnapshot> Saved { get; } = [];
        public void Save(long targetId, CostSnapshot snapshot) => Saved.Add(snapshot);
        IReadOnlyList<CostSnapshot> ICostStore.LoadAll() => [];
        public void Save(long targetId, NetworkSnapshot snapshot) { }
        IReadOnlyList<NetworkSnapshot> INetworkStore.LoadAll() => [];
    }

    [Fact]
    public async Task Costs_are_estimated_from_cached_list_prices_and_billed_costs_are_read_only_when_enabled()
    {
        var (_, settings, gateway, health, target) = Ec2Setup();
        var store = new MemoryCostStore();
        var costs = new CostService(gateway, store, settings, health, new NetworkService(gateway, store, settings));
        await health.PollHealthAsync(target, CancellationToken.None);
        var web = health.Get(1)!.Ec2.Single(e => e.Snapshot.InstanceId == "i-web");

        // Off by default: nothing is shown or read.
        Assert.Null(costs.For(web));
        Assert.Empty(costs.MissingPrices(target, DateTime.UtcNow));

        target.CostEnabled = true;
        Assert.Equal(["ec2|t3.small|Linux"], costs.MissingPrices(target, DateTime.UtcNow));
        gateway.Calls.Clear();
        await costs.SyncAsync(target, forceActual: false, CancellationToken.None);
        Assert.Equal(["price:ec2|t3.small|Linux"], gateway.Calls);
        Assert.Equal(0, gateway.CostExplorerCalls);
        var cost = costs.For(web)!;
        Assert.Equal(0.0208 * 730, cost.Estimate!.MonthlyUsd, precision: 6);
        Assert.Equal("~$15/mo", cost.Short);
        // The stopped instance costs nothing for compute.
        Assert.Equal(0, costs.For(health.Get(1)!.Ec2.Single(e => e.Snapshot.IsStopped))!.Estimate!.MonthlyUsd);

        // Cached: the next sync asks for nothing.
        gateway.Calls.Clear();
        await costs.SyncAsync(target, forceActual: false, CancellationToken.None);
        Assert.Empty(gateway.Calls);

        // Cost Explorer only when enabled, at most twice a day unless forced.
        target.CostExplorerEnabled = true;
        await costs.SyncAsync(target, forceActual: false, CancellationToken.None);
        await costs.SyncAsync(target, forceActual: false, CancellationToken.None);
        Assert.Equal(1, gateway.CostExplorerCalls);
        await costs.SyncAsync(target, forceActual: true, CancellationToken.None);
        Assert.Equal(2, gateway.CostExplorerCalls);
        Assert.Equal(7, costs.For(web)!.Actual14Days);
    }

    [Fact]
    public async Task History_reads_the_chosen_range_with_coarser_points_for_longer_periods()
    {
        var (_, _, gateway, health, target) = Ec2Setup();
        await health.PollHealthAsync(target, CancellationToken.None);
        var web = health.Get(1)!.Ec2.Single(e => e.Snapshot.InstanceId == "i-web");

        var hour = await health.GetHistoryAsync(target, web, TimeSpan.FromHours(1), CancellationToken.None);
        Assert.Equal(60, gateway.LastQueries.Single().PeriodSeconds);
        Assert.Equal("CPU", hour.Single().Metric);
        await health.GetHistoryAsync(target, web, TimeSpan.FromDays(7), CancellationToken.None);
        Assert.Equal(3600, gateway.LastQueries.Single().PeriodSeconds);
    }

    private static LoadBalancerSnapshot Alb(params (string Name, string[] States)[] groups) => new()
    {
        Name = "web-alb",
        Arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:loadbalancer/app/web-alb/abc123",
        Type = "application",
        State = "active",
        TargetGroups = groups.Select(g => new TargetGroupInfo
        {
            Name = g.Name,
            Arn = $"arn:aws:elasticloadbalancing:us-east-1:111122223333:targetgroup/{g.Name}/def456",
            Targets = g.States.Select((s, n) => new LbTargetInfo { Id = $"i-{n}", Port = 80, State = s, Reason = s == "healthy" ? null : "Target.ResponseCodeMismatch" }).ToList(),
        }).ToList(),
    };

    [Fact]
    public async Task Load_balancer_unhealthy_targets_warn_none_healthy_is_critical_and_causes_can_be_suppressed()
    {
        var stores = new MemoryStores();
        var settings = new SettingsService(stores);
        var gateway = new FakeGateway();
        gateway.LoadBalancers.Add(Alb(("api", ["healthy", "unhealthy", "healthy"]), ("admin", ["healthy"])));
        var health = new HealthService(gateway, stores, settings, new Quiet());
        var target = stores.Targets[0];
        target.EcsEnabled = target.RdsEnabled = target.CacheEnabled = target.Ec2Enabled = false;

        await health.PollHealthAsync(target, CancellationToken.None);
        var lb = health.Get(1)!.LoadBalancers.Single();
        Assert.Equal(HealthLevel.Warn, lb.Level);
        Assert.Contains(lb.Reasons, r => r.StartsWith("Target group api has unhealthy targets (2/3 healthy", StringComparison.Ordinal));

        gateway.LoadBalancers[0] = Alb(("api", ["unhealthy", "unhealthy"]), ("admin", ["healthy"]));
        await health.PollHealthAsync(target, CancellationToken.None);
        lb = health.Get(1)!.LoadBalancers.Single();
        Assert.Equal(HealthLevel.Critical, lb.Level);

        settings.Settings.SuppressedCauses.Add(new CauseSuppression("Target group api *", target.Id, "web-alb", DateTime.UtcNow));
        health.Reevaluate();
        lb = health.Get(1)!.LoadBalancers.Single();
        Assert.Equal(HealthLevel.Ok, lb.Level);
        Assert.True(lb.CauseItems.Single().Suppressed);

        // ALB traffic metrics use the ARN suffix; the alarm on a target group belongs to the load balancer.
        gateway.Alarms.Add(new AlarmInfo
        {
            Name = "api-unhealthy", State = "ALARM", Namespace = "AWS/ApplicationELB", MetricName = "UnHealthyHostCount",
            Dimensions = new() { ["TargetGroup"] = "targetgroup/api/def456", ["LoadBalancer"] = "app/web-alb/abc123" },
        });
        await health.PollMetricsAsync(target, CancellationToken.None);
        Assert.All(gateway.LastQueries, q => Assert.Equal("app/web-alb/abc123", q.Dimensions["LoadBalancer"]));
        lb = health.Get(1)!.LoadBalancers.Single();
        Assert.Equal("api-unhealthy", lb.Alarms.Single().Name);
        Assert.Equal(HealthLevel.Critical, lb.Level);
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
