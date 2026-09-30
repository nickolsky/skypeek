using System.Text.RegularExpressions;
using Skypeek.Core.Models;

namespace Skypeek.Core.Health;

/// <summary>Pure evaluation rules; no I/O so they are easy to unit test.</summary>
public static partial class HealthRules
{
    public static readonly TimeSpan StuckDeploymentAfter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan RecentErrorWindow = TimeSpan.FromMinutes(30);

    [GeneratedRegex(@"failed to deploy|deployment failed|completed, but with errors|failed to|command failed|unsuccessful command|instance deployment failed", RegexOptions.IgnoreCase)]
    private static partial Regex DeployFailurePattern();

    [GeneratedRegex(@"environment update completed successfully|successfully deployed|update completed successfully|environment health has transitioned from \w+ to ok", RegexOptions.IgnoreCase)]
    private static partial Regex DeploySuccessPattern();

    public static HealthLevel Max(HealthLevel a, HealthLevel b) => (HealthLevel)Math.Max((int)a, (int)b);

    /// <param name="isCauseSuppressed">
    /// Causes the user marked as false positives. When every cause of a Red/Yellow status is suppressed, the status
    /// itself no longer counts (deploy failures, error events, metrics and alarms still do).
    /// </param>
    public static (HealthLevel Level, List<string> Reasons) EvaluateEb(EbEnvironmentSnapshot env, IReadOnlyList<EbEvent> recentEvents, DateTime nowUtc,
        Func<string, bool>? isCauseSuppressed = null)
    {
        var level = HealthLevel.Ok;
        var reasons = new List<string>();
        var activeCauses = env.Causes.Where(c => isCauseSuppressed?.Invoke(c) != true).ToList();
        var allCausesSuppressed = env.Causes.Count > 0 && activeCauses.Count == 0;

        if (!allCausesSuppressed)
        {
            switch (env.Health)
            {
                case "Red":
                    level = HealthLevel.Critical;
                    reasons.Add($"Health Red{Suffix(env.HealthStatus)}");
                    break;
                case "Yellow":
                    level = HealthLevel.Warn;
                    reasons.Add($"Health Yellow{Suffix(env.HealthStatus)}");
                    break;
            }

            switch (env.HealthStatus)
            {
                case "Severe" or "Degraded" when level < HealthLevel.Critical:
                    level = HealthLevel.Critical;
                    reasons.Add($"Health status {env.HealthStatus}");
                    break;
                case "Warning" when level < HealthLevel.Warn:
                    level = HealthLevel.Warn;
                    reasons.Add("Health status Warning");
                    break;
            }
        }

        // Enhanced health says why (e.g. "5xx on 30% of requests"); show the first causes right in the reasons.
        if (level >= HealthLevel.Warn)
            foreach (var cause in activeCauses.Take(2))
                reasons.Add(cause);

        // The latest deploy outcome wins: a failure after the last success means the environment is broken.
        foreach (var ev in recentEvents.OrderByDescending(e => e.Date))
        {
            if (DeploySuccessPattern().IsMatch(ev.Message))
                break;
            if (ev.Severity is "ERROR" or "FATAL" && DeployFailurePattern().IsMatch(ev.Message))
            {
                level = HealthLevel.Critical;
                reasons.Add($"Deploy failed at {ev.Date.ToLocalTime():g}: {Truncate(ev.Message, 160)}");
                break;
            }
        }

        var recentErrors = recentEvents.Count(e => e.Severity is "ERROR" or "FATAL" && nowUtc - e.Date <= RecentErrorWindow);
        if (recentErrors > 0 && level < HealthLevel.Critical)
        {
            level = Max(level, HealthLevel.Warn);
            reasons.Add($"{recentErrors} error event(s) in the last {RecentErrorWindow.TotalMinutes:0} min");
        }

        return (level, reasons);
    }

    public static (HealthLevel Level, List<string> Reasons) EvaluateEcs(EcsServiceSnapshot service, DateTime nowUtc)
    {
        var reasons = new List<string>();
        if (!string.Equals(service.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return (HealthLevel.Ok, reasons);

        var level = HealthLevel.Ok;
        var primary = service.Deployments.FirstOrDefault(d => d.Status == "PRIMARY");

        if (primary?.RolloutState == "FAILED")
        {
            level = HealthLevel.Critical;
            reasons.Add($"Deployment failed{Suffix(primary.RolloutStateReason)}");
        }

        var youngRollout = false;
        if (primary?.RolloutState == "IN_PROGRESS" && primary.CreatedAt is { } created)
        {
            var age = nowUtc - created;
            if (age > StuckDeploymentAfter)
            {
                level = Max(level, HealthLevel.Warn);
                reasons.Add($"Deployment in progress for {age.TotalMinutes:0} min");
            }
            else
            {
                youngRollout = true;
            }
        }

        if (service.Desired > 0 && service.Running == 0)
        {
            level = HealthLevel.Critical;
            reasons.Add($"0/{service.Desired} tasks running");
        }
        else if (service.Running < service.Desired && !youngRollout)
        {
            level = Max(level, HealthLevel.Warn);
            reasons.Add($"{service.Running}/{service.Desired} tasks running");
        }

        return (level, reasons);
    }

    public static MetricEvaluation EvaluateMetric(string metricName, IReadOnlyList<MetricPoint> points, double warn, double critical, int sustainedMinutes, DateTime nowUtc)
    {
        var sorted = points.OrderBy(p => p.Timestamp).ToList();
        if (sorted.Count == 0)
            return new MetricEvaluation { MetricName = metricName, Level = HealthLevel.Unknown };

        var period = InferPeriodSeconds(sorted);
        var required = Math.Max(1, (int)Math.Ceiling(sustainedMinutes * 60.0 / period));
        var last = sorted[^1];

        var level = HealthLevel.Ok;
        var stale = nowUtc - last.Timestamp > TimeSpan.FromSeconds(period * 2 + 600);
        if (stale)
        {
            level = HealthLevel.Unknown;
        }
        else if (sorted.Count >= required)
        {
            var window = sorted.TakeLast(required).ToList();
            if (window.All(p => p.Value >= critical))
                level = HealthLevel.Critical;
            else if (window.All(p => p.Value >= warn))
                level = HealthLevel.Warn;
        }

        return new MetricEvaluation
        {
            MetricName = metricName,
            Current = last.Value,
            Average = sorted.Average(p => p.Value),
            Peak = sorted.Max(p => p.Value),
            PeriodSeconds = period,
            Level = level,
            Points = sorted,
        };
    }

    /// <summary>EC2 basic monitoring publishes every 5 minutes, detailed/ECS every minute. Infer from point spacing.</summary>
    public static int InferPeriodSeconds(IReadOnlyList<MetricPoint> sorted)
    {
        if (sorted.Count < 2)
            return 60;
        var gaps = sorted.Zip(sorted.Skip(1), (a, b) => (b.Timestamp - a.Timestamp).TotalSeconds).OrderBy(g => g).ToList();
        var median = gaps[gaps.Count / 2];
        return Math.Max(60, (int)(Math.Round(median / 60.0) * 60));
    }

    public static ThresholdSettings ResolveThresholds(AppSettings settings, Target target, string resourceKey, bool isEcs)
    {
        if (settings.ResourceThresholds.TryGetValue(resourceKey, out var perResource))
            return perResource;
        return (isEcs ? target.EcsThresholds : target.EbThresholds) ?? (isEcs ? settings.EcsThresholds : settings.EbThresholds);
    }

    /// <summary>Per-resource override, then the target's (EB/ECS only), then the global thresholds for the resource's kind.</summary>
    public static ThresholdSettings ResolveThresholds(AppSettings settings, Target target, ResourceStatus resource) => resource switch
    {
        EcsServiceStatus => ResolveThresholds(settings, target, resource.ResourceKey, isEcs: true),
        EbEnvironmentStatus => ResolveThresholds(settings, target, resource.ResourceKey, isEcs: false),
        _ when settings.ResourceThresholds.TryGetValue(resource.ResourceKey, out var perResource) => perResource,
        RdsInstanceStatus or RdsClusterStatus => settings.RdsThresholds,
        Ec2InstanceStatus => settings.Ec2Thresholds,
        RedshiftStatus => settings.RdsThresholds,
        CacheStatus => settings.CacheThresholds,
        _ => ThresholdSettings.Default,
    };

    /// <summary>RDS used-storage thresholds: the resource's override when it sets them, else the global ones.</summary>
    public static (double Warn, double Critical) ResolveStorageThresholds(AppSettings settings, string resourceKey) =>
        settings.ResourceThresholds.TryGetValue(resourceKey, out var perResource) && perResource.StorageWarn is { } warn && perResource.StorageCritical is { } critical
            ? (warn, critical)
            : (settings.RdsStorageWarn, settings.RdsStorageCritical);

    public static bool IsCpuOrMemoryMetric(string? metricName) =>
        metricName is not null &&
        (metricName.Contains("cpu", StringComparison.OrdinalIgnoreCase) || metricName.Contains("mem", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Alarms worth showing: CPU/memory anywhere, every alarm on an RDS, ElastiCache or load balancer metric (storage,
    /// connections, lag, 5xx, unhealthy hosts, …) and EC2 status-check alarms.
    /// </summary>
    public static bool IsRelevantAlarm(AlarmInfo alarm) =>
        IsCpuOrMemoryMetric(alarm.MetricName)
        || alarm.Namespace is "AWS/RDS" or "AWS/ElastiCache" or "AWS/ApplicationELB" or "AWS/NetworkELB" or "AWS/GatewayELB"
            or "AWS/VPN" or "AWS/CodeBuild" or "AWS/Redshift"
        || (alarm.Namespace == "AWS/EC2" && alarm.MetricName?.StartsWith("StatusCheckFailed", StringComparison.Ordinal) == true);

    public static bool AlarmMatchesEc2(AlarmInfo alarm, Ec2InstanceSnapshot instance) =>
        alarm.Dimensions.TryGetValue("InstanceId", out var id) && id == instance.InstanceId;

    /// <summary>Load balancer alarms use the ARN suffixes: LoadBalancer=app/name/id, TargetGroup=targetgroup/name/id.</summary>
    public static bool AlarmMatchesLoadBalancer(AlarmInfo alarm, LoadBalancerSnapshot lb) =>
        alarm.Namespace == lb.Namespace &&
        ((alarm.Dimensions.TryGetValue("LoadBalancer", out var name) && name == lb.ArnSuffix) ||
         (alarm.Dimensions.TryGetValue("TargetGroup", out var group) && lb.TargetGroups.Any(t => t.ArnSuffix == group)));

    public static bool AlarmMatchesRds(AlarmInfo alarm, RdsInstanceSnapshot db) =>
        alarm.Namespace == "AWS/RDS" && alarm.Dimensions.TryGetValue("DBInstanceIdentifier", out var id) && id == db.Identifier;

    public static bool AlarmMatchesRdsCluster(AlarmInfo alarm, RdsClusterSnapshot cluster) =>
        alarm.Namespace == "AWS/RDS" && alarm.Dimensions.TryGetValue("DBClusterIdentifier", out var id) && id == cluster.Identifier;

    public static bool AlarmMatchesCache(AlarmInfo alarm, CacheSnapshot cache) =>
        alarm.Namespace == "AWS/ElastiCache" &&
        ((alarm.Dimensions.TryGetValue("ReplicationGroupId", out var group) && group == cache.Id) ||
         (alarm.Dimensions.TryGetValue("CacheClusterId", out var cluster) && (cluster == cache.Id || cache.Nodes.Any(n => n.ClusterId == cluster))) ||
         (alarm.Dimensions.TryGetValue("clusterId", out var serverless) && serverless == cache.Id));

    // ---------------- RDS / ElastiCache ----------------

    private static readonly HashSet<string> RdsCriticalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "storage-full", "incompatible-network", "incompatible-option-group", "incompatible-parameters", "incompatible-restore",
        "incompatible-credentials", "inaccessible-encryption-credentials", "inaccessible-encryption-credentials-recoverable", "restore-error",
        "insufficient-capacity", "stopped-with-errors",
    };

    /// <summary>Routine states that need no attention (nightly backups, log export changes, a deliberately stopped database, …).</summary>
    private static readonly HashSet<string> RdsQuietStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "available", "backing-up", "storage-optimization", "configuring-log-exports", "configuring-enhanced-monitoring",
        "configuring-iam-database-auth", "configuring-activity-stream", "stopped",
    };

    public static (HealthLevel Level, List<string> Reasons) EvaluateRdsStatus(string status, IReadOnlyList<ServiceEvent> events, DateTime nowUtc)
    {
        var level = HealthLevel.Ok;
        var reasons = new List<string>();
        if (RdsCriticalStates.Contains(status))
        {
            level = HealthLevel.Critical;
            reasons.Add($"Status {status}");
        }
        else if (!RdsQuietStates.Contains(status))
        {
            level = HealthLevel.Warn;
            reasons.Add($"Status {status}");
        }
        AddRecentFailures(events, nowUtc, ref level, reasons);
        return (level, reasons);
    }

    public static (HealthLevel Level, List<string> Reasons) EvaluateRdsInstance(RdsInstanceSnapshot db, IReadOnlyList<ServiceEvent> events, DateTime nowUtc)
    {
        var (level, reasons) = EvaluateRdsStatus(db.Status, events, nowUtc);
        if (!db.ReplicationNormal)
        {
            level = HealthLevel.Critical;
            reasons.Add($"Replication {db.ReplicationState ?? "not normal"}");
        }
        return (level, reasons);
    }

    private static readonly HashSet<string> CacheCriticalStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "create-failed", "incompatible-network", "restore-failed", "failed",
    };

    public static (HealthLevel Level, List<string> Reasons) EvaluateCache(CacheSnapshot cache, IReadOnlyList<ServiceEvent> events, DateTime nowUtc)
    {
        var level = HealthLevel.Ok;
        var reasons = new List<string>();
        if (CacheCriticalStates.Contains(cache.Status))
        {
            level = HealthLevel.Critical;
            reasons.Add($"Status {cache.Status}");
        }
        else if (!cache.Status.Equals("available", StringComparison.OrdinalIgnoreCase) && !cache.Status.Equals("snapshotting", StringComparison.OrdinalIgnoreCase))
        {
            level = HealthLevel.Warn;
            reasons.Add($"Status {cache.Status}");
        }

        var badNodes = cache.Nodes.Where(n => !n.Status.Equals("available", StringComparison.OrdinalIgnoreCase)).ToList();
        if (badNodes.Count > 0 && level < HealthLevel.Critical)
        {
            level = Max(level, HealthLevel.Warn);
            reasons.Add(string.Join(", ", badNodes.Take(3).Select(n => $"node {n.ClusterId} {n.Status}")));
        }
        // Roles are only reported with cluster mode disabled; without them there is nothing to check.
        foreach (var shard in cache.Shards.Where(s => cache.Kind == CacheKind.ReplicationGroup && s.Nodes.Any(n => n.Role is not null) && !s.Nodes.Any(n => n.Role == "primary")))
        {
            level = HealthLevel.Critical;
            reasons.Add($"Shard {shard.Id} has no primary");
        }
        AddRecentFailures(events, nowUtc, ref level, reasons);
        return (level, reasons);
    }

    // ---------------- EC2 / load balancers ----------------

    /// <summary>
    /// A running instance with a failed status check is critical; scheduled maintenance is a warning. A stopped instance
    /// was stopped on purpose (or by its owner) and is not a problem.
    /// </summary>
    public static (HealthLevel Level, List<string> Reasons) EvaluateEc2(Ec2InstanceSnapshot instance)
    {
        var level = HealthLevel.Ok;
        var reasons = new List<string>();
        if (!instance.IsRunning)
            return (instance.State is "stopped" or "pending" or "stopping" or "shutting-down" ? HealthLevel.Ok : HealthLevel.Unknown, reasons);

        if (instance.SystemStatus == "impaired")
        {
            level = HealthLevel.Critical;
            reasons.Add("System status check failed (AWS hardware or network)");
        }
        if (instance.InstanceStatus == "impaired")
        {
            level = HealthLevel.Critical;
            reasons.Add("Instance status check failed (OS not reachable)");
        }
        if (instance.ScheduledEvents.Count > 0)
        {
            level = Max(level, HealthLevel.Warn);
            reasons.Add($"Scheduled: {instance.ScheduledEvents[0]}");
        }
        return (level, reasons);
    }

    /// <summary>
    /// Target group problems of a load balancer as stable texts (so they can be suppressed like EB causes), with their
    /// level: some targets unhealthy is a warning, none healthy is critical.
    /// </summary>
    public static IReadOnlyList<(string Cause, HealthLevel Level, string Detail)> LoadBalancerCauses(LoadBalancerSnapshot lb)
    {
        var causes = new List<(string, HealthLevel, string)>();
        foreach (var group in lb.TargetGroups.OrderBy(g => g.Name))
        {
            if (group.Unhealthy == 0)
                continue;
            var detail = $"{group.HealthText}{(group.UnhealthyReasons is { Length: > 0 } why ? $" — {why}" : "")}";
            causes.Add(group.Healthy == 0
                ? ($"Target group {group.Name} has no healthy targets", HealthLevel.Critical, detail)
                : ($"Target group {group.Name} has unhealthy targets", HealthLevel.Warn, detail));
        }
        return causes;
    }

    public static (HealthLevel Level, List<string> Reasons) EvaluateLoadBalancer(LoadBalancerSnapshot lb, Func<string, bool>? isCauseSuppressed = null)
    {
        var level = HealthLevel.Ok;
        var reasons = new List<string>();
        switch (lb.State)
        {
            case "failed":
                level = HealthLevel.Critical;
                reasons.Add($"Load balancer failed{Suffix(lb.StateReason)}");
                break;
            case "active_impaired":
                level = HealthLevel.Warn;
                reasons.Add($"Load balancer impaired{Suffix(lb.StateReason)}");
                break;
        }
        foreach (var (cause, causeLevel, detail) in LoadBalancerCauses(lb))
        {
            if (isCauseSuppressed?.Invoke(cause) == true)
                continue;
            level = Max(level, causeLevel);
            reasons.Add($"{cause} ({detail})");
        }
        return (level, reasons);
    }

    /// <summary>Re-evaluates a load balancer's own health with the current cause suppressions.</summary>
    public static void ApplyCauseSuppression(LoadBalancerStatus lb, long targetId, AppSettings settings)
    {
        lb.CauseItems = LoadBalancerCauses(lb.Snapshot)
            .Select(c => MatchCause(c.Cause, targetId, lb.Snapshot.Name, settings) is { } rule
                ? new CauseItem(c.Cause, true, rule.Pattern)
                : new CauseItem(c.Cause, false, null))
            .ToList();
        var suppressed = lb.CauseItems.Where(i => i.Suppressed).Select(i => i.Text).ToHashSet();
        (lb.BaseLevel, lb.BaseReasons) = EvaluateLoadBalancer(lb.Snapshot, suppressed.Contains);
    }

    private static void AddRecentFailures(IReadOnlyList<ServiceEvent> events, DateTime nowUtc, ref HealthLevel level, List<string> reasons)
    {
        var failures = events.Where(e => e.IsFailure && nowUtc - e.Date <= RecentErrorWindow).OrderByDescending(e => e.Date).ToList();
        if (failures.Count == 0)
            return;
        level = Max(level, HealthLevel.Warn);
        reasons.Add($"{failures.Count} failure event(s) in the last {RecentErrorWindow.TotalMinutes:0} min: {Truncate(failures[0].Message, 120)}");
    }

    /// <summary>
    /// max_connections of an RDS instance: the parameter group value when it is a number or a formula we can evaluate,
    /// else the engine's default formula. Formulas use the instance class memory, so the result is an estimate.
    /// </summary>
    public static (int? Value, string? Source) EstimateMaxConnections(string engine, string instanceClass, string? parameterValue, double? serverlessMaxAcu)
    {
        var memory = InstanceClassMemoryBytes(instanceClass, serverlessMaxAcu);
        if (!string.IsNullOrWhiteSpace(parameterValue))
        {
            if (int.TryParse(parameterValue, out var explicitValue))
                return explicitValue > 0 ? (explicitValue, "parameter group") : (null, "parameter group: unlimited");
            if (memory is { } m && EvaluateFormula(parameterValue, m) is { } fromFormula)
                return ((int)fromFormula, $"≈ parameter group formula at {m / (1L << 30):0.#} GiB");
        }

        var formula = engine.ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => "{DBInstanceClassMemory/12582880}",
            "postgres" or "aurora-postgresql" => "LEAST({DBInstanceClassMemory/9531392},5000)",
            "aurora-mysql" or "aurora" => "GREATEST({log(DBInstanceClassMemory/805306368)*45},{log(DBInstanceClassMemory/8187281408)*1000})",
            var e when e.StartsWith("oracle", StringComparison.Ordinal) => "LEAST({DBInstanceClassMemory/9868951},20000)",
            _ => null,
        };
        if (formula is null || memory is null)
            return (null, null);
        var value = EvaluateFormula(formula, memory.Value);
        return value is null ? (null, null) : ((int)value, $"≈ engine default at {memory.Value / (1L << 30):0.#} GiB");
    }

    /// <summary>Approximate memory of an instance class (db.&lt;family&gt;.&lt;size&gt;), or of Serverless v2 at its maximum ACU.</summary>
    public static double? InstanceClassMemoryBytes(string instanceClass, double? serverlessMaxAcu)
    {
        const double GiB = 1L << 30;
        if (instanceClass.Equals("db.serverless", StringComparison.OrdinalIgnoreCase))
            return serverlessMaxAcu is { } acu ? acu * 2 * GiB : null;

        var parts = instanceClass.ToLowerInvariant().Split('.');
        if (parts.Length != 3)
            return null;
        var family = parts[1];
        var size = parts[2];
        double? baseGiB = size switch
        {
            "micro" => 1,
            "small" => 2,
            "medium" => 4,
            "large" => 8,
            "xlarge" => 16,
            _ when size.EndsWith("xlarge", StringComparison.Ordinal) && int.TryParse(size[..^"xlarge".Length], out var n) => n * 16.0,
            _ => null,
        };
        if (baseGiB is null)
            return null;
        // General purpose and burstable: 4 GiB per vCPU; memory optimized (r, z): 8; x2g: 16; x2ie*: 32.
        var factor = family switch
        {
            _ when family.StartsWith("x2ie", StringComparison.Ordinal) => 8.0,
            _ when family.StartsWith('x') => 4.0,
            _ when family.StartsWith('r') || family.StartsWith('z') => 2.0,
            _ => 1.0,
        };
        return baseGiB * factor * GiB;
    }

    [GeneratedRegex(@"^\{(log\()?DBInstanceClassMemory/(\d+)\)?(\*(\d+))?\}$", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryTerm();

    /// <summary>Evaluates RDS parameter formulas like LEAST({DBInstanceClassMemory/9531392},5000); null when not understood.</summary>
    public static double? EvaluateFormula(string formula, double memoryBytes)
    {
        var text = formula.Replace(" ", "");
        foreach (var (name, pick) in new (string, Func<double, double, double>)[] { ("LEAST(", Math.Min), ("GREATEST(", Math.Max) })
        {
            if (!text.StartsWith(name, StringComparison.OrdinalIgnoreCase) || !text.EndsWith(')'))
                continue;
            var inner = text[name.Length..^1];
            // Split on the comma that is not nested in braces or parentheses.
            var depth = 0;
            for (var i = 0; i < inner.Length; i++)
            {
                depth += inner[i] switch { '{' or '(' => 1, '}' or ')' => -1, _ => 0 };
                if (inner[i] == ',' && depth == 0)
                    return EvaluateFormula(inner[..i], memoryBytes) is { } a && EvaluateFormula(inner[(i + 1)..], memoryBytes) is { } b ? pick(a, b) : null;
            }
            return null;
        }
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number;
        var match = MemoryTerm().Match(text);
        if (!match.Success)
            return null;
        var value = memoryBytes / double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (match.Groups[1].Success)
            value = Math.Log2(value);
        if (match.Groups[4].Success)
            value *= double.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
        return Math.Max(0, Math.Floor(value));
    }

    public static bool AlarmMatchesEcs(AlarmInfo alarm, EcsServiceSnapshot service) =>
        alarm.Namespace is "AWS/ECS" or "ECS/ContainerInsights" &&
        alarm.Dimensions.TryGetValue("ClusterName", out var cluster) && cluster == service.ClusterName &&
        alarm.Dimensions.TryGetValue("ServiceName", out var name) && name == service.ServiceName;

    public static bool AlarmMatchesEb(AlarmInfo alarm, EbEnvironmentSnapshot env) =>
        (alarm.Dimensions.TryGetValue("InstanceId", out var instance) && env.InstanceIds.Contains(instance)) ||
        (alarm.Dimensions.TryGetValue("AutoScalingGroupName", out var asg) && env.AutoScalingGroups.Contains(asg)) ||
        (alarm.Dimensions.TryGetValue("EnvironmentName", out var envName) && envName == env.EnvironmentName);

    /// <summary>Combines base level, metric evaluations and alarms into the final level and reasons.</summary>
    public static void Recompute(ResourceStatus status, ThresholdSettings thresholds, int sustainedMinutes, (double Warn, double Critical)? storage = null)
    {
        var level = status.BaseLevel;
        var reasons = new List<string>(status.BaseReasons);

        void AddMetric(MetricEvaluation? eval, string label, double warn, double critical)
        {
            if (eval is null || eval.Level < HealthLevel.Warn)
                return;
            level = Max(level, eval.Level);
            var limit = eval.Level == HealthLevel.Critical ? critical : warn;
            reasons.Add($"{label} {eval.Current:0}% ≥ {limit:0}% for {sustainedMinutes} min ({(eval.Level == HealthLevel.Critical ? "critical" : "warning")})");
        }

        AddMetric(status.Cpu, "CPU", thresholds.CpuWarn, thresholds.CpuCritical);
        AddMetric(status.Memory, status.MemoryLabel, thresholds.MemWarn, thresholds.MemCritical);
        if (status is RdsInstanceStatus db && storage is { } st)
            AddMetric(db.StorageUsed, "Storage used", st.Warn, st.Critical);

        foreach (var alarm in status.Alarms.Where(a => a.CountsAsProblem))
        {
            level = HealthLevel.Critical;
            reasons.Add($"Alarm {alarm.Name} in ALARM");
        }

        if (level == HealthLevel.Unknown && (status.Cpu is not null || status.Memory is not null))
            level = HealthLevel.Ok;

        status.Level = level;
        status.Reasons = reasons;
    }

    /// <summary>The cap for a resource: its own override, else its target's.</summary>
    public static AlertCap ResolveAlertCap(AppSettings settings, Target target, string resourceKey) =>
        settings.ResourceAlertCaps.TryGetValue(resourceKey, out var cap) ? cap : target.AlertCap;

    /// <summary>Applies an alert cap after <see cref="Recompute"/>: "warning" lowers critical to warning.</summary>
    public static void ApplyAlertCap(ResourceStatus status, AlertCap cap)
    {
        status.Cap = cap;
        status.UncappedLevel = status.Level;
        if (cap == AlertCap.Warning && status.Level == HealthLevel.Critical)
            status.Level = HealthLevel.Warn;
    }

    public const string TargetTrackingPrefix = "TargetTracking-";

    /// <summary>Why an alarm is suppressed for this target, or null when it counts.</summary>
    public static string? SuppressionReason(AlarmInfo alarm, long targetId, AppSettings settings)
    {
        if (settings.IgnoreTargetTrackingAlarms && alarm.Name.StartsWith(TargetTrackingPrefix, StringComparison.Ordinal))
            return "auto-scaling target-tracking alarm";
        foreach (var rule in settings.SuppressedAlarms)
            if ((rule.TargetId is null || rule.TargetId == targetId) && WildcardMatch(rule.Pattern, alarm.Name))
                return $"suppressed by rule \"{rule.Pattern}\"";
        return null;
    }

    public static void ApplySuppression(TargetHealth health, long targetId, AppSettings settings)
    {
        foreach (var alarm in AllAlarms(health))
        {
            alarm.SuppressedReason = SuppressionReason(alarm, targetId, settings);
            alarm.Suppressed = alarm.SuppressedReason is not null;
        }
    }

    public static IEnumerable<AlarmInfo> AllAlarms(TargetHealth health) =>
        health.AllResources.SelectMany(r => r.Alarms).Concat(health.OtherAlarms);

    /// <summary>Collapses whitespace and line breaks so causes match regardless of formatting.</summary>
    public static string NormalizeCause(string cause) => Regex.Replace(cause, @"\s+", " ").Trim();

    public static CauseSuppression? MatchCause(string cause, long targetId, string environmentName, AppSettings settings, string? instance = null)
    {
        var text = NormalizeCause(cause);
        return settings.SuppressedCauses.FirstOrDefault(rule =>
            (rule.TargetId is null || rule.TargetId == targetId) &&
            (rule.EnvironmentName is null || rule.EnvironmentName == environmentName) &&
            (rule.OnlyFor is null || rule.OnlyFor == instance) &&
            WildcardMatch(NormalizeCause(rule.Pattern), text));
    }

    /// <summary>Re-evaluates an environment's own health with the current cause suppressions.</summary>
    public static void ApplyCauseSuppression(EbEnvironmentStatus eb, long targetId, AppSettings settings, DateTime nowUtc)
    {
        eb.CauseItems = eb.Snapshot.Causes
            .Select(c => MatchCause(c, targetId, eb.Snapshot.EnvironmentName, settings) is { } rule
                ? new CauseItem(c, true, rule.Pattern)
                : new CauseItem(c, false, null))
            .ToList();
        var suppressed = eb.CauseItems.Where(i => i.Suppressed).Select(i => i.Text).ToHashSet();
        (eb.BaseLevel, eb.BaseReasons) = EvaluateEb(eb.Snapshot, eb.RecentEvents, nowUtc, suppressed.Contains);
    }

    /// <summary>Case-insensitive full match with * (any run) and ? (one character).</summary>
    public static bool WildcardMatch(string pattern, string text)
    {
        var regex = "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public static decimal EstimateMonthlyMetricsCostUsd(int metricCount, int intervalMinutes)
    {
        if (metricCount <= 0 || intervalMinutes <= 0)
            return 0;
        var requestsPerMonth = 30m * 24 * 60 / intervalMinutes;
        return metricCount * requestsPerMonth / 1000m * 0.01m;
    }

    private static string Suffix(string? value) => string.IsNullOrWhiteSpace(value) ? "" : $" ({value})";

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
