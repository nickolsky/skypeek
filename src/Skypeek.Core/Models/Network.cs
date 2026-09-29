using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

/// <summary>Everything the Network tab shows for one target, downloaded in one go and cached in the vault.</summary>
public sealed class NetworkSnapshot
{
    public long TargetId { get; init; }
    public DateTime DownloadedUtc { get; set; }
    public List<VpcInfo> Vpcs { get; set; } = [];
    public List<SubnetInfo> Subnets { get; set; } = [];
    public List<NetworkInterfaceInfo> Interfaces { get; set; } = [];
    public List<SecurityGroupInfo> SecurityGroups { get; set; } = [];
    public List<ElasticIpInfo> ElasticIps { get; set; } = [];
    public List<RouteTableInfo> RouteTables { get; set; } = [];
    /// <summary>Internet gateways and egress-only internet gateways.</summary>
    public List<InternetGatewayInfo> InternetGateways { get; set; } = [];
    public List<NatGatewayInfo> NatGateways { get; set; } = [];
    public List<VpcEndpointInfo> Endpoints { get; set; } = [];
    public List<PeeringInfo> Peerings { get; set; } = [];
    /// <summary>Parts that could not be read (the rest is still shown).</summary>
    public string? Error { get; set; }
}

public sealed class VpcInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public List<string> Cidrs { get; init; } = [];
    public List<string> Ipv6Cidrs { get; init; } = [];
    public bool IsDefault { get; init; }
    public string? State { get; init; }

    [JsonIgnore] public string Title => Name is { Length: > 0 } n ? $"{n} ({Id})" : Id;
}

public sealed class SubnetInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string VpcId { get; init; } = "";
    public string Cidr { get; init; } = "";
    public List<string> Ipv6Cidrs { get; init; } = [];
    public string? AvailabilityZone { get; init; }
    public int? AvailableIps { get; init; }
    public bool MapPublicIpOnLaunch { get; init; }
    public bool DefaultForAz { get; init; }

    [JsonIgnore] public string Title => Name is { Length: > 0 } n ? $"{n} ({Id})" : Id;
    /// <summary>Usable addresses: the block minus the 5 AWS reserves.</summary>
    [JsonIgnore] public int? UsableIps => NetworkRules.PrefixLength(Cidr) is { } p and <= 28 ? (1 << (32 - p)) - 5 : null;
    [JsonIgnore] public int? UsedIps => UsableIps is { } total && AvailableIps is { } free ? total - free : null;
    [JsonIgnore] public double? UsedPercent => UsableIps is { } total and > 0 && UsedIps is { } used ? used * 100.0 / total : null;
    [JsonIgnore]
    public string UsageText => UsableIps is { } total && AvailableIps is { } free ? $"{total - free}/{total} IPs used · {free} free" : "";
    [JsonIgnore] public string Kind => MapPublicIpOnLaunch ? "public" : "private";
    /// <summary>Nearly full subnets (fewer than 16 free addresses, or 90% used) cannot scale out.</summary>
    [JsonIgnore] public HealthLevel UsageLevel => AvailableIps is < 16 || UsedPercent is >= 90 ? HealthLevel.Warn : HealthLevel.Ok;
    [JsonIgnore]
    public string KindDetail => (MapPublicIpOnLaunch ? "assigns public IPs to new instances" : "no public IPs by default")
                                + (DefaultForAz ? " · default subnet of the zone" : "");
}

public sealed class NetworkInterfaceInfo
{
    public string Id { get; init; } = "";
    public string? VpcId { get; init; }
    public string? SubnetId { get; init; }
    public string? AvailabilityZone { get; init; }
    public string? PrivateIp { get; init; }
    public List<string> PrivateIps { get; init; } = [];
    public List<string> PublicIps { get; init; } = [];
    public List<string> Ipv6 { get; init; } = [];
    /// <summary>interface, nat_gateway, vpc_endpoint, lambda, network_load_balancer, …</summary>
    public string? InterfaceType { get; init; }
    public string? Description { get; init; }
    public string? Status { get; init; }
    public string? InstanceId { get; init; }
    public string? RequesterId { get; init; }
    public bool RequesterManaged { get; init; }
    public string? Name { get; init; }
    public List<SecurityGroupRef> SecurityGroups { get; init; } = [];

    /// <summary>What uses the interface, e.g. "EC2 i-0abc", "Load balancer app/web", "RDS", "NAT gateway".</summary>
    [JsonIgnore] public string Owner => NetworkRules.InterfaceOwner(this);
    [JsonIgnore] public IEnumerable<string> AllIps => PrivateIps.Concat(PublicIps).Concat(Ipv6);
    [JsonIgnore]
    public string AddressText => string.Join(" · ", new[]
    {
        string.Join(", ", PrivateIps.Count > 0 ? PrivateIps : PrivateIp is null ? [] : [PrivateIp]),
        PublicIps.Count > 0 ? $"public {string.Join(", ", PublicIps)}" : null,
        Ipv6.Count > 0 ? $"IPv6 {string.Join(", ", Ipv6)}" : null,
    }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed class ElasticIpInfo
{
    public string PublicIp { get; init; } = "";
    public string? AllocationId { get; init; }
    public string? InstanceId { get; init; }
    public string? NetworkInterfaceId { get; init; }
    public string? PrivateIp { get; init; }
    public string? Name { get; init; }

    [JsonIgnore] public bool IsAssociated => NetworkInterfaceId is not null || InstanceId is not null;
}

public sealed class SecurityGroupInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? VpcId { get; init; }
    public string? OwnerId { get; init; }
    public string? NameTag { get; init; }
    public List<SecurityGroupRuleInfo> Rules { get; set; } = [];

    [JsonIgnore] public IEnumerable<SecurityGroupRuleInfo> Inbound => Rules.Where(r => !r.IsEgress).OrderBy(r => r.SortKey);
    [JsonIgnore] public IEnumerable<SecurityGroupRuleInfo> Outbound => Rules.Where(r => r.IsEgress).OrderBy(r => r.SortKey);
    [JsonIgnore] public string Title => NameTag is { Length: > 0 } tag && tag != Name ? $"{Name} · {tag}" : Name;
    [JsonIgnore] public int OpenToWorldCount => Rules.Count(r => !r.IsEgress && r.IsOpenToWorld);

    public string ConsoleUrl(string region) =>
        $"https://{region}.console.aws.amazon.com/ec2/home?region={region}#SecurityGroup:groupId={Id}";
}

public enum RuleSourceKind { Ipv4, Ipv6, PrefixList, SecurityGroup }

/// <summary>One security group rule as AWS reports it (each rule has exactly one source).</summary>
public sealed class SecurityGroupRuleInfo
{
    public string RuleId { get; init; } = "";
    public string GroupId { get; init; } = "";
    public bool IsEgress { get; init; }
    /// <summary>"-1" (all), "tcp", "udp", "icmp", "icmpv6" or a protocol number.</summary>
    public string Protocol { get; init; } = "-1";
    public int? FromPort { get; init; }
    public int? ToPort { get; init; }
    public string? CidrIpv4 { get; init; }
    public string? CidrIpv6 { get; init; }
    public string? PrefixListId { get; init; }
    public string? ReferencedGroupId { get; init; }
    public string? ReferencedGroupUserId { get; init; }
    public string? Description { get; init; }

    [JsonIgnore]
    public RuleSourceKind SourceKind => CidrIpv4 is not null ? RuleSourceKind.Ipv4 : CidrIpv6 is not null ? RuleSourceKind.Ipv6
        : PrefixListId is not null ? RuleSourceKind.PrefixList : RuleSourceKind.SecurityGroup;
    [JsonIgnore] public string Source => CidrIpv4 ?? CidrIpv6 ?? PrefixListId ?? ReferencedGroupId ?? "";
    [JsonIgnore] public string ProtocolText => NetworkRules.ProtocolText(Protocol);
    [JsonIgnore] public string PortText => NetworkRules.PortText(Protocol, FromPort, ToPort);
    [JsonIgnore] public string SourceText => ReferencedGroupUserId is { } user && ReferencedGroupId is not null ? $"{Source} (account {user})" : Source;
    [JsonIgnore] public bool IsOpenToWorld => NetworkRules.IsWorld(CidrIpv4) || NetworkRules.IsWorld(CidrIpv6);
    [JsonIgnore] public string DirectionText => IsEgress ? "outbound" : "inbound";
    [JsonIgnore] public string Summary => $"{DirectionText} {ProtocolText} {PortText} {(IsEgress ? "to" : "from")} {SourceText}";
    [JsonIgnore] public string SortKey => $"{(FromPort ?? -1) + 100000:D6}|{Protocol}|{Source}";

    public SecurityGroupRuleSpec ToSpec() => new(IsEgress, Protocol, FromPort, ToPort, SourceKind, Source, Description);
}

/// <summary>A rule the user wants to add, or the new values of a rule being edited.</summary>
/// <param name="FromPort">For ICMP: the type (-1 = all). Ignored for "all protocols".</param>
/// <param name="ToPort">For ICMP: the code (-1 = all).</param>
public sealed record SecurityGroupRuleSpec(bool IsEgress, string Protocol, int? FromPort, int? ToPort, RuleSourceKind SourceKind, string Source, string? Description)
{
    public bool IsOpenToWorld => SourceKind is RuleSourceKind.Ipv4 or RuleSourceKind.Ipv6 && NetworkRules.IsWorld(Source);
    public string Summary => $"{(IsEgress ? "outbound" : "inbound")} {NetworkRules.ProtocolText(Protocol)} {NetworkRules.PortText(Protocol, FromPort, ToPort)} {(IsEgress ? "to" : "from")} {Source}";
}

/// <summary>Pure helpers for the Network tab; no I/O.</summary>
public static class NetworkRules
{
    public static int? PrefixLength(string? cidr) =>
        cidr?.Split('/') is [_, var p] && int.TryParse(p, out var length) ? length : null;

    public static bool IsWorld(string? cidr) => cidr is "0.0.0.0/0" or "::/0";

    public static string ProtocolText(string protocol) => protocol switch
    {
        "-1" => "all traffic",
        "tcp" or "6" => "TCP",
        "udp" or "17" => "UDP",
        "icmp" or "1" => "ICMP",
        "icmpv6" or "58" => "ICMPv6",
        _ => $"protocol {protocol}",
    };

    private static bool IsIcmp(string protocol) => protocol is "icmp" or "1" or "icmpv6" or "58";

    public static string PortText(string protocol, int? from, int? to)
    {
        if (protocol == "-1")
            return "all ports";
        if (IsIcmp(protocol))
            return from is null or -1 ? "all types" : to is null or -1 ? $"type {from}" : $"type {from} code {to}";
        if (from is null && to is null)
            return "all ports";
        if (from is 0 && to is 65535)
            return "all ports";
        return from == to ? $"port {from}" : $"ports {from}-{to}";
    }

    /// <summary>Why the rule cannot be sent, or null when it is valid.</summary>
    public static string? Validate(SecurityGroupRuleSpec spec)
    {
        var protocol = spec.Protocol.Trim().ToLowerInvariant();
        if (protocol is not ("-1" or "tcp" or "udp" or "icmp" or "icmpv6") && !(int.TryParse(protocol, out var number) && number is >= 0 and <= 255))
            return "Protocol must be all, TCP, UDP, ICMP, ICMPv6 or a protocol number 0–255.";
        if (protocol is "tcp" or "udp")
        {
            if (spec.FromPort is not { } from || spec.ToPort is not { } to)
                return "Enter a port or a port range.";
            if (from is < 0 or > 65535 || to is < 0 or > 65535)
                return "Ports must be between 0 and 65535.";
            if (from > to)
                return "The first port of a range must not be above the last.";
        }
        if (IsIcmp(protocol) && (spec.FromPort is < -1 or > 255 || spec.ToPort is < -1 or > 255))
            return "ICMP type and code must be between -1 (all) and 255.";

        var source = spec.Source.Trim();
        switch (spec.SourceKind)
        {
            case RuleSourceKind.Ipv4 when !IsCidr(source, AddressFamily.InterNetwork):
                return "Enter an IPv4 CIDR such as 10.0.0.0/16 or 203.0.113.5/32.";
            case RuleSourceKind.Ipv6 when !IsCidr(source, AddressFamily.InterNetworkV6):
                return "Enter an IPv6 CIDR such as 2001:db8::/32.";
            case RuleSourceKind.PrefixList when !source.StartsWith("pl-", StringComparison.Ordinal):
                return "Enter a prefix list id (pl-…).";
            case RuleSourceKind.SecurityGroup when !source.StartsWith("sg-", StringComparison.Ordinal):
                return "Enter a security group id (sg-…).";
        }
        if (spec.Description is { Length: > 255 })
            return "The description can be at most 255 characters.";
        return null;
    }

    public static bool IsCidr(string text, AddressFamily family)
    {
        var parts = text.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != family)
            return false;
        var max = family == AddressFamily.InterNetwork ? 32 : 128;
        return int.TryParse(parts[1], out var length) && length >= 0 && length <= max;
    }

    /// <summary>An IP address or CIDR inside <paramref name="cidr"/>.</summary>
    public static bool Contains(string cidr, string ip)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var length)
            || !IPAddress.TryParse(ip.Split('/')[0], out var address) || network.AddressFamily != address.AddressFamily)
            return false;
        var a = network.GetAddressBytes();
        var b = address.GetAddressBytes();
        for (var bit = 0; bit < length; bit++)
        {
            var mask = (byte)(0x80 >> (bit % 8));
            if ((a[bit / 8] & mask) != (b[bit / 8] & mask))
                return false;
        }
        return true;
    }

    /// <summary>The route table a subnet uses: its explicit association, else the VPC's main table.</summary>
    public static (RouteTableInfo? Table, bool Explicit) RouteTableOf(NetworkSnapshot s, SubnetInfo subnet) =>
        s.RouteTables.FirstOrDefault(t => t.SubnetIds.Contains(subnet.Id)) is { } table
            ? (table, true)
            : (s.RouteTables.FirstOrDefault(t => t.VpcId == subnet.VpcId && t.IsMain), false);

    /// <summary>A short label for a route target, e.g. "NAT gateway nat-01 (54.1.2.3)".</summary>
    public static string TargetLabel(NetworkSnapshot s, string? target)
    {
        if (target is null)
            return "deleted target";
        if (target == "local")
            return "local (inside the VPC)";
        if (target.StartsWith("igw-", StringComparison.Ordinal))
            return $"internet gateway {target}";
        if (target.StartsWith("eigw-", StringComparison.Ordinal))
            return $"egress-only internet gateway {target}";
        if (target.StartsWith("nat-", StringComparison.Ordinal))
            return s.NatGateways.FirstOrDefault(n => n.Id == target) is { } nat
                ? $"{(nat.IsPublic ? "NAT gateway" : "private NAT gateway")} {target} ({nat.PublicIpText})"
                : $"NAT gateway {target}";
        if (target.StartsWith("tgw-", StringComparison.Ordinal))
            return $"transit gateway {target}";
        if (target.StartsWith("pcx-", StringComparison.Ordinal))
            return $"peering {target}";
        if (target.StartsWith("vgw-", StringComparison.Ordinal))
            return $"VPN gateway {target}";
        if (target.StartsWith("vpce-", StringComparison.Ordinal))
            return s.Endpoints.FirstOrDefault(e => e.Id == target) is { } ep ? $"VPC endpoint {target} ({ep.ShortService})" : $"VPC endpoint {target}";
        if (target.StartsWith("i-", StringComparison.Ordinal))
            return $"instance {target} (NAT instance or appliance)";
        if (target.StartsWith("eni-", StringComparison.Ordinal))
            return s.Interfaces.FirstOrDefault(i => i.Id == target) is { } eni ? $"network interface {target} ({eni.Owner})" : $"network interface {target}";
        return target;
    }

    public static void SubnetRoutingOf(NetworkSnapshot s, string subnetId, out SubnetRouting routing) =>
        routing = Routing(s, s.Subnets.First(x => x.Id == subnetId));

    /// <summary>How a subnet reaches the internet, from the default routes of its route table.</summary>
    public static SubnetRouting Routing(NetworkSnapshot s, SubnetInfo subnet)
    {
        var (table, isExplicit) = RouteTableOf(s, subnet);
        var routes = table?.Routes ?? [];
        var v4 = routes.FirstOrDefault(r => r.IsDefaultIpv4);
        var v6 = routes.FirstOrDefault(r => r.IsDefaultIpv6);
        string? warning = null;
        string? note = null;

        InternetAccess access;
        string summary;
        if (table is null)
        {
            access = InternetAccess.Isolated;
            summary = "No route table found";
        }
        else if (v4 is null)
        {
            access = InternetAccess.Isolated;
            summary = s.Endpoints.Any(e => e.RouteTableIds.Contains(table.Id) || e.SubnetIds.Contains(subnet.Id))
                ? "No internet route (isolated); reaches AWS services through VPC endpoints"
                : "No internet route (isolated)";
        }
        else if (v4.IsBlackhole || v4.Target is null)
        {
            access = InternetAccess.Blackhole;
            summary = $"Default route is a blackhole ({v4.Target ?? "target deleted"}): internet traffic is dropped";
        }
        else if (v4.Target.StartsWith("igw-", StringComparison.Ordinal))
        {
            access = InternetAccess.Public;
            summary = $"Public: internet gateway {v4.Target}";
            if (!subnet.MapPublicIpOnLaunch)
                note = "Does not assign public IPs automatically: only resources given a public or Elastic IP (e.g. load balancers, NAT gateways) use the internet gateway.";
        }
        else if (v4.Target.StartsWith("nat-", StringComparison.Ordinal))
        {
            access = InternetAccess.Nat;
            var nat = s.NatGateways.FirstOrDefault(n => n.Id == v4.Target);
            summary = nat is null ? $"Private: outbound through NAT gateway {v4.Target}"
                : nat.IsPublic ? $"Private: outbound through NAT gateway {nat.Id}, seen on the internet as {nat.PublicIpText}"
                : $"Private: through private NAT gateway {nat.Id} (no internet; other networks only)";
            if (nat is { State: not "available" })
                warning = $"NAT gateway {nat.Id} is {nat.State}{(nat.FailureMessage is { } f ? $": {f}" : "")}.";
            else if (nat is { IsPublic: true, SubnetId: { } natSubnet } && s.Subnets.FirstOrDefault(x => x.Id == natSubnet) is { } home
                     && Routing(s, home, depth: 1).Access != InternetAccess.Public)
                warning = $"NAT gateway {nat.Id} sits in {home.Title}, which has no route to an internet gateway, so it cannot reach the internet.";
        }
        else if (v4.Target.StartsWith("i-", StringComparison.Ordinal) || v4.Target.StartsWith("eni-", StringComparison.Ordinal))
        {
            access = InternetAccess.Nat;
            summary = $"Private: outbound through {TargetLabel(s, v4.Target)}";
        }
        else
        {
            access = InternetAccess.Other;
            summary = $"Default route to {TargetLabel(s, v4.Target)}: internet access depends on that network";
        }

        string? ipv6 = v6 is null ? null
            : v6.Target?.StartsWith("eigw-", StringComparison.Ordinal) == true ? $"IPv6: outbound only through {v6.Target}"
            : v6.Target?.StartsWith("igw-", StringComparison.Ordinal) == true ? $"IPv6: public through {v6.Target}"
            : $"IPv6: default route to {TargetLabel(s, v6.Target)}";
        return new SubnetRouting(subnet, table, isExplicit, access, v4?.Target, summary, ipv6, warning, note);
    }

    private static SubnetRouting Routing(NetworkSnapshot s, SubnetInfo subnet, int depth) =>
        depth > 0 ? RoutingNoWarnings(s, subnet) : Routing(s, subnet);

    private static SubnetRouting RoutingNoWarnings(NetworkSnapshot s, SubnetInfo subnet)
    {
        var (table, isExplicit) = RouteTableOf(s, subnet);
        var v4 = table?.Routes.FirstOrDefault(r => r.IsDefaultIpv4);
        var access = v4?.Target?.StartsWith("igw-", StringComparison.Ordinal) == true && !v4.IsBlackhole ? InternetAccess.Public : InternetAccess.Other;
        return new SubnetRouting(subnet, table, isExplicit, access, v4?.Target, "", null, null);
    }

    public static string InterfaceOwner(NetworkInterfaceInfo eni)
    {
        var description = eni.Description ?? "";
        if (eni.InstanceId is { } instance)
            return $"EC2 {instance}";
        if (description.StartsWith("ELB ", StringComparison.Ordinal))
            return $"Load balancer {description[4..]}";
        return eni.InterfaceType switch
        {
            "nat_gateway" => "NAT gateway",
            "vpc_endpoint" or "gateway_load_balancer_endpoint" => "VPC endpoint",
            "lambda" => "Lambda",
            "network_load_balancer" or "gateway_load_balancer" => "Load balancer",
            "efs" => "EFS mount target",
            "transit_gateway" => "Transit gateway",
            "api_gateway_managed" => "API Gateway",
            "quicksight" => "QuickSight",
            _ when description.StartsWith("RDSNetworkInterface", StringComparison.Ordinal) || eni.RequesterId == "amazon-rds" => "RDS",
            _ when description.StartsWith("ElastiCache", StringComparison.Ordinal) || eni.RequesterId == "amazon-elasticache" => $"ElastiCache{(description.Length > "ElastiCache".Length ? description["ElastiCache".Length..] : "")}",
            _ when description.StartsWith("arn:aws:ecs:", StringComparison.Ordinal) => "ECS task",
            _ when description.StartsWith("AWS Lambda VPC ENI", StringComparison.Ordinal) => "Lambda",
            _ when description.StartsWith("Interface for NAT Gateway", StringComparison.Ordinal) => "NAT gateway",
            _ when description.StartsWith("EFS mount target", StringComparison.Ordinal) => "EFS mount target",
            _ when description.Length > 0 => description,
            _ => eni.Status == "available" ? "not attached" : eni.RequesterManaged ? "AWS managed" : "",
        };
    }
}
