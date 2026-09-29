using System.Collections.Concurrent;
using Skypeek.Core.Credentials;
using Skypeek.Core.Health;
using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

/// <summary>Polls EB/ECS/RDS/ElastiCache state, CloudWatch metrics and alarms; evaluates health and raises notifications on changes.</summary>
public sealed class HealthService
{
    private const int MaxEventsPerEnvironment = 30;
    private static readonly TimeSpan MetricsWindow = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan AgentMetricsCacheTtl = TimeSpan.FromMinutes(60);

    private readonly IAwsGateway _gateway;
    private readonly IHealthStore _store;
    private readonly SettingsService _settings;
    private readonly INotifier _notifier;
    private readonly ConcurrentDictionary<long, TargetHealth> _state = new();
    private readonly ConcurrentDictionary<string, NotifiedState> _notified = new();
    private readonly ConcurrentDictionary<long, (DateTime At, IReadOnlyList<MetricDescriptor> Metrics)> _agentMetrics = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _targetLocks = new();

    private sealed record NotifiedState(HealthLevel Level, HashSet<string> ActiveAlarms);

    public HealthService(IAwsGateway gateway, IHealthStore store, SettingsService settings, INotifier notifier)
    {
        _gateway = gateway;
        _store = store;
        _settings = settings;
        _notifier = notifier;

        foreach (var health in store.LoadAll())
        {
            // Settings (suppressions, thresholds) may have changed since the snapshot was saved.
            if (settings.FindTarget(health.TargetId) is { } target)
                RecomputeAll(target, health);
            _state[health.TargetId] = health;
            foreach (var r in health.AllResources)
                _notified[r.ResourceKey] = new NotifiedState(r.Level, r.Alarms.Where(a => a.CountsAsProblem).Select(a => a.Name).ToHashSet());
        }
    }

    public event Action? Changed;

    public IReadOnlyList<TargetHealth> Snapshot()
    {
        var targetIds = _settings.Targets.Select(t => t.Id).ToHashSet();
        return _state.Values.Where(h => targetIds.Contains(h.TargetId)).ToList();
    }

    public TargetHealth? Get(long targetId) => _state.TryGetValue(targetId, out var h) ? h : null;

    public void Forget(long targetId) => _state.TryRemove(targetId, out _);

    public Task<int> PollHealthAsync(Target target, CancellationToken ct) => Serialized(target, () => PollHealthCoreAsync(target, ct), ct);

    public Task<int> PollMetricsAsync(Target target, CancellationToken ct) => Serialized(target, () => PollMetricsCoreAsync(target, ct), ct);

    /// <summary>Health and metrics polls of one target mutate the same snapshot, so they never run concurrently.</summary>
    private async Task<int> Serialized(Target target, Func<Task<int>> work, CancellationToken ct)
    {
        var gate = _targetLocks.GetOrAdd(target.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<int> PollHealthCoreAsync(Target target, CancellationToken ct)
    {
        var previous = _state.GetValueOrDefault(target.Id);
        var now = DateTime.UtcNow;
        var errors = new List<string>();
        var next = new TargetHealth
        {
            TargetId = target.Id,
            OtherAlarms = previous?.OtherAlarms ?? [],
            MetricsUpdated = previous?.MetricsUpdated,
            MetricsError = previous?.MetricsError,
            Eb = previous?.Eb ?? [],
            Ecs = previous?.Ecs ?? [],
            Rds = previous?.Rds ?? [],
            RdsClusters = previous?.RdsClusters ?? [],
            Caches = previous?.Caches ?? [],
            Ec2 = previous?.Ec2 ?? [],
            LoadBalancers = previous?.LoadBalancers ?? [],
        };
        var since = previous?.HealthUpdated?.AddMinutes(-2) ?? now.AddHours(-24);

        if (target.EbEnabled)
        {
            try
            {
                var envs = await _gateway.GetEbEnvironmentsAsync(target, since, ct);
                next.Eb = envs.Select(env => BuildEb(target, env, previous?.Eb.FirstOrDefault(p => p.Snapshot.EnvironmentId == env.EnvironmentId), now)).ToList();
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"Elastic Beanstalk: {ex.Message}");
            }
        }
        else
        {
            next.Eb = [];
        }

        if (target.EcsEnabled)
        {
            try
            {
                var services = await _gateway.GetEcsServicesAsync(target, ct);
                var list = new List<EcsServiceStatus>();
                foreach (var svc in services)
                    list.Add(await BuildEcsAsync(target, svc, previous?.Ecs.FirstOrDefault(p => p.Snapshot.ServiceArn == svc.ServiceArn), now, ct));
                next.Ecs = list;
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"ECS: {ex.Message}");
            }
        }
        else
        {
            next.Ecs = [];
        }

        if (target.RdsEnabled)
        {
            try
            {
                var rds = await _gateway.GetRdsAsync(target, since, ct);
                next.Rds = rds.Instances.Select(db => BuildRds(target, db, previous?.Rds.FirstOrDefault(p => p.Snapshot.Identifier == db.Identifier), now)).ToList();
                next.RdsClusters = rds.Clusters.Select(c => BuildRdsCluster(target, c, previous?.RdsClusters.FirstOrDefault(p => p.Snapshot.Identifier == c.Identifier), now)).ToList();
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"RDS: {ex.Message}");
            }
        }
        else
        {
            next.Rds = [];
            next.RdsClusters = [];
        }

        if (target.CacheEnabled)
        {
            try
            {
                var caches = await _gateway.GetCachesAsync(target, since, ct);
                next.Caches = caches.Select(c => BuildCache(target, c, previous?.Caches.FirstOrDefault(p => p.Snapshot.Id == c.Id && p.Snapshot.Kind == c.Kind), now)).ToList();
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"ElastiCache: {ex.Message}");
            }
        }
        else
        {
            next.Caches = [];
        }

        if (target.Ec2Enabled)
        {
            try
            {
                var includeEb = _settings.Settings.Ec2IncludeEbInstances;
                var instances = await _gateway.GetEc2InstancesAsync(target, ct);
                next.Ec2 = instances
                    .Where(i => includeEb || i.EbEnvironment is null)
                    .Select(i => BuildEc2(target, i, previous?.Ec2.FirstOrDefault(p => p.Snapshot.InstanceId == i.InstanceId), now))
                    .ToList();
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"EC2: {ex.Message}");
            }
        }
        else
        {
            next.Ec2 = [];
        }

        if (target.ElbEnabled)
        {
            try
            {
                var lbs = await _gateway.GetLoadBalancersAsync(target, ct);
                next.LoadBalancers = lbs.Select(lb => BuildLoadBalancer(target, lb, previous?.LoadBalancers.FirstOrDefault(p => p.Snapshot.Arn == lb.Arn), now)).ToList();
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"Load balancers: {ex.Message}");
            }
        }
        else
        {
            next.LoadBalancers = [];
        }

        next.HealthUpdated = now;
        next.HealthError = errors.Count > 0 ? string.Join("; ", errors) : null;
        RecomputeAll(target, next);
        Commit(target, next);

        if (errors.Count > 0)
            throw new InvalidOperationException(next.HealthError);
        return next.AllResources.Count();
    }

    private async Task<int> PollMetricsCoreAsync(Target target, CancellationToken ct)
    {
        if (!_state.TryGetValue(target.Id, out var health) || health.HealthUpdated is null)
        {
            try
            {
                await PollHealthCoreAsync(target, ct);
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                // Partial health is fine; metrics for whatever was discovered are still useful.
            }
            health = _state.GetValueOrDefault(target.Id) ?? new TargetHealth { TargetId = target.Id };
        }

        var settings = _settings.Settings;
        var now = DateTime.UtcNow;
        var errors = new List<string>();

        var (queries, bindings) = await BuildMetricQueriesAsync(target, health, ct, skipHidden: true);
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> data = new Dictionary<string, IReadOnlyList<MetricPoint>>();
        if (queries.Count > 0)
        {
            try
            {
                data = await _gateway.GetMetricDataAsync(target, queries, now - MetricsWindow, now, ct);
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"Metrics: {ex.Message}");
            }
        }

        List<AlarmInfo>? alarms = null;
        try
        {
            var result = await _gateway.GetAlarmsAsync(target, now.AddHours(-Math.Max(1, settings.AlarmHistoryHours)), ct);
            alarms = result.Alarms.Where(a => HealthRules.IsRelevantAlarm(a) && (a.IsActive || a.IsRecent)).ToList();
        }
        catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
        {
            errors.Add($"Alarms: {ex.Message}");
        }

        ApplyMetrics(target, health, bindings, data, settings, now, errors.Count == 0 || data.Count > 0);
        if (alarms is not null)
            ApplyAlarms(health, alarms);

        health.MetricsUpdated = now;
        health.MetricsError = errors.Count > 0 ? string.Join("; ", errors) : null;
        RecomputeAll(target, health);
        Commit(target, health);

        if (errors.Count > 0)
            throw new InvalidOperationException(health.MetricsError);
        return queries.Count;
    }

    /// <summary>
    /// Re-reads one resource (EB environment, ECS service, RDS instance or cluster, cache) and its metrics right now,
    /// without waiting for the target-wide polls. Alarms keep their last polled state.
    /// </summary>
    public Task<int> RefreshResourceAsync(Target target, ResourceStatus resource, CancellationToken ct) =>
        Serialized(target, () => RefreshResourceCoreAsync(target, resource, ct), ct);

    private async Task<int> RefreshResourceCoreAsync(Target target, ResourceStatus resource, CancellationToken ct)
    {
        var health = _state.GetValueOrDefault(target.Id) ?? new TargetHealth { TargetId = target.Id };
        var now = DateTime.UtcNow;
        var single = new TargetHealth { TargetId = target.Id };

        switch (resource)
        {
            case EbEnvironmentStatus eb:
            {
                var prev = health.Eb.FirstOrDefault(e => e.ResourceKey == eb.ResourceKey) ?? eb;
                var since = prev.RecentEvents.Count > 0 ? prev.RecentEvents.Max(e => e.Date).AddMinutes(-2) : now.AddHours(-24);
                var envs = await _gateway.GetEbEnvironmentsAsync(target, since, ct, eb.Snapshot.EnvironmentName);
                if (envs.FirstOrDefault() is not { } env)
                    throw new InvalidOperationException($"{eb.Snapshot.EnvironmentName} was not found (terminated?).");
                var fresh = BuildEb(target, env, prev, now);
                fresh.Alarms = prev.Alarms;
                single.Eb = [fresh];
                break;
            }
            case EcsServiceStatus ecs:
            {
                var prev = health.Ecs.FirstOrDefault(e => e.ResourceKey == ecs.ResourceKey) ?? ecs;
                var services = await _gateway.GetEcsServicesAsync(target, ct, ecs.Snapshot.ClusterArn, ecs.Snapshot.ServiceArn ?? ecs.Snapshot.ServiceName);
                if (services.FirstOrDefault() is not { } svc)
                    throw new InvalidOperationException($"{ecs.Snapshot.ServiceName} was not found (deleted?).");
                var fresh = await BuildEcsAsync(target, svc, prev, now, ct);
                fresh.Alarms = prev.Alarms;
                single.Ecs = [fresh];
                break;
            }
            case RdsInstanceStatus db:
            {
                var prev = health.Rds.FirstOrDefault(e => e.ResourceKey == db.ResourceKey) ?? db;
                var inventory = await _gateway.GetRdsAsync(target, EventsSince(prev.RecentEvents, now), ct, onlyInstance: db.Snapshot.Identifier);
                if (inventory.Instances.FirstOrDefault() is not { } snapshot)
                    throw new InvalidOperationException($"{db.Snapshot.Identifier} was not found (deleted?).");
                var fresh = BuildRds(target, snapshot, prev, now);
                fresh.Alarms = prev.Alarms;
                single.Rds = [fresh];
                break;
            }
            case RdsClusterStatus cluster:
            {
                var prev = health.RdsClusters.FirstOrDefault(e => e.ResourceKey == cluster.ResourceKey) ?? cluster;
                var inventory = await _gateway.GetRdsAsync(target, EventsSince(prev.RecentEvents, now), ct, onlyCluster: cluster.Snapshot.Identifier);
                if (inventory.Clusters.FirstOrDefault() is not { } snapshot)
                    throw new InvalidOperationException($"{cluster.Snapshot.Identifier} was not found (deleted?).");
                var fresh = BuildRdsCluster(target, snapshot, prev, now);
                fresh.Alarms = prev.Alarms;
                single.RdsClusters = [fresh];
                // The cluster's instances are refreshed with it.
                single.Rds = inventory.Instances.Select(i =>
                {
                    var old = health.Rds.FirstOrDefault(r => r.Snapshot.Identifier == i.Identifier);
                    var member = BuildRds(target, i, old, now);
                    member.Alarms = old?.Alarms ?? [];
                    return member;
                }).ToList();
                break;
            }
            case CacheStatus cache:
            {
                var prev = health.Caches.FirstOrDefault(e => e.ResourceKey == cache.ResourceKey) ?? cache;
                var caches = await _gateway.GetCachesAsync(target, EventsSince(prev.RecentEvents, now), ct, cache.Snapshot);
                if (caches.FirstOrDefault() is not { } snapshot)
                    throw new InvalidOperationException($"{cache.Snapshot.Id} was not found (deleted?).");
                var fresh = BuildCache(target, snapshot, prev, now);
                fresh.Alarms = prev.Alarms;
                single.Caches = [fresh];
                break;
            }
            case Ec2InstanceStatus ec2:
            {
                var prev = health.Ec2.FirstOrDefault(e => e.ResourceKey == ec2.ResourceKey) ?? ec2;
                var instances = await _gateway.GetEc2InstancesAsync(target, ct, [ec2.Snapshot.InstanceId]);
                if (instances.FirstOrDefault() is not { } snapshot)
                    throw new InvalidOperationException($"{ec2.Snapshot.InstanceId} was not found (terminated?).");
                var fresh = BuildEc2(target, snapshot, prev, now);
                fresh.Alarms = prev.Alarms;
                single.Ec2 = [fresh];
                break;
            }
            case LoadBalancerStatus lb:
            {
                var prev = health.LoadBalancers.FirstOrDefault(e => e.ResourceKey == lb.ResourceKey) ?? lb;
                var lbs = await _gateway.GetLoadBalancersAsync(target, ct, lb.Snapshot.Arn);
                if (lbs.FirstOrDefault() is not { } snapshot)
                    throw new InvalidOperationException($"{lb.Snapshot.Name} was not found (deleted?).");
                var fresh = BuildLoadBalancer(target, snapshot, prev, now);
                fresh.Alarms = prev.Alarms;
                single.LoadBalancers = [fresh];
                break;
            }
            default:
                return 0;
        }

        // Metrics for just this resource.
        var settings = _settings.Settings;
        var (queries, bindings) = await BuildMetricQueriesAsync(target, single, ct);
        if (queries.Count > 0)
        {
            var data = await _gateway.GetMetricDataAsync(target, queries, now - MetricsWindow, now, ct);
            ApplyMetrics(target, single, bindings, data, settings, now, haveData: true);
        }

        // Merge into the target's snapshot.
        var merged = new TargetHealth
        {
            TargetId = target.Id,
            Eb = Merge(health.Eb, single.Eb),
            Ecs = Merge(health.Ecs, single.Ecs),
            Rds = Merge(health.Rds, single.Rds),
            RdsClusters = Merge(health.RdsClusters, single.RdsClusters),
            Caches = Merge(health.Caches, single.Caches),
            Ec2 = Merge(health.Ec2, single.Ec2),
            LoadBalancers = Merge(health.LoadBalancers, single.LoadBalancers),
            OtherAlarms = health.OtherAlarms,
            HealthUpdated = health.HealthUpdated,
            MetricsUpdated = health.MetricsUpdated,
            HealthError = health.HealthError,
            MetricsError = health.MetricsError,
        };
        RecomputeAll(target, merged);
        Commit(target, merged);
        return 1;
    }

    private static List<T> Merge<T>(List<T> existing, List<T> fresh) where T : ResourceStatus =>
        existing.Where(e => fresh.All(f => f.ResourceKey != e.ResourceKey)).Concat(fresh).ToList();

    private static DateTime EventsSince(List<ServiceEvent> events, DateTime now) =>
        events.Count > 0 ? events.Max(e => e.Date).AddMinutes(-2) : now.AddHours(-24);

    /// <summary>One CPU/memory series to analyze; <paramref name="ToPercent"/> converts raw values (e.g. free bytes) to % used.</summary>
    /// <param name="Inverted">The raw metric falls as usage rises (free memory), so its minimum is the usage peak.</param>
    private sealed record UsageSource(string Scope, string Metric, string Namespace, string MetricName, IReadOnlyDictionary<string, string> Dimensions,
        Func<double, double>? ToPercent = null, bool Inverted = false, bool Estimated = false);

    /// <summary>
    /// Hourly CPU/memory statistics of the resource's nodes over the last <paramref name="days"/> days, read from
    /// CloudWatch on demand (Skypeek itself keeps only the last hour), with a sizing recommendation.
    /// </summary>
    public async Task<UsageReport> AnalyzeUsageAsync(Target target, ResourceStatus resource, int days, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var from = now.AddDays(-days);
        var sources = new List<UsageSource>();
        string kind;
        switch (resource)
        {
            case EcsServiceStatus ecs:
                kind = "ecs";
                var ecsDims = new Dictionary<string, string> { ["ClusterName"] = ecs.Snapshot.ClusterName, ["ServiceName"] = ecs.Snapshot.ServiceName };
                sources.Add(new(ecs.Snapshot.ServiceName, "CPU", "AWS/ECS", "CPUUtilization", ecsDims));
                sources.Add(new(ecs.Snapshot.ServiceName, "Memory", "AWS/ECS", "MemoryUtilization", ecsDims));
                break;
            case EbEnvironmentStatus eb:
                kind = "ec2";
                var agent = await GetAgentMemoryMetricsAsync(target, ct);
                foreach (var id in eb.Snapshot.InstanceIds)
                {
                    sources.Add(new(id, "CPU", "AWS/EC2", "CPUUtilization", new Dictionary<string, string> { ["InstanceId"] = id }));
                    if (agent.FirstOrDefault(d => d.Dimensions.TryGetValue("InstanceId", out var i) && i == id) is { } memory)
                        sources.Add(new(id, "Memory", memory.Namespace, memory.MetricName, memory.Dimensions));
                }
                break;
            case Ec2InstanceStatus ec2:
            {
                kind = "ec2";
                var id = ec2.Snapshot.InstanceId;
                sources.Add(new(id, "CPU", "AWS/EC2", "CPUUtilization", new Dictionary<string, string> { ["InstanceId"] = id }));
                var agentMetrics = await GetAgentMemoryMetricsAsync(target, ct);
                if (agentMetrics.FirstOrDefault(d => d.Dimensions.TryGetValue("InstanceId", out var i) && i == id) is { } memory)
                    sources.Add(new(id, "Memory", memory.Namespace, memory.MetricName, memory.Dimensions));
                break;
            }
            case RdsInstanceStatus db:
                kind = "rds";
                var dbDims = new Dictionary<string, string> { ["DBInstanceIdentifier"] = db.Snapshot.Identifier };
                sources.Add(new(db.Snapshot.Identifier, "CPU", "AWS/RDS", "CPUUtilization", dbDims));
                // RDS reports free memory only; used % is estimated from the instance class size.
                if (HealthRules.InstanceClassMemoryBytes(db.Snapshot.InstanceClass, null) is { } total)
                    sources.Add(new(db.Snapshot.Identifier, "Memory", "AWS/RDS", "FreeableMemory", dbDims, v => 100 - v / total * 100, Inverted: true, Estimated: true));
                if (db.Snapshot.MaxConnections is { } max and > 0)
                    sources.Add(new(db.Snapshot.Identifier, "Connections", "AWS/RDS", "DatabaseConnections", dbDims, v => v / max * 100,
                        Estimated: db.Snapshot.MaxConnectionsSource?.StartsWith('≈') == true));
                break;
            case CacheStatus cache when cache.Snapshot.Kind != CacheKind.Serverless:
                kind = "cache";
                var memcached = cache.Snapshot.Engine == "memcached";
                foreach (var node in cache.Snapshot.Nodes)
                {
                    var dims = new Dictionary<string, string> { ["CacheClusterId"] = node.ClusterId, ["CacheNodeId"] = node.NodeId };
                    sources.Add(new(node.ClusterId, "CPU", "AWS/ElastiCache", memcached ? "CPUUtilization" : "EngineCPUUtilization", dims));
                    if (!memcached)
                        sources.Add(new(node.ClusterId, "Memory", "AWS/ElastiCache", "DatabaseMemoryUsagePercentage", dims));
                }
                break;
            default:
                return new UsageReport(from, now, [], Provisioning.Unknown, ["Usage analysis is not available for this resource type."], now);
        }

        var queries = new List<MetricQuery>();
        for (var i = 0; i < sources.Count; i++)
            foreach (var stat in new[] { "Average", "Minimum", "Maximum" })
                queries.Add(new MetricQuery($"u{i}{stat.ToLowerInvariant()}", sources[i].Namespace, sources[i].MetricName, sources[i].Dimensions, 3600, stat));
        var data = queries.Count == 0
            ? new Dictionary<string, IReadOnlyList<MetricPoint>>()
            : await _gateway.GetMetricDataAsync(target, queries, from, now, ct);

        var series = new List<UsageSeries>();
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            IReadOnlyList<MetricPoint> Points(string stat)
            {
                var points = data.GetValueOrDefault($"u{i}{stat}") ?? [];
                return source.ToPercent is { } f ? points.Select(p => p with { Value = Math.Clamp(f(p.Value), 0, 100) }).ToList() : points;
            }
            var (min, max) = source.Inverted ? (Points("maximum"), Points("minimum")) : (Points("minimum"), Points("maximum"));
            series.Add(UsageAnalysis.Summarize(source.Scope, source.Metric, Points("average"), min, max, source.Estimated));
        }
        var (verdict, reasons) = UsageAnalysis.Recommend(series, kind);
        return new UsageReport(from, now, series, verdict, reasons, DateTime.UtcNow);
    }

    /// <summary>Number of CloudWatch metrics one metrics poll requests for the target (for the cost estimate).</summary>
    public int MetricCount(long targetId)
    {
        if (!_state.TryGetValue(targetId, out var h))
            return 0;
        return h.Ecs.Count * 2
               + h.Eb.Sum(e => e.Snapshot.InstanceIds.Count * (e.Instances.Any(i => i.Memory is not null) ? 2 : 1))
               + h.Rds.Sum(r => RdsMetricNames(r.Snapshot).Count())
               + h.Caches.Sum(c => c.Snapshot.Kind == CacheKind.Serverless ? 2 : c.Snapshot.Nodes.Count() * 5)
               + h.Ec2.Where(e => e.Snapshot.IsRunning && !e.IsHidden).Sum(e => e.Memory is not null ? 2 : 1)
               + h.LoadBalancers.Where(l => !l.IsHidden).Sum(l => LoadBalancerMetricNames(l.Snapshot).Count());
    }

    /// <summary>Re-evaluates stored data with the current thresholds and alarm suppressions (after settings change).</summary>
    public void Reevaluate()
    {
        var settings = _settings.Settings;
        foreach (var target in _settings.Targets)
        {
            if (!_state.TryGetValue(target.Id, out var h))
                continue;
            var now = DateTime.UtcNow;
            foreach (var ecs in h.Ecs)
            {
                var th = HealthRules.ResolveThresholds(settings, target, ecs.ResourceKey, isEcs: true);
                if (ecs.Cpu is { } cpu) ecs.Cpu = HealthRules.EvaluateMetric(cpu.MetricName, cpu.Points, th.CpuWarn, th.CpuCritical, settings.SustainedMinutes, now);
                if (ecs.Memory is { } mem) ecs.Memory = HealthRules.EvaluateMetric(mem.MetricName, mem.Points, th.MemWarn, th.MemCritical, settings.SustainedMinutes, now);
            }
            foreach (var eb in h.Eb)
            {
                var th = HealthRules.ResolveThresholds(settings, target, eb.ResourceKey, isEcs: false);
                foreach (var inst in eb.Instances)
                {
                    if (inst.Cpu is { } cpu) inst.Cpu = HealthRules.EvaluateMetric(cpu.MetricName, cpu.Points, th.CpuWarn, th.CpuCritical, settings.SustainedMinutes, now);
                    if (inst.Memory is { } mem) inst.Memory = HealthRules.EvaluateMetric(mem.MetricName, mem.Points, th.MemWarn, th.MemCritical, settings.SustainedMinutes, now);
                }
                eb.Cpu = Worst(eb.Instances.Select(i => i.Cpu));
                eb.Memory = Worst(eb.Instances.Select(i => i.Memory));
            }
            foreach (var db in h.Rds)
            {
                var th = HealthRules.ResolveThresholds(settings, target, db);
                if (db.Cpu is { } cpu) db.Cpu = HealthRules.EvaluateMetric(cpu.MetricName, cpu.Points, th.CpuWarn, th.CpuCritical, settings.SustainedMinutes, now);
                if (db.Memory is { } conn) db.Memory = HealthRules.EvaluateMetric(conn.MetricName, conn.Points, th.MemWarn, th.MemCritical, settings.SustainedMinutes, now);
                var (storageWarn, storageCritical) = HealthRules.ResolveStorageThresholds(settings, db.ResourceKey);
                if (db.StorageUsed is { } st) db.StorageUsed = HealthRules.EvaluateMetric(st.MetricName, st.Points, storageWarn, storageCritical, settings.SustainedMinutes, now);
            }
            foreach (var ec2 in h.Ec2)
            {
                var th = HealthRules.ResolveThresholds(settings, target, ec2);
                if (ec2.Cpu is { } cpu) ec2.Cpu = HealthRules.EvaluateMetric(cpu.MetricName, cpu.Points, th.CpuWarn, th.CpuCritical, settings.SustainedMinutes, now);
                if (ec2.Memory is { } mem) ec2.Memory = HealthRules.EvaluateMetric(mem.MetricName, mem.Points, th.MemWarn, th.MemCritical, settings.SustainedMinutes, now);
            }
            foreach (var cache in h.Caches)
            {
                var th = HealthRules.ResolveThresholds(settings, target, cache);
                foreach (var node in cache.NodeMetrics)
                {
                    if (node.Cpu is { } cpu) node.Cpu = HealthRules.EvaluateMetric(cpu.MetricName, cpu.Points, th.CpuWarn, th.CpuCritical, settings.SustainedMinutes, now);
                    if (node.Memory is { } mem) node.Memory = HealthRules.EvaluateMetric(mem.MetricName, mem.Points, th.MemWarn, th.MemCritical, settings.SustainedMinutes, now);
                }
                cache.Cpu = Worst(cache.NodeMetrics.Select(n => n.Cpu));
                cache.Memory = Worst(cache.NodeMetrics.Select(n => n.Memory));
            }
            RecomputeAll(target, h);
            // A settings change (e.g. suppressing an alarm) is the user's own action, not news worth a toast.
            Commit(target, h, notify: false);
        }
    }

    private EbEnvironmentStatus BuildEb(Target target, EbEnvironmentSnapshot env, EbEnvironmentStatus? prev, DateTime now)
    {
        var events = (prev?.RecentEvents ?? [])
            .Concat(env.NewEvents)
            .DistinctBy(e => (e.Date, e.Message))
            .OrderByDescending(e => e.Date)
            .Take(MaxEventsPerEnvironment)
            .ToList();

        var (level, reasons) = HealthRules.EvaluateEb(env, events, now);
        var instances = env.InstanceIds
            .Select(id => prev?.Instances.FirstOrDefault(i => i.InstanceId == id) ?? new InstanceMetric { InstanceId = id })
            .ToList();

        return new EbEnvironmentStatus
        {
            TargetId = target.Id,
            TargetName = target.DisplayName,
            Region = target.Region,
            Snapshot = env,
            RecentEvents = events,
            Instances = instances,
            BaseLevel = level,
            BaseReasons = reasons,
            Cpu = Worst(instances.Select(i => i.Cpu)),
            Memory = Worst(instances.Select(i => i.Memory)),
            Alarms = prev?.Alarms ?? [],
            RefreshedUtc = now,
        };
    }

    private static List<ServiceEvent> MergeEvents(List<ServiceEvent>? previous, List<ServiceEvent> fresh) =>
        (previous ?? []).Concat(fresh)
            .DistinctBy(e => (e.Date, e.Message))
            .OrderByDescending(e => e.Date)
            .Take(MaxEventsPerEnvironment)
            .ToList();

    private static RdsInstanceStatus BuildRds(Target target, RdsInstanceSnapshot db, RdsInstanceStatus? prev, DateTime now)
    {
        var events = MergeEvents(prev?.RecentEvents, db.NewEvents);
        var (level, reasons) = HealthRules.EvaluateRdsInstance(db, events, now);
        return new RdsInstanceStatus
        {
            TargetId = target.Id,
            TargetName = target.DisplayName,
            Region = target.Region,
            Snapshot = db,
            RecentEvents = events,
            BaseLevel = level,
            BaseReasons = reasons,
            Cpu = prev?.Cpu,
            Memory = db.MaxConnections is null ? null : prev?.Memory,
            Connections = prev?.Connections,
            StorageUsed = db.IsAurora ? null : prev?.StorageUsed,
            FreeStorageBytes = prev?.FreeStorageBytes,
            FreeableMemoryBytes = prev?.FreeableMemoryBytes,
            ReplicaLagSeconds = prev?.ReplicaLagSeconds,
            Alarms = prev?.Alarms ?? [],
            RefreshedUtc = now,
        };
    }

    private static RdsClusterStatus BuildRdsCluster(Target target, RdsClusterSnapshot cluster, RdsClusterStatus? prev, DateTime now)
    {
        var events = MergeEvents(prev?.RecentEvents, cluster.NewEvents);
        var (level, reasons) = HealthRules.EvaluateRdsStatus(cluster.Status, events, now);
        if (cluster.Members.Count > 0 && !cluster.Members.Any(m => m.IsWriter) && cluster.ReplicationSource is null && cluster.GlobalCluster is null)
        {
            level = HealthLevel.Critical;
            reasons.Add("No writer instance");
        }
        return new RdsClusterStatus
        {
            TargetId = target.Id,
            TargetName = target.DisplayName,
            Region = target.Region,
            Snapshot = cluster,
            RecentEvents = events,
            BaseLevel = level,
            BaseReasons = reasons,
            Alarms = prev?.Alarms ?? [],
            RefreshedUtc = now,
        };
    }

    private static CacheStatus BuildCache(Target target, CacheSnapshot cache, CacheStatus? prev, DateTime now)
    {
        var events = MergeEvents(prev?.RecentEvents, cache.NewEvents);
        var (level, reasons) = HealthRules.EvaluateCache(cache, events, now);
        var nodes = cache.Kind == CacheKind.Serverless
            ? [prev?.NodeMetrics.FirstOrDefault() ?? new CacheNodeMetric { ClusterId = cache.Id }]
            : cache.Nodes.Select(n => prev?.NodeMetrics.FirstOrDefault(m => m.ClusterId == n.ClusterId && m.NodeId == n.NodeId)
                                      ?? new CacheNodeMetric { ClusterId = n.ClusterId, NodeId = n.NodeId }).ToList();
        return new CacheStatus
        {
            TargetId = target.Id,
            TargetName = target.DisplayName,
            Region = target.Region,
            Snapshot = cache,
            RecentEvents = events,
            NodeMetrics = nodes,
            BaseLevel = level,
            BaseReasons = reasons,
            Cpu = Worst(nodes.Select(n => n.Cpu)),
            Memory = Worst(nodes.Select(n => n.Memory)),
            Alarms = prev?.Alarms ?? [],
            RefreshedUtc = now,
        };
    }

    private static Ec2InstanceStatus BuildEc2(Target target, Ec2InstanceSnapshot instance, Ec2InstanceStatus? prev, DateTime now)
    {
        var (level, reasons) = HealthRules.EvaluateEc2(instance);
        return new Ec2InstanceStatus
        {
            TargetId = target.Id,
            TargetName = target.DisplayName,
            Region = target.Region,
            Snapshot = instance,
            BaseLevel = level,
            BaseReasons = reasons,
            // A stopped instance has no current load.
            Cpu = instance.IsRunning ? prev?.Cpu : null,
            Memory = instance.IsRunning ? prev?.Memory : null,
            Alarms = prev?.Alarms ?? [],
            RefreshedUtc = now,
        };
    }

    private static LoadBalancerStatus BuildLoadBalancer(Target target, LoadBalancerSnapshot lb, LoadBalancerStatus? prev, DateTime now)
    {
        var (level, reasons) = HealthRules.EvaluateLoadBalancer(lb);
        return new LoadBalancerStatus
        {
            TargetId = target.Id,
            TargetName = target.DisplayName,
            Region = target.Region,
            Snapshot = lb,
            BaseLevel = level,
            BaseReasons = reasons,
            RequestCount = prev?.RequestCount,
            Elb5xxCount = prev?.Elb5xxCount,
            Target5xxCount = prev?.Target5xxCount,
            ActiveFlows = prev?.ActiveFlows,
            Alarms = prev?.Alarms ?? [],
            RefreshedUtc = now,
        };
    }

    private async Task<EcsServiceStatus> BuildEcsAsync(Target target, EcsServiceSnapshot svc, EcsServiceStatus? prev, DateTime now, CancellationToken ct)
    {
        var (level, reasons) = HealthRules.EvaluateEcs(svc, now);
        var stopped = new List<string>();
        if (level >= HealthLevel.Warn)
        {
            try
            {
                stopped = (await _gateway.GetStoppedTaskReasonsAsync(target, svc.ClusterArn, svc.ServiceName, ct)).ToList();
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                stopped.Add($"(could not read stopped tasks: {ex.Message})");
            }
        }

        return new EcsServiceStatus
        {
            TargetId = target.Id,
            TargetName = target.DisplayName,
            Region = target.Region,
            Snapshot = svc,
            StoppedReasons = stopped,
            BaseLevel = level,
            BaseReasons = reasons,
            Cpu = prev?.Cpu,
            Memory = prev?.Memory,
            Alarms = prev?.Alarms ?? [],
            RefreshedUtc = now,
        };
    }

    private enum MetricSlot
    {
        EcsCpu, EcsMemory, Ec2Cpu, Ec2Memory,
        RdsCpu, RdsConnections, RdsFreeStorage, RdsFreeableMemory, RdsReplicaLag,
        CacheCpu, CacheMemory, CacheConnections, CacheEvictions, CacheHitRate, CacheReplicationLag,
        InstanceCpu, InstanceMemory,
        LbRequests, LbElb5xx, LbTarget5xx, LbActiveFlows,
    }

    /// <summary>Traffic metrics per load balancer type (counts over the last hour, not thresholded).</summary>
    private static IEnumerable<(string Metric, MetricSlot Slot, string Stat)> LoadBalancerMetricNames(LoadBalancerSnapshot lb)
    {
        switch (lb.Type)
        {
            case "application":
                yield return ("RequestCount", MetricSlot.LbRequests, "Sum");
                yield return ("HTTPCode_ELB_5XX_Count", MetricSlot.LbElb5xx, "Sum");
                yield return ("HTTPCode_Target_5XX_Count", MetricSlot.LbTarget5xx, "Sum");
                break;
            case "network":
                yield return ("ActiveFlowCount", MetricSlot.LbActiveFlows, "Average");
                break;
        }
    }

    /// <param name="InstanceId">EC2 instance id, or "cluster|node" for a cache node.</param>
    private sealed record Binding(ResourceStatus Resource, MetricSlot Slot, string? InstanceId);

    /// <summary>RDS metrics read per instance: storage only where it is allocated, lag only on replicas.</summary>
    private static IEnumerable<(string Metric, MetricSlot Slot)> RdsMetricNames(RdsInstanceSnapshot db)
    {
        yield return ("CPUUtilization", MetricSlot.RdsCpu);
        yield return ("DatabaseConnections", MetricSlot.RdsConnections);
        yield return ("FreeableMemory", MetricSlot.RdsFreeableMemory);
        if (!db.IsAurora)
            yield return ("FreeStorageSpace", MetricSlot.RdsFreeStorage);
        if (db.ReplicaSource is not null)
            yield return ("ReplicaLag", MetricSlot.RdsReplicaLag);
        else if (db.IsAurora && db.IsClusterWriter == false)
            yield return ("AuroraReplicaLag", MetricSlot.RdsReplicaLag);
    }

    /// <param name="skipHidden">Target-wide polls skip resources hidden from the dashboard (no cost); a single refresh does not.</param>
    private async Task<(List<MetricQuery>, Dictionary<string, Binding>)> BuildMetricQueriesAsync(Target target, TargetHealth health, CancellationToken ct, bool skipHidden = false)
    {
        var queries = new List<MetricQuery>();
        var bindings = new Dictionary<string, Binding>();

        void Add(string ns, string metric, IReadOnlyDictionary<string, string> dims, Binding binding, string stat = "Average")
        {
            if (skipHidden && binding.Resource.IsHidden)
                return;
            var id = $"m{queries.Count}";
            queries.Add(new MetricQuery(id, ns, metric, dims, Stat: stat));
            bindings[id] = binding;
        }

        foreach (var db in health.Rds)
        {
            var dims = new Dictionary<string, string> { ["DBInstanceIdentifier"] = db.Snapshot.Identifier };
            foreach (var (metric, slot) in RdsMetricNames(db.Snapshot))
                Add("AWS/RDS", metric, dims, new Binding(db, slot, null));
        }

        foreach (var cache in health.Caches)
        {
            if (cache.Snapshot.Kind == CacheKind.Serverless)
            {
                var dims = new Dictionary<string, string> { ["clusterId"] = cache.Snapshot.Id };
                Add("AWS/ElastiCache", "CurrConnections", dims, new Binding(cache, MetricSlot.CacheConnections, $"{cache.Snapshot.Id}|"));
                Add("AWS/ElastiCache", "CacheHitRate", dims, new Binding(cache, MetricSlot.CacheHitRate, $"{cache.Snapshot.Id}|"));
                continue;
            }
            var memcached = cache.Snapshot.Engine == "memcached";
            foreach (var node in cache.Snapshot.Nodes)
            {
                var dims = new Dictionary<string, string> { ["CacheClusterId"] = node.ClusterId, ["CacheNodeId"] = node.NodeId };
                var key = $"{node.ClusterId}|{node.NodeId}";
                Add("AWS/ElastiCache", memcached ? "CPUUtilization" : "EngineCPUUtilization", dims, new Binding(cache, MetricSlot.CacheCpu, key));
                Add("AWS/ElastiCache", "CurrConnections", dims, new Binding(cache, MetricSlot.CacheConnections, key));
                Add("AWS/ElastiCache", "Evictions", dims, new Binding(cache, MetricSlot.CacheEvictions, key), "Sum");
                if (!memcached)
                {
                    Add("AWS/ElastiCache", "DatabaseMemoryUsagePercentage", dims, new Binding(cache, MetricSlot.CacheMemory, key));
                    Add("AWS/ElastiCache", "CacheHitRate", dims, new Binding(cache, MetricSlot.CacheHitRate, key));
                    if (node.Role == "replica")
                        Add("AWS/ElastiCache", "ReplicationLag", dims, new Binding(cache, MetricSlot.CacheReplicationLag, key));
                }
            }
        }

        foreach (var ecs in health.Ecs)
        {
            var dims = new Dictionary<string, string> { ["ClusterName"] = ecs.Snapshot.ClusterName, ["ServiceName"] = ecs.Snapshot.ServiceName };
            Add("AWS/ECS", "CPUUtilization", dims, new Binding(ecs, MetricSlot.EcsCpu, null));
            Add("AWS/ECS", "MemoryUtilization", dims, new Binding(ecs, MetricSlot.EcsMemory, null));
        }

        foreach (var lb in health.LoadBalancers)
        {
            var dims = new Dictionary<string, string> { ["LoadBalancer"] = lb.Snapshot.ArnSuffix };
            foreach (var (metric, slot, stat) in LoadBalancerMetricNames(lb.Snapshot))
                Add(lb.Snapshot.Namespace, metric, dims, new Binding(lb, slot, null), stat);
        }

        // EC2 instances: of EB environments, and standalone ones (running only).
        var instanceOwners = new Dictionary<string, List<(ResourceStatus Owner, MetricSlot Memory)>>();
        void Own(string id, ResourceStatus owner, MetricSlot memory)
        {
            if (skipHidden && owner.IsHidden)
                return;
            if (!instanceOwners.TryGetValue(id, out var list))
                instanceOwners[id] = list = [];
            list.Add((owner, memory));
        }
        foreach (var eb in health.Eb)
            foreach (var id in eb.Snapshot.InstanceIds)
            {
                Own(id, eb, MetricSlot.Ec2Memory);
                Add("AWS/EC2", "CPUUtilization", new Dictionary<string, string> { ["InstanceId"] = id }, new Binding(eb, MetricSlot.Ec2Cpu, id));
            }
        foreach (var ec2 in health.Ec2.Where(e => e.Snapshot.IsRunning))
        {
            var id = ec2.Snapshot.InstanceId;
            Own(id, ec2, MetricSlot.InstanceMemory);
            Add("AWS/EC2", "CPUUtilization", new Dictionary<string, string> { ["InstanceId"] = id }, new Binding(ec2, MetricSlot.InstanceCpu, id));
        }

        if (instanceOwners.Count > 0)
        {
            // Memory on EC2 exists only when the CloudWatch agent publishes it; discover its exact dimension sets.
            var agentMetrics = await GetAgentMemoryMetricsAsync(target, ct);
            foreach (var descriptor in agentMetrics)
            {
                if (!descriptor.Dimensions.TryGetValue("InstanceId", out var id) || !instanceOwners.TryGetValue(id, out var owners))
                    continue;
                foreach (var (owner, slot) in owners)
                    if (!bindings.Values.Any(b => b.Slot == slot && b.InstanceId == id && ReferenceEquals(b.Resource, owner)))
                        Add(descriptor.Namespace, descriptor.MetricName, descriptor.Dimensions, new Binding(owner, slot, id));
            }
        }

        return (queries, bindings);
    }

    private async Task<IReadOnlyList<MetricDescriptor>> GetAgentMemoryMetricsAsync(Target target, CancellationToken ct)
    {
        if (_agentMetrics.TryGetValue(target.Id, out var cached) && DateTime.UtcNow - cached.At < AgentMetricsCacheTtl)
            return cached.Metrics;
        try
        {
            var metrics = await _gateway.ListMetricsAsync(target, "CWAgent", "mem_used_percent", ct);
            _agentMetrics[target.Id] = (DateTime.UtcNow, metrics);
            return metrics;
        }
        catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
        {
            return [];
        }
    }

    private static void ApplyMetrics(Target target, TargetHealth health, Dictionary<string, Binding> bindings,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> data, AppSettings settings, DateTime now, bool haveData)
    {
        if (!haveData)
            return;

        foreach (var (id, binding) in bindings)
        {
            var points = data.GetValueOrDefault(id) ?? [];
            if (binding.Resource is RdsInstanceStatus db)
            {
                ApplyRdsMetric(db, binding.Slot, points, HealthRules.ResolveThresholds(settings, target, db), settings, now);
                continue;
            }
            if (binding.Resource is CacheStatus cacheStatus)
            {
                ApplyCacheMetric(cacheStatus, binding, points, HealthRules.ResolveThresholds(settings, target, cacheStatus), settings, now);
                continue;
            }
            if (binding.Resource is LoadBalancerStatus lb)
            {
                double? Sum() => points.Count == 0 ? 0 : points.Sum(p => p.Value);
                switch (binding.Slot)
                {
                    case MetricSlot.LbRequests: lb.RequestCount = Sum(); break;
                    case MetricSlot.LbElb5xx: lb.Elb5xxCount = Sum(); break;
                    case MetricSlot.LbTarget5xx: lb.Target5xxCount = Sum(); break;
                    case MetricSlot.LbActiveFlows: lb.ActiveFlows = Last(points); break;
                }
                continue;
            }
            if (binding.Resource is Ec2InstanceStatus ec2)
            {
                var limits = HealthRules.ResolveThresholds(settings, target, ec2);
                if (binding.Slot == MetricSlot.InstanceCpu)
                    ec2.Cpu = HealthRules.EvaluateMetric("CPU", points, limits.CpuWarn, limits.CpuCritical, settings.SustainedMinutes, now);
                else
                    ec2.Memory = HealthRules.EvaluateMetric("Memory", points, limits.MemWarn, limits.MemCritical, settings.SustainedMinutes, now);
                continue;
            }
            var th = HealthRules.ResolveThresholds(settings, target, binding.Resource);
            var isCpu = binding.Slot is MetricSlot.EcsCpu or MetricSlot.Ec2Cpu;
            var eval = HealthRules.EvaluateMetric(isCpu ? "CPU" : "Memory", points,
                isCpu ? th.CpuWarn : th.MemWarn, isCpu ? th.CpuCritical : th.MemCritical, settings.SustainedMinutes, now);

            switch (binding.Slot)
            {
                case MetricSlot.EcsCpu: binding.Resource.Cpu = eval; break;
                case MetricSlot.EcsMemory: binding.Resource.Memory = eval; break;
                case MetricSlot.Ec2Cpu or MetricSlot.Ec2Memory:
                    var eb = (EbEnvironmentStatus)binding.Resource;
                    var inst = eb.Instances.FirstOrDefault(i => i.InstanceId == binding.InstanceId);
                    if (inst is null)
                        eb.Instances.Add(inst = new InstanceMetric { InstanceId = binding.InstanceId! });
                    if (binding.Slot == MetricSlot.Ec2Cpu) inst.Cpu = eval; else inst.Memory = eval;
                    break;
            }
        }

        foreach (var eb in health.Eb)
        {
            eb.Cpu = Worst(eb.Instances.Select(i => i.Cpu));
            eb.Memory = Worst(eb.Instances.Select(i => i.Memory));
        }
        foreach (var cache in health.Caches)
        {
            cache.Cpu = Worst(cache.NodeMetrics.Select(n => n.Cpu));
            cache.Memory = Worst(cache.NodeMetrics.Select(n => n.Memory));
        }
    }

    private static double? Last(IReadOnlyList<MetricPoint> points) => points.Count == 0 ? null : points.MaxBy(p => p.Timestamp)!.Value;

    private static void ApplyRdsMetric(RdsInstanceStatus db, MetricSlot slot, IReadOnlyList<MetricPoint> points, ThresholdSettings th, AppSettings settings, DateTime now)
    {
        switch (slot)
        {
            case MetricSlot.RdsCpu:
                db.Cpu = HealthRules.EvaluateMetric("CPU", points, th.CpuWarn, th.CpuCritical, settings.SustainedMinutes, now);
                break;
            case MetricSlot.RdsConnections:
                db.Connections = HealthRules.EvaluateMetric("Connections", points, double.MaxValue, double.MaxValue, settings.SustainedMinutes, now);
                db.Memory = db.Snapshot.MaxConnections is { } max and > 0
                    ? HealthRules.EvaluateMetric("Connections", points.Select(p => p with { Value = p.Value / max * 100 }).ToList(), th.MemWarn, th.MemCritical, settings.SustainedMinutes, now)
                    : null;
                break;
            case MetricSlot.RdsFreeStorage:
                db.FreeStorageBytes = Last(points);
                if (db.Snapshot.AllocatedStorageGb is { } gb and > 0)
                {
                    var allocated = gb * (double)(1L << 30);
                    var (storageWarn, storageCritical) = HealthRules.ResolveStorageThresholds(settings, db.ResourceKey);
                    db.StorageUsed = HealthRules.EvaluateMetric("Storage", points.Select(p => p with { Value = Math.Max(0, 100 - p.Value / allocated * 100) }).ToList(),
                        storageWarn, storageCritical, settings.SustainedMinutes, now);
                }
                break;
            case MetricSlot.RdsFreeableMemory:
                db.FreeableMemoryBytes = Last(points);
                break;
            case MetricSlot.RdsReplicaLag:
                // AuroraReplicaLag is in milliseconds, ReplicaLag in seconds.
                db.ReplicaLagSeconds = Last(points) is { } lag ? (db.Snapshot.IsAurora ? lag / 1000 : lag) : null;
                break;
        }
    }

    private static void ApplyCacheMetric(CacheStatus cache, Binding binding, IReadOnlyList<MetricPoint> points, ThresholdSettings th, AppSettings settings, DateTime now)
    {
        var parts = (binding.InstanceId ?? "|").Split('|');
        var node = cache.NodeMetrics.FirstOrDefault(n => n.ClusterId == parts[0] && n.NodeId == parts[1]);
        if (node is null)
            cache.NodeMetrics.Add(node = new CacheNodeMetric { ClusterId = parts[0], NodeId = parts[1] });
        switch (binding.Slot)
        {
            case MetricSlot.CacheCpu:
                node.Cpu = HealthRules.EvaluateMetric("CPU", points, th.CpuWarn, th.CpuCritical, settings.SustainedMinutes, now);
                break;
            case MetricSlot.CacheMemory:
                node.Memory = HealthRules.EvaluateMetric("Memory", points, th.MemWarn, th.MemCritical, settings.SustainedMinutes, now);
                break;
            case MetricSlot.CacheConnections:
                node.Connections = Last(points);
                break;
            case MetricSlot.CacheEvictions:
                node.Evictions = points.Count == 0 ? null : points.Sum(p => p.Value);
                break;
            case MetricSlot.CacheHitRate:
                node.HitRate = Last(points);
                break;
            case MetricSlot.CacheReplicationLag:
                node.ReplicationLagSeconds = Last(points);
                break;
        }
    }

    private static void ApplyAlarms(TargetHealth health, List<AlarmInfo> alarms)
    {
        var matched = new HashSet<string>();
        foreach (var ecs in health.Ecs)
        {
            ecs.Alarms = alarms.Where(a => HealthRules.AlarmMatchesEcs(a, ecs.Snapshot)).ToList();
            matched.UnionWith(ecs.Alarms.Select(a => a.Name));
        }
        foreach (var eb in health.Eb)
        {
            eb.Alarms = alarms.Where(a => HealthRules.AlarmMatchesEb(a, eb.Snapshot)).ToList();
            matched.UnionWith(eb.Alarms.Select(a => a.Name));
        }
        foreach (var db in health.Rds)
        {
            db.Alarms = alarms.Where(a => HealthRules.AlarmMatchesRds(a, db.Snapshot)).ToList();
            matched.UnionWith(db.Alarms.Select(a => a.Name));
        }
        foreach (var cluster in health.RdsClusters)
        {
            cluster.Alarms = alarms.Where(a => HealthRules.AlarmMatchesRdsCluster(a, cluster.Snapshot)).ToList();
            matched.UnionWith(cluster.Alarms.Select(a => a.Name));
        }
        foreach (var cache in health.Caches)
        {
            cache.Alarms = alarms.Where(a => HealthRules.AlarmMatchesCache(a, cache.Snapshot)).ToList();
            matched.UnionWith(cache.Alarms.Select(a => a.Name));
        }
        foreach (var lb in health.LoadBalancers)
        {
            lb.Alarms = alarms.Where(a => HealthRules.AlarmMatchesLoadBalancer(a, lb.Snapshot)).ToList();
            matched.UnionWith(lb.Alarms.Select(a => a.Name));
        }
        // EB nodes listed here too keep their alarms on the environment, so they are not counted twice.
        var ebAlarms = health.Eb.SelectMany(e => e.Alarms).Select(a => a.Name).ToHashSet();
        foreach (var ec2 in health.Ec2)
        {
            ec2.Alarms = alarms.Where(a => !ebAlarms.Contains(a.Name) && HealthRules.AlarmMatchesEc2(a, ec2.Snapshot)).ToList();
            matched.UnionWith(ec2.Alarms.Select(a => a.Name));
        }
        health.OtherAlarms = alarms.Where(a => !matched.Contains(a.Name)).ToList();
    }

    private void RecomputeAll(Target target, TargetHealth health)
    {
        var settings = _settings.Settings;
        HealthRules.ApplySuppression(health, target.Id, settings);
        var now = DateTime.UtcNow;
        foreach (var eb in health.Eb)
            HealthRules.ApplyCauseSuppression(eb, target.Id, settings, now);
        foreach (var lb in health.LoadBalancers)
            HealthRules.ApplyCauseSuppression(lb, target.Id, settings);
        foreach (var r in health.AllResources)
            r.IsHidden = settings.HiddenResources.Contains(r.ResourceKey);
        foreach (var r in health.AllResources)
            HealthRules.Recompute(r, HealthRules.ResolveThresholds(settings, target, r), settings.SustainedMinutes, HealthRules.ResolveStorageThresholds(settings, r.ResourceKey));
    }

    private void Commit(Target target, TargetHealth health, bool notify = true)
    {
        _state[target.Id] = health;
        try
        {
            _store.Save(target.Id, health);
        }
        catch
        {
            // Persisting a snapshot is best effort.
        }
        NotifyTransitions(target, health, notify);
        Changed?.Invoke();
    }

    /// <summary>Tracks what was last seen per resource; emits toasts only for changes when <paramref name="emit"/> is set.</summary>
    private void NotifyTransitions(Target target, TargetHealth health, bool emit)
    {
        var messages = new List<(string Title, string Message)>();
        var alarmNotifications = new HashSet<string>();

        foreach (var r in health.AllResources)
        {
            var active = r.Alarms.Where(a => a.CountsAsProblem).Select(a => a.Name).ToHashSet();
            var had = _notified.TryGetValue(r.ResourceKey, out var prev);
            _notified[r.ResourceKey] = new NotifiedState(r.Level, active);

            // Hidden resources are tracked (so unhiding does not replay old news) but never notify.
            if (r.IsHidden)
                continue;
            var prevLevel = had ? prev!.Level : HealthLevel.Ok;
            var wasProblem = prevLevel >= HealthLevel.Warn;
            var isProblem = r.Level >= HealthLevel.Warn;

            if (isProblem && (!wasProblem || r.Level > prevLevel))
                messages.Add(($"{target.DisplayName}: {r.DisplayName}", $"{(r.Level == HealthLevel.Critical ? "Critical" : "Warning")}: {r.ReasonText}"));
            else if (!isProblem && wasProblem && r.Level == HealthLevel.Ok)
                messages.Add(($"{target.DisplayName}: {r.DisplayName}", "Recovered — back to OK"));

            foreach (var name in active)
                if (prev is null || !prev.ActiveAlarms.Contains(name))
                    alarmNotifications.Add(name);
        }

        foreach (var alarm in health.OtherAlarms.Where(a => a.CountsAsProblem))
        {
            var key = $"{target.Id}:alarm:{alarm.Name}";
            if (!_notified.ContainsKey(key))
                alarmNotifications.Add(alarm.Name);
            _notified[key] = new NotifiedState(HealthLevel.Critical, []);
        }
        foreach (var key in _notified.Keys.Where(k => k.StartsWith($"{target.Id}:alarm:", StringComparison.Ordinal)).ToList())
            if (!health.OtherAlarms.Any(a => a.CountsAsProblem && key.EndsWith(":" + a.Name, StringComparison.Ordinal)))
                _notified.TryRemove(key, out _);

        // Alarm names already covered by a resource message are not repeated.
        foreach (var name in alarmNotifications.Where(n => !messages.Any(m => m.Message.Contains(n, StringComparison.Ordinal))))
            messages.Add(($"{target.DisplayName}: CloudWatch alarm", $"{name} is in ALARM"));

        if (!emit)
            return;
        if (messages.Count > 3)
            _notifier.Notify($"{target.DisplayName}: {messages.Count} health changes", string.Join("\n", messages.Take(3).Select(m => m.Title)), "Open the dashboard for details");
        else
            foreach (var (title, message) in messages)
                _notifier.Notify(title, message);
    }

    private static MetricEvaluation? Worst(IEnumerable<MetricEvaluation?> evaluations) =>
        evaluations.Where(e => e is not null && e.Current is not null)
            .OrderByDescending(e => e!.Level)
            .ThenByDescending(e => e!.Current)
            .FirstOrDefault();
}
