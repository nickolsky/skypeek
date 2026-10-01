using System.Globalization;
using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

/// <summary>One on-demand price from the AWS Price List (per hour, per GB-month, …).</summary>
public sealed class PriceEntry
{
    public double? Usd { get; init; }
    public string? Unit { get; init; }
    public string? Description { get; init; }
    public DateTime FetchedUtc { get; init; }
    /// <summary>Several different prices matched; the lowest is used.</summary>
    public bool Ambiguous { get; init; }
}

/// <summary>A product returned by the Price List API, flattened to its on-demand price dimensions.</summary>
public sealed record PriceItem(string UsageType, string Unit, double Usd, string Description, IReadOnlyDictionary<string, string> Attributes);

/// <summary>What to ask the Price List API for one price key.</summary>
/// <param name="UsageType">Pick price dimensions with this usage type, with or without the region prefix (e.g. "NatGateway-Hours" matches "USE2-NatGateway-Hours").</param>
public sealed record PriceQuery(string Key, string ServiceCode, IReadOnlyDictionary<string, string> Filters, string? UsageType = null, string? Unit = null);

public sealed class ServiceCost
{
    public string Service { get; init; } = "";
    public double MonthToDate { get; init; }
    public double LastMonth { get; init; }
}

/// <summary>Billed cost from Cost Explorer for the target's account and region.</summary>
public sealed class ActualCosts
{
    public DateTime FetchedUtc { get; init; }
    public string Currency { get; init; } = "USD";
    public double MonthToDate { get; init; }
    public double LastMonth { get; init; }
    public bool MonthToDateEstimated { get; init; }
    public List<ServiceCost> Services { get; init; } = [];
    /// <summary>Resource id/ARN → cost of the last 14 days (needs resource-level data enabled in Billing).</summary>
    public Dictionary<string, double> ResourceLast14Days { get; init; } = new();
    /// <summary>Why resource-level costs are missing, if they are.</summary>
    public string? ResourceNote { get; init; }
}

/// <summary>Everything cost-related for one target, cached in the vault.</summary>
public sealed class CostSnapshot
{
    public long TargetId { get; init; }
    public Dictionary<string, PriceEntry> Prices { get; init; } = new();
    /// <summary>RDS engine major versions with billed Extended Support: "engine|major" → date Extended Support starts.</summary>
    public Dictionary<string, DateTime> ExtendedSupportStarts { get; init; } = new();
    /// <summary>Engines whose support dates were read, and when.</summary>
    public Dictionary<string, DateTime> EngineSupportFetchedUtc { get; init; } = new();
    public ActualCosts? Actual { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public string? Error { get; set; }
}

/// <summary>Estimated monthly on-demand cost of one resource.</summary>
/// <param name="Complete">False when some part could not be priced (shown as "at least").</param>
public sealed record CostEstimate(double MonthlyUsd, string Basis, bool Complete = true)
{
    public string Text => $"{(Complete ? "~" : "≥")}{CostRules.Money(MonthlyUsd)}/mo";
}

/// <summary>Pure pricing rules: which prices a resource needs and how its monthly estimate is computed.</summary>
public static class CostRules
{
    public const double HoursPerMonth = 730;

    public static string Money(double usd) => usd switch
    {
        >= 1000 => usd.ToString("$#,##0", CultureInfo.InvariantCulture),
        >= 10 => usd.ToString("$0", CultureInfo.InvariantCulture),
        _ => usd.ToString("$0.00", CultureInfo.InvariantCulture),
    };

    // ---------------- price keys ----------------

    /// <summary>EC2 "PlatformDetails" to the Price List operating system.</summary>
    public static string Ec2Os(string? platform) => platform switch
    {
        null => "Linux",
        _ when platform.Contains("Windows", StringComparison.OrdinalIgnoreCase) => "Windows",
        _ when platform.Contains("Red Hat", StringComparison.OrdinalIgnoreCase) => "RHEL",
        _ when platform.Contains("SUSE", StringComparison.OrdinalIgnoreCase) => "SUSE",
        _ when platform.Contains("Ubuntu Pro", StringComparison.OrdinalIgnoreCase) => "Ubuntu Pro",
        _ => "Linux",
    };

    public static string Ec2Key(string instanceType, string? platform) => $"ec2|{instanceType}|{Ec2Os(platform)}";

    /// <summary>RDS engine → Price List databaseEngine; null for engines with license variants we do not price.</summary>
    public static string? RdsEngine(string engine) => engine.ToLowerInvariant() switch
    {
        "postgres" => "PostgreSQL",
        "mysql" => "MySQL",
        "mariadb" => "MariaDB",
        "aurora-postgresql" => "Aurora PostgreSQL",
        "aurora-mysql" or "aurora" => "Aurora MySQL",
        _ => null,
    };

    /// <summary>How an RDS instance is deployed; Price List prices differ per deployment.</summary>
    public enum RdsDeployment { Single, Multi, Cluster }

    public static RdsDeployment Deployment(RdsInstanceSnapshot s) =>
        s.IsAurora ? RdsDeployment.Single : s.IsMultiAzClusterMember ? RdsDeployment.Cluster : s.MultiAz ? RdsDeployment.Multi : RdsDeployment.Single;

    private static string Text(RdsDeployment d) => d switch { RdsDeployment.Multi => "multi", RdsDeployment.Cluster => "cluster", _ => "single" };

    private static string DeploymentOption(string text) => text switch
    {
        "multi" => "Multi-AZ",
        "cluster" => "Multi-AZ (readable standbys)",
        _ => "Single-AZ",
    };

    /// <summary>Usage type prefix of provisioned IOPS / throughput per deployment ("RDS:Multi-AZ-GP3-PIOPS"…).</summary>
    private static string UsagePrefix(string deployment) => deployment switch { "multi" => "Multi-AZ-", "cluster" => "Multi-AZCluster-", _ => "" };

    public static string RdsKey(string instanceClass, string engine, RdsDeployment deployment, bool ioOptimized = false) =>
        $"rds|{instanceClass}|{engine}|{Text(deployment)}{(ioOptimized ? "|iopt" : "")}";
    public static string RdsStorageKey(string storageType, string engine, RdsDeployment deployment) => $"rdsstorage|{storageType}|{engine}|{Text(deployment)}";
    public static string RdsIopsKey(string storageType, string engine, RdsDeployment deployment) => $"rdsiops|{storageType}|{engine}|{Text(deployment)}";
    public static string RdsThroughputKey(string engine, RdsDeployment deployment) => $"rdstput|gp3|{engine}|{Text(deployment)}";

    /// <summary>
    /// Provisioned IOPS and throughput that are billed on top of storage: all of io1/io2, and gp3 above its baseline
    /// (3,000 IOPS / 125 MiB/s, or 12,000 / 500 from 400 GB for MySQL, MariaDB and PostgreSQL).
    /// </summary>
    public static (int Iops, int ThroughputMbps) BilledStoragePerformance(RdsInstanceSnapshot s)
    {
        switch (s.StorageType)
        {
            case "io1" or "io2":
                return (s.Iops ?? 0, 0);
            case "gp3":
                var large = s.AllocatedStorageGb >= 400 && s.Engine is "mysql" or "mariadb" or "postgres";
                return (Math.Max(0, (s.Iops ?? 0) - (large ? 12000 : 3000)), Math.Max(0, (s.StorageThroughputMbps ?? 0) - (large ? 500 : 125)));
            default:
                return (0, 0);
        }
    }

    /// <summary>"8.4.9" → the longest known major version it starts with ("8.4"); Aurora MySQL "8.0.mysql_aurora.3.05" → "8.0".</summary>
    public static string? MajorVersion(string? version, IEnumerable<string> majors) =>
        version is null ? null : majors.Where(m => version == m || version.StartsWith(m + ".", StringComparison.Ordinal)).MaxBy(m => m.Length);
    public static string CacheKey(string nodeType, string engine) => $"cache|{nodeType}|{engine}";
    public const string NatKey = "nat|hours";
    /// <summary>A public IPv4 address, in use or idle (AWS charges both the same).</summary>
    public const string Ipv4Key = "ipv4|hours";
    /// <summary>An interface VPC endpoint, per hour and zone.</summary>
    public const string EndpointKey = "vpce|hours";
    public const string VpnKey = "vpn|hours";
    public const string SecretKey = "secret|month";
    /// <summary>An Advanced-tier parameter, priced per hour.</summary>
    public const string AdvancedParameterKey = "ssmadv|hour";
    public static string LoadBalancerKey(string type) => $"elb|{type}";
    public const string FargateCpuKey = "fargate|vcpu";
    public const string FargateMemoryKey = "fargate|gb";

    /// <summary>The Price List query behind a key, for the target's region.</summary>
    public static PriceQuery? QueryFor(string key, string region)
    {
        var parts = key.Split('|');
        Dictionary<string, string> F(params (string, string)[] pairs)
        {
            var d = pairs.ToDictionary(p => p.Item1, p => p.Item2);
            d["regionCode"] = region;
            return d;
        }
        return parts[0] switch
        {
            "ec2" => new PriceQuery(key, "AmazonEC2", F(("instanceType", parts[1]), ("operatingSystem", parts[2]), ("tenancy", "Shared"),
                ("preInstalledSw", "NA"), ("capacitystatus", "Used"), ("licenseModel", "No License required")), Unit: "Hrs"),
            "rds" when RdsEngine(parts[2]) is { } engine => new PriceQuery(key, "AmazonRDS", F(("instanceType", parts[1]), ("databaseEngine", engine),
                ("deploymentOption", DeploymentOption(parts[3])), ("productFamily", "Database Instance")),
                UsageType: parts.Length > 4 && parts[4] == "iopt" ? $"InstanceUsageIOOptimized:{parts[1]}" : null, Unit: "Hrs"),
            "rdsstorage" when StorageVolumeType(parts[1]) is { } volume && RdsEngine(parts[2]) is { } storageEngine => new PriceQuery(key, "AmazonRDS", F(("productFamily", "Database Storage"),
                ("volumeType", volume), ("databaseEngine", storageEngine), ("deploymentOption", DeploymentOption(parts[3]))), Unit: "GB-Mo"),
            "rdsiops" when RdsEngine(parts[2]) is { } iopsEngine && parts[1] is "gp3" or "io1" or "io2" => new PriceQuery(key, "AmazonRDS",
                F(("productFamily", "Provisioned IOPS"), ("databaseEngine", iopsEngine), ("deploymentOption", DeploymentOption(parts[3]))),
                UsageType: $"RDS:{UsagePrefix(parts[3])}{parts[1] switch { "gp3" => "GP3-", "io2" => "IO2-", _ => "" }}PIOPS", Unit: "IOPS-Mo"),
            "rdstput" when RdsEngine(parts[2]) is { } tputEngine => new PriceQuery(key, "AmazonRDS",
                F(("productFamily", "Provisioned Throughput"), ("databaseEngine", tputEngine), ("deploymentOption", DeploymentOption(parts[3]))),
                UsageType: $"RDS:{UsagePrefix(parts[3])}GP3-Throughput", Unit: "MBPS-Mo"),
            "cache" => new PriceQuery(key, "AmazonElastiCache", F(("instanceType", parts[1]), ("cacheEngine", CacheEngine(parts[2])), ("productFamily", "Cache Instance")), Unit: "Hrs"),
            "nat" => new PriceQuery(key, "AmazonEC2", F(("productFamily", "NAT Gateway")), UsageType: "NatGateway-Hours", Unit: "Hrs"),
            "elb" => new PriceQuery(key, "AWSELB", F(("productFamily", parts[1] switch
                {
                    "network" => "Load Balancer-Network",
                    "gateway" => "Load Balancer-Gateway",
                    _ => "Load Balancer-Application",
                })), UsageType: "LoadBalancerUsage", Unit: "Hrs"),
            // These usage types carry a region prefix (USE1-…), so they are matched by "contains".
            "ipv4" => new PriceQuery(key, "AmazonVPC", F(("usagetype~", "PublicIPv4:InUseAddress")), UsageType: "PublicIPv4:InUseAddress", Unit: "Hrs"),
            "vpce" => new PriceQuery(key, "AmazonVPC", F(("usagetype~", "VpcEndpoint-Hours")), UsageType: "VpcEndpoint-Hours", Unit: "Hrs"),
            "vpn" => new PriceQuery(key, "AmazonVPC", F(("usagetype~", "VPN-Usage-Hours:ipsec.1")), UsageType: "VPN-Usage-Hours:ipsec.1", Unit: "Hrs"),
            "secret" => new PriceQuery(key, "AWSSecretsManager", F(("productFamily", "Secret")), UsageType: "AWSSecretsManager-Secrets", Unit: "Secrets"),
            "ssmadv" => new PriceQuery(key, "AWSSystemsManager", F(("usagetype~", "PS-Advanced-Param-Tier1")), UsageType: "PS-Advanced-Param-Tier1", Unit: "Hour"),
            "fargate" => new PriceQuery(key, "AmazonECS", F(("productFamily", "Compute")),
                UsageType: parts[1] == "vcpu" ? "Fargate-vCPU-Hours:perCPU" : "Fargate-GB-Hours", Unit: "hours"),
            _ => null,
        };
    }

    private static string? StorageVolumeType(string storageType) => storageType switch
    {
        "gp2" => "General Purpose",
        "gp3" => "General Purpose-GP3",
        "io1" => "Provisioned IOPS",
        "io2" => "Provisioned IOPS-IO2",
        "standard" => "Magnetic",
        _ => null,
    };

    private static string CacheEngine(string engine) => engine.ToLowerInvariant() switch
    {
        "memcached" => "Memcached",
        "valkey" => "Valkey",
        _ => "Redis",
    };

    /// <summary>The price keys the target's resources and NAT gateways need.</summary>
    public static IEnumerable<string> KeysFor(TargetHealth health, NetworkSnapshot? network)
    {
        foreach (var r in health.AllResources)
            foreach (var key in KeysFor(r))
                yield return key;
        if (network?.NatGateways.Count > 0)
            yield return NatKey;
        if (network is not null && (network.Interfaces.Any(i => i.PublicIps.Count > 0) || network.ElasticIps.Count > 0))
            yield return Ipv4Key;
        if (network?.Endpoints.Any(IsHourlyEndpoint) == true)
            yield return EndpointKey;
    }

    /// <summary>Interface and Gateway Load Balancer endpoints are billed per hour and zone; gateway endpoints (S3, DynamoDB) are free.</summary>
    public static bool IsHourlyEndpoint(VpcEndpointInfo e) => e.Type is "Interface" or "GatewayLoadBalancer" && e.State is null or "available";

    public static IEnumerable<string> KeysFor(ResourceStatus r)
    {
        switch (r)
        {
            case Ec2InstanceStatus { Snapshot.InstanceType: { } type } ec2:
                yield return Ec2Key(type, ec2.Snapshot.Platform);
                break;
            case EbEnvironmentStatus eb:
                foreach (var type in eb.Snapshot.InstanceHealth.Select(i => i.InstanceType).Where(t => t is not null).Distinct())
                    yield return Ec2Key(type!, null);
                break;
            case RdsInstanceStatus db:
            {
                var s = db.Snapshot;
                var deployment = Deployment(s);
                // A Multi-AZ DB cluster is priced once, on its writer (the price covers the two readable standbys).
                if (deployment == RdsDeployment.Cluster && s.IsClusterWriter != true)
                    break;
                if (s.InstanceClass != "db.serverless")
                    yield return RdsKey(s.InstanceClass, s.Engine, deployment, s.IsIoOptimized);
                if (!s.IsAurora && s.StorageType is { } storage)
                {
                    yield return RdsStorageKey(storage, s.Engine, deployment);
                    var (iops, throughput) = BilledStoragePerformance(s);
                    if (iops > 0)
                        yield return RdsIopsKey(storage, s.Engine, deployment);
                    if (throughput > 0)
                        yield return RdsThroughputKey(s.Engine, deployment);
                }
                break;
            }
            case CacheStatus cache when cache.Snapshot.Kind != CacheKind.Serverless && cache.Snapshot.NodeType is { } nodeType:
                yield return CacheKey(nodeType, cache.Snapshot.Engine);
                break;
            case VpnConnectionStatus:
                yield return VpnKey;
                break;
            case LoadBalancerStatus lb when lb.Snapshot.Type is "application" or "network" or "gateway":
                yield return LoadBalancerKey(lb.Snapshot.Type);
                break;
            case EcsServiceStatus ecs when IsFargate(ecs):
                yield return FargateCpuKey;
                yield return FargateMemoryKey;
                break;
        }
    }

    private static bool IsFargate(EcsServiceStatus ecs) =>
        ecs.Snapshot.LaunchType == "FARGATE" || ecs.Snapshot.Tasks.Any(t => t.LaunchType == "FARGATE");

    // ---------------- estimates ----------------

    /// <summary>Monthly on-demand estimate from list prices; null when the resource has no priceable part.</summary>
    public static CostEstimate? Estimate(ResourceStatus r, Func<string, PriceEntry?> price)
    {
        double? Hourly(string key) => price(key)?.Usd;
        switch (r)
        {
            case Ec2InstanceStatus ec2:
            {
                if (!ec2.Snapshot.IsRunning)
                    return new CostEstimate(0, $"{ec2.Snapshot.State}: no compute charge (EBS volumes are still billed)");
                if (ec2.Snapshot.InstanceType is not { } type || Hourly(Ec2Key(type, ec2.Snapshot.Platform)) is not { } hourly)
                    return null;
                return new CostEstimate(hourly * HoursPerMonth, $"{type} {Ec2Os(ec2.Snapshot.Platform)} on-demand {Rate(hourly)}/h × 730 h; EBS and data transfer not included");
            }
            case EbEnvironmentStatus eb:
            {
                var running = eb.Snapshot.InstanceHealth.Where(i => i.State is null or "running").ToList();
                if (running.Count == 0)
                    return null;
                double total = 0;
                var missing = 0;
                foreach (var group in running.GroupBy(i => i.InstanceType))
                {
                    if (group.Key is { } type && Hourly(Ec2Key(type, null)) is { } hourly)
                        total += hourly * HoursPerMonth * group.Count();
                    else
                        missing += group.Count();
                }
                var basis = string.Join(", ", running.GroupBy(i => i.InstanceType ?? "?").Select(g => $"{g.Count()}× {g.Key}"))
                            + " on-demand (Linux prices) × 730 h; load balancer listed separately; EBS and data transfer not included";
                return missing == running.Count ? null : new CostEstimate(total, basis, missing == 0);
            }
            case RdsInstanceStatus db:
            {
                var s = db.Snapshot;
                var deployment = Deployment(s);
                if (deployment == RdsDeployment.Cluster && s.IsClusterWriter != true)
                    return new CostEstimate(0, $"reader of Multi-AZ DB cluster {s.ClusterIdentifier}: included in the cluster price shown on its writer");
                var deploymentText = s.IsIoOptimized ? "Aurora I/O-Optimized" : deployment switch
                {
                    RdsDeployment.Multi => "Multi-AZ",
                    RdsDeployment.Cluster => "Multi-AZ DB cluster (writer + 2 readable standbys)",
                    _ => s.IsAurora ? "Aurora" : "Single-AZ",
                };
                double total = 0;
                var parts = new List<string>();
                var complete = true;
                var stopped = s.Status == "stopped";
                if (!stopped)
                {
                    if (s.InstanceClass != "db.serverless" && Hourly(RdsKey(s.InstanceClass, s.Engine, deployment, s.IsIoOptimized)) is { } hourly)
                    {
                        total += hourly * HoursPerMonth;
                        parts.Add($"{s.InstanceClass} {deploymentText} {Rate(hourly)}/h × 730 h");
                    }
                    else
                    {
                        complete = false;
                        parts.Add(s.InstanceClass == "db.serverless" ? "Serverless v2 capacity is usage-based (not estimated)" : $"no list price for {s.Engine} {s.InstanceClass}");
                    }
                    if (s.InstanceClass.StartsWith("db.t", StringComparison.Ordinal))
                        parts.Add("burstable: CPU credits above the baseline are billed extra");
                }
                else
                {
                    parts.Add("stopped: no instance charge (storage is still billed)");
                }
                if (!s.IsAurora && s.StorageType is { } storage && s.AllocatedStorageGb is { } gb)
                {
                    if (Hourly(RdsStorageKey(storage, s.Engine, deployment)) is { } perGb)
                    {
                        total += perGb * gb;
                        parts.Add($"{gb} GB {storage} × {Rate(perGb)}/GB-month");
                    }
                    else
                    {
                        complete = false;
                    }
                    var (iops, throughput) = BilledStoragePerformance(s);
                    if (iops > 0)
                    {
                        if (Hourly(RdsIopsKey(storage, s.Engine, deployment)) is { } perIops)
                        {
                            total += perIops * iops;
                            parts.Add($"{iops:N0} {(storage == "gp3" ? "extra " : "")}IOPS × {Rate(perIops)}/IOPS-month");
                        }
                        else
                        {
                            complete = false;
                        }
                    }
                    if (throughput > 0)
                    {
                        if (Hourly(RdsThroughputKey(s.Engine, deployment)) is { } perMbps)
                        {
                            total += perMbps * throughput;
                            parts.Add($"{throughput:N0} MiB/s extra throughput × {Rate(perMbps)}/MiB/s-month");
                        }
                        else
                        {
                            complete = false;
                        }
                    }
                    if (storage == "gp3" && iops == 0 && throughput == 0 && s.Iops is { } baseIops)
                        parts.Add($"{baseIops:N0} IOPS / {s.StorageThroughputMbps ?? 0} MiB/s included in gp3");
                }
                else if (s.IsAurora)
                {
                    parts.Add(s.IsIoOptimized ? "Aurora storage is billed per GB used (not included); I/O is included" : "Aurora storage and I/O are usage-based (not included)");
                }
                return total == 0 && !stopped && !complete ? null
                    : new CostEstimate(total, string.Join("; ", parts) + "; backup storage beyond the free allowance and data transfer not included", complete);
            }
            case CacheStatus cache:
            {
                if (cache.Snapshot.Kind == CacheKind.Serverless || cache.Snapshot.NodeType is not { } nodeType)
                    return null;
                var nodes = cache.Snapshot.Nodes.Count();
                if (nodes == 0 || Hourly(CacheKey(nodeType, cache.Snapshot.Engine)) is not { } hourly)
                    return null;
                return new CostEstimate(hourly * HoursPerMonth * nodes, $"{nodes}× {nodeType} {Rate(hourly)}/h × 730 h; backups and data transfer not included");
            }
            case VpnConnectionStatus vpn:
                return vpn.Snapshot.State == "available" && Hourly(VpnKey) is { } vpnHourly
                    ? new CostEstimate(vpnHourly * HoursPerMonth, $"VPN connection {Rate(vpnHourly)}/h × 730 h; data transfer out not included")
                    : null;
            case LoadBalancerStatus lb:
            {
                if (lb.Snapshot.Type is not ("application" or "network" or "gateway") || Hourly(LoadBalancerKey(lb.Snapshot.Type)) is not { } hourly)
                    return null;
                return new CostEstimate(hourly * HoursPerMonth, $"{lb.Snapshot.TypeText} {Rate(hourly)}/h × 730 h, plus capacity units (LCU/NLCU) by traffic (not included)", Complete: false);
            }
            case EcsServiceStatus ecs when IsFargate(ecs):
            {
                if (Hourly(FargateCpuKey) is not { } cpu || Hourly(FargateMemoryKey) is not { } memory)
                    return null;
                double vcpu = 0, gb = 0;
                foreach (var task in ecs.Snapshot.Tasks)
                {
                    if (double.TryParse(task.Cpu, CultureInfo.InvariantCulture, out var units)) vcpu += units / 1024;
                    if (double.TryParse(task.Memory, CultureInfo.InvariantCulture, out var mib)) gb += mib / 1024;
                }
                if (vcpu == 0 && gb == 0)
                    return null;
                var spot = ecs.Snapshot.Tasks.Any(t => t.CapacityProvider == "FARGATE_SPOT");
                return new CostEstimate((vcpu * cpu + gb * memory) * HoursPerMonth,
                    $"{ecs.Snapshot.Tasks.Count} task(s): {vcpu:0.##} vCPU × {Rate(cpu)}/h + {gb:0.##} GB × {Rate(memory)}/h, × 730 h (Linux/x86 on-demand{(spot ? "; some tasks run on Fargate Spot, which is cheaper" : "")})");
            }
            case EcsServiceStatus:
                return new CostEstimate(0, "runs on EC2 container instances; their cost is on the instances", Complete: false);
        }
        return null;
    }

    /// <summary>
    /// What a VPC costs by the hour, whatever runs in it: NAT gateways, interface endpoints (per zone) and public IPv4
    /// addresses (every one, on instances, load balancers and NAT gateways alike). Data processing is not included.
    /// </summary>
    public static CostEstimate? VpcEstimate(NetworkSnapshot s, string vpcId, Func<string, PriceEntry?> price)
    {
        var parts = new List<string>();
        double total = 0;
        var nats = s.NatGateways.Count(n => n.VpcId == vpcId && n.State == "available");
        if (nats > 0 && price(NatKey)?.Usd is { } nat)
        {
            total += nats * nat * HoursPerMonth;
            parts.Add($"{nats} NAT gateway(s) × {Rate(nat)}/h");
        }
        var zones = s.Endpoints.Where(e => e.VpcId == vpcId && IsHourlyEndpoint(e)).Sum(e => Math.Max(1, e.SubnetIds.Count));
        if (zones > 0 && price(EndpointKey)?.Usd is { } endpoint)
        {
            total += zones * endpoint * HoursPerMonth;
            parts.Add($"{zones} interface endpoint zone(s) × {Rate(endpoint)}/h");
        }
        var ips = s.Interfaces.Where(i => i.VpcId == vpcId).SelectMany(i => i.PublicIps).Distinct().Count();
        if (ips > 0 && price(Ipv4Key)?.Usd is { } ipv4)
        {
            total += ips * ipv4 * HoursPerMonth;
            parts.Add($"{ips} public IPv4 address(es) × {Rate(ipv4)}/h");
        }
        return parts.Count == 0 ? null
            : new CostEstimate(total, $"{string.Join(" + ", parts)} × 730 h; data processed and transferred not included", Complete: false);
    }

    /// <summary>Elastic IPs that are not associated (billed while idle, outside any VPC's total).</summary>
    public static CostEstimate? IdleAddressEstimate(NetworkSnapshot s, Func<string, PriceEntry?> price) =>
        s.ElasticIps.Count(a => !a.IsAssociated) is var idle and > 0 && price(Ipv4Key)?.Usd is { } ipv4
            ? new CostEstimate(idle * ipv4 * HoursPerMonth, $"{idle} idle Elastic IP(s) × {Rate(ipv4)}/h × 730 h")
            : null;

    /// <summary>Every VPC of the target plus idle Elastic IPs.</summary>
    public static CostEstimate? NetworkEstimate(NetworkSnapshot s, Func<string, PriceEntry?> price)
    {
        var parts = s.Vpcs.Select(v => VpcEstimate(s, v.Id, price)).Append(IdleAddressEstimate(s, price)).Where(e => e is not null).ToList();
        return parts.Count == 0 ? null
            : new CostEstimate(parts.Sum(p => p!.MonthlyUsd), "NAT gateways, interface endpoints and public IPv4 addresses (Network tab)", Complete: false);
    }

    /// <summary>Secrets ($ per secret per month) and Advanced-tier parameters; Standard parameters are free.</summary>
    public static CostEstimate? CatalogEstimate(int secrets, int advancedParameters, Func<string, PriceEntry?> price)
    {
        var parts = new List<string>();
        double total = 0;
        if (secrets > 0 && price(SecretKey)?.Usd is { } secret)
        {
            total += secrets * secret;
            parts.Add($"{secrets} secret(s) × {Money(secret)}");
        }
        if (advancedParameters > 0 && price(AdvancedParameterKey)?.Usd is { } hourly)
        {
            total += advancedParameters * hourly * HoursPerMonth;
            parts.Add($"{advancedParameters} Advanced parameter(s) × {Money(hourly * HoursPerMonth)}");
        }
        return parts.Count == 0 ? null : new CostEstimate(total, $"{string.Join(" + ", parts)} a month; API calls by your applications not included");
    }

    public static CostEstimate? NatEstimate(NetworkSnapshot network, Func<string, PriceEntry?> price) =>
        network.NatGateways.Count(n => n.State == "available") is var count and > 0 && price(NatKey)?.Usd is { } hourly
            ? new CostEstimate(hourly * HoursPerMonth * count, $"{count} NAT gateway(s) × {Rate(hourly)}/h × 730 h, plus data processed (not included)", Complete: false)
            : null;

    private static string Rate(double usd) => usd.ToString(usd < 0.1 ? "$0.0000" : "$0.000", CultureInfo.InvariantCulture);

    /// <summary>The ids Cost Explorer uses for a resource (instance ids, ARNs), to match resource-level costs.</summary>
    public static IEnumerable<string> CostIds(ResourceStatus r) => r switch
    {
        Ec2InstanceStatus ec2 => [ec2.Snapshot.InstanceId],
        EbEnvironmentStatus eb => eb.Snapshot.InstanceIds,
        RdsInstanceStatus db when db.Snapshot.Arn is { } arn => [arn],
        CacheStatus cache => cache.Snapshot.Nodes.Select(n => n.ClusterId).Append(cache.Snapshot.Id),
        LoadBalancerStatus lb => [lb.Snapshot.Arn],
        _ => [],
    };

    /// <summary>Resource-level actual cost of the last 14 days, when Cost Explorer has it.</summary>
    public static double? Actual14Days(ResourceStatus r, ActualCosts? actual)
    {
        if (actual is not { ResourceLast14Days.Count: > 0 })
            return null;
        var ids = CostIds(r).ToList();
        if (ids.Count == 0)
            return null;
        // Cost Explorer ids are instance ids or ARNs; cache nodes appear as ARNs ending in the node's cluster id.
        var matches = actual.ResourceLast14Days.Where(kv => ids.Any(id => kv.Key == id || kv.Key.EndsWith(":" + id, StringComparison.Ordinal) || kv.Key.EndsWith("/" + id, StringComparison.Ordinal))).ToList();
        return matches.Count == 0 ? null : matches.Sum(kv => kv.Value);
    }

    /// <summary>Parses one Price List JSON document into its on-demand price dimensions.</summary>
    public static IEnumerable<PriceItem> ParseProduct(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var attributes = new Dictionary<string, string>();
        if (root.TryGetProperty("product", out var product) && product.TryGetProperty("attributes", out var attrs))
            foreach (var a in attrs.EnumerateObject())
                attributes[a.Name] = a.Value.GetString() ?? "";
        var usageType = attributes.GetValueOrDefault("usagetype", "");
        if (!root.TryGetProperty("terms", out var terms) || !terms.TryGetProperty("OnDemand", out var onDemand))
            yield break;
        foreach (var offer in onDemand.EnumerateObject())
        {
            if (!offer.Value.TryGetProperty("priceDimensions", out var dims))
                continue;
            foreach (var dim in dims.EnumerateObject())
            {
                var unit = dim.Value.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
                var description = dim.Value.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                if (dim.Value.TryGetProperty("pricePerUnit", out var ppu) && ppu.TryGetProperty("USD", out var usd)
                    && double.TryParse(usd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    yield return new PriceItem(usageType, unit, value, description, attributes);
            }
        }
    }

    /// <summary>"USE2-NatGateway-Hours" → "NatGateway-Hours"; us-east-1 usage types have no prefix, eu-west-1 uses "EU-".</summary>
    public static string WithoutRegion(string usageType) =>
        System.Text.RegularExpressions.Regex.Replace(usageType, "^([A-Z]{3}[0-9]|EU)-", "");

    private static readonly string[] ExcludedUsage = ["Spot", "ARM", "ExtendedSupport", "TrustStore", "Regional", "Outposts", "IOOptimized"];

    /// <summary>
    /// Marks an RDS estimate when the engine version is in (or about to enter) billed Extended Support: that charge
    /// is per vCPU-hour and not in the estimate.
    /// </summary>
    public static CostEstimate? WithEngineSupport(CostEstimate? estimate, RdsInstanceSnapshot s, IReadOnlyDictionary<string, DateTime> extendedSupportStarts, DateTime nowUtc)
    {
        if (estimate is null || s.EngineLifecycleSupport == "open-source-rds-extended-support-disabled")
            return estimate;
        var majors = extendedSupportStarts.Keys.Where(k => k.StartsWith(s.Engine + "|", StringComparison.Ordinal)).Select(k => k[(s.Engine.Length + 1)..]);
        if (MajorVersion(s.EngineVersion, majors) is not { } major || !extendedSupportStarts.TryGetValue($"{s.Engine}|{major}", out var start))
            return estimate;
        if (nowUtc >= start)
            return estimate with
            {
                Complete = false,
                Basis = $"{s.Engine} {major} is in RDS Extended Support since {start:yyyy-MM-dd}: its per-vCPU-hour charge is not included (upgrade to stop it); " + estimate.Basis,
            };
        if (start - nowUtc < TimeSpan.FromDays(120))
            return estimate with { Basis = $"standard support for {s.Engine} {major} ends {start.AddDays(-1):yyyy-MM-dd}; Extended Support is billed extra after that; " + estimate.Basis };
        return estimate;
    }

    /// <summary>Picks the on-demand price for a query from the products the API returned.</summary>
    public static PriceEntry Pick(PriceQuery query, IReadOnlyList<PriceItem> items, DateTime nowUtc)
    {
        var matches = items
            .Where(i => query.Unit is null || i.Unit.Equals(query.Unit, StringComparison.OrdinalIgnoreCase))
            .Where(i => query.UsageType is null || WithoutRegion(i.UsageType) == query.UsageType)
            // Spot, ARM, extended-support and add-on variants have their own usage types; keep the plain on-demand one.
            .Where(i => !ExcludedUsage.Any(x => i.UsageType.Contains(x, StringComparison.OrdinalIgnoreCase)
                                                 && query.UsageType?.Contains(x, StringComparison.OrdinalIgnoreCase) != true))
            .Where(i => i.Usd > 0)
            .ToList();
        if (matches.Count == 0)
            return new PriceEntry { FetchedUtc = nowUtc, Description = "no matching list price" };
        var best = matches.MinBy(i => i.Usd)!;
        return new PriceEntry
        {
            Usd = best.Usd,
            Unit = best.Unit,
            Description = best.Description,
            FetchedUtc = nowUtc,
            Ambiguous = matches.Select(m => m.Usd).Distinct().Count() > 1,
        };
    }
}

/// <summary>Dashboard cost figures for one resource: the estimate and, when available, the billed cost.</summary>
public sealed record ResourceCost(CostEstimate? Estimate, double? Actual14Days)
{
    [JsonIgnore]
    public string? Short => Estimate is { } e && (e.MonthlyUsd > 0 || e.Complete) ? e.Text : Actual14Days is { } a ? $"{CostRules.Money(a / 14 * 30)}/mo billed" : null;

    [JsonIgnore]
    public string Detail => string.Join(" · ", new[]
    {
        Estimate is { } e ? $"Estimate {e.Text}: {e.Basis}" : null,
        Actual14Days is { } a ? $"Billed in the last 14 days: {CostRules.Money(a)} (≈ {CostRules.Money(a / 14 * 30)}/month)" : null,
    }.Where(s => s is not null));
}
