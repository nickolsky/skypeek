using System.Text.Json.Serialization;

namespace Skypeek.Core.Models;

public sealed class RouteTableInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string VpcId { get; init; } = "";
    /// <summary>The VPC's main route table: used by every subnet without an explicit association.</summary>
    public bool IsMain { get; init; }
    public List<string> SubnetIds { get; init; } = [];
    public List<RouteInfo> Routes { get; init; } = [];

    [JsonIgnore] public string Title => Name is { Length: > 0 } n ? $"{n} ({Id})" : Id;
}

public sealed class RouteInfo
{
    /// <summary>CIDR (IPv4 or IPv6) or prefix list id.</summary>
    public string Destination { get; init; } = "";
    /// <summary>local, igw-…, nat-…, eigw-…, tgw-…, pcx-…, vpce-…, vgw-…, eni-…, i-…, … (null when the target is gone).</summary>
    public string? Target { get; init; }
    /// <summary>active or blackhole (the target was deleted).</summary>
    public string? State { get; init; }
    /// <summary>CreateRouteTable (local), CreateRoute or EnableVgwRoutePropagation.</summary>
    public string? Origin { get; init; }

    [JsonIgnore] public bool IsBlackhole => State == "blackhole";
    [JsonIgnore] public bool IsDefaultIpv4 => Destination == "0.0.0.0/0";
    [JsonIgnore] public bool IsDefaultIpv6 => Destination == "::/0";
    [JsonIgnore] public string TargetText => Target is null ? "(target deleted)" : Target;
    [JsonIgnore] public string Text => $"{Destination} → {TargetText}{(IsBlackhole ? " (blackhole)" : "")}{(Origin == "EnableVgwRoutePropagation" ? " (propagated)" : "")}";
}

public sealed class InternetGatewayInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public List<string> VpcIds { get; init; } = [];
    /// <summary>Egress-only (IPv6 outbound only).</summary>
    public bool EgressOnly { get; init; }
}

public sealed class NatGatewayInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string? VpcId { get; init; }
    public string? SubnetId { get; init; }
    /// <summary>pending, available, deleting, deleted, failed.</summary>
    public string State { get; init; } = "";
    /// <summary>public (internet via its Elastic IP) or private (to other networks only).</summary>
    public string ConnectivityType { get; init; } = "public";
    public List<string> PublicIps { get; init; } = [];
    public List<string> PrivateIps { get; init; } = [];
    public string? FailureMessage { get; init; }

    [JsonIgnore] public bool IsPublic => ConnectivityType != "private";
    [JsonIgnore] public string Title => Name is { Length: > 0 } n ? $"{n} ({Id})" : Id;
    [JsonIgnore] public string PublicIpText => PublicIps.Count == 0 ? (IsPublic ? "no public IP" : "private NAT") : string.Join(", ", PublicIps);
}

public sealed class VpcEndpointInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string VpcId { get; init; } = "";
    public string ServiceName { get; init; } = "";
    /// <summary>Gateway (S3/DynamoDB, via route tables), Interface (ENIs in subnets), GatewayLoadBalancer, …</summary>
    public string Type { get; init; } = "";
    public string? State { get; init; }
    public List<string> RouteTableIds { get; init; } = [];
    public List<string> SubnetIds { get; init; } = [];

    /// <summary>com.amazonaws.us-east-1.s3 → s3.</summary>
    [JsonIgnore] public string ShortService => ServiceName.Split('.').LastOrDefault() ?? ServiceName;
}

public sealed class PeeringInfo
{
    public string Id { get; init; } = "";
    public string? Name { get; init; }
    public string? RequesterVpcId { get; init; }
    public string? RequesterOwner { get; init; }
    public string? RequesterRegion { get; init; }
    public string? RequesterCidr { get; init; }
    public string? AccepterVpcId { get; init; }
    public string? AccepterOwner { get; init; }
    public string? AccepterRegion { get; init; }
    public string? AccepterCidr { get; init; }
    public string? Status { get; init; }

    /// <summary>The other side as seen from <paramref name="vpcId"/>.</summary>
    public string PeerText(string vpcId) => RequesterVpcId == vpcId
        ? $"{AccepterVpcId} ({AccepterCidr}{(AccepterRegion is { } r ? $", {r}" : "")}{(AccepterOwner is { } o ? $", account {o}" : "")})"
        : $"{RequesterVpcId} ({RequesterCidr}{(RequesterRegion is { } r2 ? $", {r2}" : "")}{(RequesterOwner is { } o2 ? $", account {o2}" : "")})";
}

public enum InternetAccess
{
    /// <summary>Default route to an internet gateway: public addresses are reachable both ways.</summary>
    Public,
    /// <summary>Default route to a NAT gateway (or NAT instance/appliance): outbound only.</summary>
    Nat,
    /// <summary>Default route to a transit gateway, peering, VPN or firewall: internet access depends on that network.</summary>
    Other,
    /// <summary>No default route: no internet access (VPC endpoints may still reach AWS services).</summary>
    Isolated,
    /// <summary>Default route whose target was deleted: traffic is dropped.</summary>
    Blackhole,
}

/// <summary>How one subnet reaches the internet, derived from its route table.</summary>
/// <param name="Via">The default route's target (igw-…, nat-…, tgw-…), if any.</param>
public sealed record SubnetRouting(SubnetInfo Subnet, RouteTableInfo? RouteTable, bool ExplicitAssociation, InternetAccess Access, string? Via,
    string Summary, string? Ipv6Summary, string? Warning, string? Note = null);
