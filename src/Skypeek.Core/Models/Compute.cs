using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

public sealed record SecurityGroupRef(string Id, string Name);

/// <summary>One network interface of an EC2 instance with its addresses and security groups.</summary>
public sealed class Ec2InterfaceInfo
{
    public string Id { get; init; } = "";
    public string? SubnetId { get; init; }
    public string? PrivateIp { get; init; }
    public List<string> PrivateIps { get; init; } = [];
    public string? PublicIp { get; init; }
    /// <summary>The public address is an Elastic IP (kept when the instance stops).</summary>
    public bool IsElasticIp { get; init; }
    public List<string> Ipv6 { get; init; } = [];
    public List<SecurityGroupRef> SecurityGroups { get; init; } = [];
    public int? DeviceIndex { get; init; }

    [JsonIgnore]
    public string Summary => string.Join(" · ", new[]
    {
        string.Join(", ", PrivateIps.Count > 0 ? PrivateIps : PrivateIp is null ? [] : [PrivateIp]),
        PublicIp is null ? null : $"public {PublicIp}{(IsElasticIp ? " (Elastic IP)" : "")}",
        Ipv6.Count > 0 ? $"IPv6 {string.Join(", ", Ipv6)}" : null,
        SubnetId,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed class Ec2InstanceSnapshot
{
    public string InstanceId { get; init; } = "";
    public string? Name { get; init; }
    public string? InstanceType { get; init; }
    public string State { get; init; } = "";
    public string? StateReason { get; init; }
    public string? AvailabilityZone { get; init; }
    public string? VpcId { get; init; }
    public string? SubnetId { get; init; }
    public string? PrivateIp { get; init; }
    public string? PublicIp { get; init; }
    public string? PrivateDns { get; init; }
    public string? PublicDns { get; init; }
    public DateTime? LaunchTime { get; init; }
    public string? Platform { get; init; }
    public string? ImageId { get; init; }
    public string? KeyName { get; init; }
    public string? IamInstanceProfile { get; init; }
    /// <summary>"spot" or "capacity-block" when not on-demand.</summary>
    public string? Lifecycle { get; init; }
    /// <summary>"ebs" or "instance-store".</summary>
    public string? RootDeviceType { get; init; }
    public List<Ec2InterfaceInfo> Interfaces { get; init; } = [];
    public List<SecurityGroupRef> SecurityGroups { get; init; } = [];
    public Dictionary<string, string> Tags { get; init; } = new();

    /// <summary>Status checks: ok, impaired, initializing, insufficient-data, not-applicable (null when not reported).</summary>
    public string? SystemStatus { get; init; }
    public string? InstanceStatus { get; init; }
    public List<string> StatusDetails { get; init; } = [];
    /// <summary>Scheduled maintenance events (reboot, retirement, …) that have not completed yet.</summary>
    public List<string> ScheduledEvents { get; init; } = [];

    [JsonIgnore] public string? EbEnvironment => Tags.GetValueOrDefault("elasticbeanstalk:environment-name");
    [JsonIgnore] public string? AutoScalingGroup => Tags.GetValueOrDefault("aws:autoscaling:groupName");
    [JsonIgnore] public string? CloudFormationStack => Tags.GetValueOrDefault("aws:cloudformation:stack-name");
    [JsonIgnore] public bool IsRunning => State == "running";
    [JsonIgnore] public bool IsStopped => State == "stopped";
    [JsonIgnore] public bool CanStart => State == "stopped";
    [JsonIgnore] public bool CanStop => State is "running" or "pending";
    [JsonIgnore] public bool CanReboot => State == "running";
    [JsonIgnore] public bool HasElasticIp => Interfaces.Any(i => i.IsElasticIp);

    /// <summary>Who manages the instance, e.g. "Elastic Beanstalk env-prod" or "Auto Scaling group web-asg".</summary>
    [JsonIgnore]
    public string? OwnerText => EbEnvironment is { } eb ? $"Elastic Beanstalk {eb}"
        : AutoScalingGroup is { } asg ? $"Auto Scaling group {asg}"
        : Tags.ContainsKey("AmazonECSManaged") ? "ECS capacity provider"
        : Lifecycle == "spot" ? "spot instance"
        : null;

    [JsonIgnore] public string Title => Name is { Length: > 0 } n ? n : InstanceId;

    [JsonIgnore]
    public string ChecksText => SystemStatus is null && InstanceStatus is null
        ? "not reported"
        : $"system {SystemStatus ?? "?"} · instance {InstanceStatus ?? "?"}";

    [JsonIgnore]
    public string AddressText => string.Join(" · ", new[] { PrivateIp, PublicIp is null ? null : $"public {PublicIp}" }.Where(s => s is not null));
}

public sealed class Ec2InstanceStatus : ResourceStatus
{
    public Ec2InstanceSnapshot Snapshot { get; init; } = new();

    public override string ResourceKey => ResourceKeys.Ec2(TargetId, Snapshot.InstanceId);
    public override string DisplayName => Snapshot.Name is { Length: > 0 } n ? $"{n} ({Snapshot.InstanceId})" : Snapshot.InstanceId;
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/ec2/home?region={Region}#InstanceDetails:instanceId={Snapshot.InstanceId}";
}

// ---------------- Elastic Load Balancing (v2: application, network, gateway) ----------------

public sealed class LbTargetInfo
{
    public string Id { get; init; } = "";
    public int? Port { get; init; }
    public string? AvailabilityZone { get; init; }
    /// <summary>initial, healthy, unhealthy, unhealthy.draining, unused, draining, unavailable.</summary>
    public string State { get; init; } = "";
    public string? Reason { get; init; }
    public string? Description { get; init; }

    [JsonIgnore]
    public HealthLevel Level => State switch
    {
        "healthy" => HealthLevel.Ok,
        "unhealthy" or "unhealthy.draining" => HealthLevel.Critical,
        "unavailable" => HealthLevel.Warn,
        _ => HealthLevel.Unknown,
    };
    [JsonIgnore] public bool IsInstance => Id.StartsWith("i-", StringComparison.Ordinal);
    [JsonIgnore] public string Title => Port is { } p ? $"{Id}:{p}" : Id;
    [JsonIgnore]
    public string Detail => string.Join(" · ", new[] { State, Reason, Description, AvailabilityZone }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed class TargetGroupInfo
{
    public string Name { get; init; } = "";
    public string Arn { get; init; } = "";
    public string? Protocol { get; init; }
    public int? Port { get; init; }
    /// <summary>instance, ip, lambda or alb.</summary>
    public string? TargetType { get; init; }
    public string? VpcId { get; init; }
    public string? HealthCheck { get; init; }
    public List<string> LoadBalancerArns { get; init; } = [];
    public List<LbTargetInfo> Targets { get; init; } = [];
    /// <summary>Target health could not be read.</summary>
    public string? Error { get; init; }

    [JsonIgnore] public int Healthy => Targets.Count(t => t.State == "healthy");
    [JsonIgnore] public int Unhealthy => Targets.Count(t => t.Level >= HealthLevel.Warn);
    /// <summary>Targets that take part in health checks (draining/unused ones are on their way out or not used).</summary>
    [JsonIgnore] public int Counted => Targets.Count(t => t.State is not ("draining" or "unused"));
    [JsonIgnore]
    public HealthLevel Level => Unhealthy == 0 ? (Targets.Count == 0 ? HealthLevel.Unknown : HealthLevel.Ok)
        : Healthy == 0 ? HealthLevel.Critical : HealthLevel.Warn;
    [JsonIgnore] public string ProtocolText => Port is { } p ? $"{Protocol}:{p}" : Protocol ?? "";
    [JsonIgnore] public string HealthText => Targets.Count == 0 ? "no registered targets" : $"{Healthy}/{Counted} healthy";
    /// <summary>The targetgroup/name/id part of the ARN, as used in CloudWatch dimensions.</summary>
    [JsonIgnore] public string ArnSuffix => Arn.IndexOf(":targetgroup/", StringComparison.Ordinal) is var i and >= 0 ? Arn[(i + 1)..] : Arn;
    [JsonIgnore] public string UnhealthyReasons => string.Join("; ", Targets.Where(t => t.Level >= HealthLevel.Warn).Select(t => t.Reason ?? t.State).Distinct().Take(3));
}

public sealed class LoadBalancerSnapshot
{
    public string Name { get; init; } = "";
    public string Arn { get; init; } = "";
    /// <summary>application, network or gateway.</summary>
    public string Type { get; init; } = "";
    public string? Scheme { get; init; }
    /// <summary>provisioning, active, active_impaired or failed.</summary>
    public string State { get; init; } = "";
    public string? StateReason { get; init; }
    public string? DnsName { get; init; }
    public string? VpcId { get; init; }
    public string? IpAddressType { get; init; }
    public DateTime? Created { get; init; }
    public List<string> Zones { get; init; } = [];
    public List<string> SecurityGroups { get; init; } = [];
    public List<TargetGroupInfo> TargetGroups { get; init; } = [];

    /// <summary>The app/name/id part of the ARN, as used in CloudWatch dimensions.</summary>
    [JsonIgnore] public string ArnSuffix => Arn.IndexOf(":loadbalancer/", StringComparison.Ordinal) is var i and >= 0 ? Arn[(i + ":loadbalancer/".Length)..] : Arn;
    [JsonIgnore] public string TypeText => Type switch { "application" => "ALB", "network" => "NLB", "gateway" => "GWLB", _ => Type };
    [JsonIgnore] public string Namespace => Type == "network" ? "AWS/NetworkELB" : Type == "gateway" ? "AWS/GatewayELB" : "AWS/ApplicationELB";
    [JsonIgnore] public int Healthy => TargetGroups.Sum(t => t.Healthy);
    [JsonIgnore] public int Counted => TargetGroups.Sum(t => t.Counted);
}

public sealed class LoadBalancerStatus : ResourceStatus
{
    public LoadBalancerSnapshot Snapshot { get; init; } = new();
    /// <summary>Target group problems with their suppression state (recomputed with the current settings).</summary>
    public List<CauseItem> CauseItems { get; set; } = [];
    /// <summary>Last hour, from CloudWatch: ALB requests and ELB 5xx, NLB active flows.</summary>
    public double? RequestCount { get; set; }
    public double? Elb5xxCount { get; set; }
    public double? Target5xxCount { get; set; }
    public double? ActiveFlows { get; set; }

    [JsonIgnore]
    public string TrafficText => string.Join(" · ", new[]
    {
        RequestCount is { } r ? $"{r:N0} requests" : null,
        Elb5xxCount is { } e ? $"{e:N0} ELB 5xx" : null,
        Target5xxCount is { } t ? $"{t:N0} target 5xx" : null,
        ActiveFlows is { } f ? $"{f:N0} active flows" : null,
    }.Where(s => s is not null)) is { Length: > 0 } text ? $"{text} in the last hour" : "no traffic data";

    public override string ResourceKey => ResourceKeys.LoadBalancer(TargetId, Snapshot.Name);
    public override string DisplayName => Snapshot.Name;
    public override string ConsoleUrl =>
        $"https://{Region}.console.aws.amazon.com/ec2/home?region={Region}#LoadBalancer:loadBalancerArn={Uri.EscapeDataString(Snapshot.Arn)}";

    public string TargetGroupConsoleUrl(TargetGroupInfo group) =>
        $"https://{Region}.console.aws.amazon.com/ec2/home?region={Region}#TargetGroup:targetGroupArn={Uri.EscapeDataString(group.Arn)}";
}

/// <summary>One listener rule, rendered as text (loaded on demand for the details page).</summary>
public sealed record LbRuleInfo(string Priority, bool IsDefault, string Conditions, string Actions);

public sealed record LbListenerInfo(string Arn, string Protocol, int? Port, string? SslPolicy, IReadOnlyList<string> Certificates, IReadOnlyList<LbRuleInfo> Rules)
{
    public string Title => $"{Protocol}:{Port}";
    public string Detail => string.Join(" · ", new[]
    {
        SslPolicy,
        Certificates.Count > 0 ? $"{Certificates.Count} certificate(s)" : null,
        $"{Rules.Count} rule(s)",
    }.Where(s => s is not null));
}
