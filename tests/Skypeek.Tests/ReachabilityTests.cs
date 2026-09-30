using System.Net;
using Skypeek.Core.Models;

namespace Skypeek.Tests;

public class ReachabilityTests
{
    private static SecurityGroupRuleInfo In(string group, int port, string? cidr = null, string? fromGroup = null) => new()
    {
        RuleId = $"sgr-{group}-{port}-{cidr ?? fromGroup}", GroupId = group, Protocol = "tcp", FromPort = port, ToPort = port, CidrIpv4 = cidr, ReferencedGroupId = fromGroup,
    };

    private static SecurityGroupRuleInfo OutAll(string group) => new() { RuleId = $"sgr-{group}-out", GroupId = group, IsEgress = true, Protocol = "-1", CidrIpv4 = "0.0.0.0/0" };

    private static NetworkAclInfo AllowAll(string vpc, params string[] subnets) => new()
    {
        Id = $"acl-{vpc}", VpcId = vpc, IsDefault = true, SubnetIds = subnets.ToList(),
        Entries =
        [
            new() { RuleNumber = 100, Egress = false, Protocol = "-1", Cidr = "0.0.0.0/0", Allow = true },
            new() { RuleNumber = 100, Egress = true, Protocol = "-1", Cidr = "0.0.0.0/0", Allow = true },
            new() { RuleNumber = 32767, Egress = false, Protocol = "-1", Cidr = "0.0.0.0/0", Allow = false },
            new() { RuleNumber = 32767, Egress = true, Protocol = "-1", Cidr = "0.0.0.0/0", Allow = false },
        ],
    };

    private static NetworkInterfaceInfo Eni(string id, string ip, string vpc, string subnet, string group, string? publicIp = null) => new()
    {
        Id = id, PrivateIp = ip, PrivateIps = [ip], VpcId = vpc, SubnetId = subnet, SecurityGroups = [new(group, group)], PublicIps = publicIp is null ? [] : [publicIp], Status = "in-use",
    };

    /// <summary>vpc-a 10.0.0.0/16: app (10.0.1.10) and db (10.0.2.20); db allows 5432 from sg-app; peering to vpc-b.</summary>
    private static NetworkSnapshot VpcA(bool routeToPeer = true) => new()
    {
        TargetId = 1,
        Vpcs = [new VpcInfo { Id = "vpc-a", Cidrs = ["10.0.0.0/16"] }],
        Subnets = [new SubnetInfo { Id = "subnet-app", VpcId = "vpc-a", Cidr = "10.0.1.0/24" }, new SubnetInfo { Id = "subnet-db", VpcId = "vpc-a", Cidr = "10.0.2.0/24" }],
        Interfaces = [Eni("eni-app", "10.0.1.10", "vpc-a", "subnet-app", "sg-app", publicIp: "54.1.2.3"), Eni("eni-db", "10.0.2.20", "vpc-a", "subnet-db", "sg-db")],
        SecurityGroups =
        [
            new SecurityGroupInfo { Id = "sg-app", Name = "app", VpcId = "vpc-a", Rules = [OutAll("sg-app"), In("sg-app", 443, "0.0.0.0/0")] },
            new SecurityGroupInfo { Id = "sg-db", Name = "db", VpcId = "vpc-a", Rules = [OutAll("sg-db"), In("sg-db", 5432, fromGroup: "sg-app"), In("sg-db", 5432, "10.1.0.0/16")] },
        ],
        RouteTables =
        [
            new RouteTableInfo
            {
                Id = "rtb-a", VpcId = "vpc-a", IsMain = true,
                Routes =
                [
                    new RouteInfo { Destination = "10.0.0.0/16", Target = "local", State = "active" },
                    new RouteInfo { Destination = "0.0.0.0/0", Target = "igw-a", State = "active" },
                    .. routeToPeer ? [new RouteInfo { Destination = "10.1.0.0/16", Target = "pcx-ab", State = "active" }] : Array.Empty<RouteInfo>(),
                    new RouteInfo { Destination = "172.16.0.0/12", Target = "tgw-1", State = "active" },
                ],
            },
        ],
        Peerings = [new PeeringInfo { Id = "pcx-ab", RequesterVpcId = "vpc-a", AccepterVpcId = "vpc-b", Status = "active" }],
        NetworkAcls = [AllowAll("vpc-a", "subnet-app", "subnet-db")],
    };

    /// <summary>vpc-b 10.1.0.0/16 (another target): a worker at 10.1.5.5.</summary>
    private static NetworkSnapshot VpcB(bool routeBack) => new()
    {
        TargetId = 2,
        Vpcs = [new VpcInfo { Id = "vpc-b", Cidrs = ["10.1.0.0/16"] }],
        Subnets = [new SubnetInfo { Id = "subnet-w", VpcId = "vpc-b", Cidr = "10.1.5.0/24" }],
        Interfaces = [Eni("eni-w", "10.1.5.5", "vpc-b", "subnet-w", "sg-w")],
        SecurityGroups = [new SecurityGroupInfo { Id = "sg-w", Name = "worker", VpcId = "vpc-b", Rules = [OutAll("sg-w")] }],
        RouteTables =
        [
            new RouteTableInfo
            {
                Id = "rtb-b", VpcId = "vpc-b", IsMain = true,
                Routes = [new RouteInfo { Destination = "10.1.0.0/16", Target = "local", State = "active" },
                    .. routeBack ? [new RouteInfo { Destination = "10.0.0.0/16", Target = "pcx-ab", State = "active" }] : Array.Empty<RouteInfo>()],
            },
        ],
        Peerings = [new PeeringInfo { Id = "pcx-ab", RequesterVpcId = "vpc-a", AccepterVpcId = "vpc-b", Status = "active" }],
        NetworkAcls = [AllowAll("vpc-b", "subnet-w")],
    };

    private static ReachEndpoint E(NetworkSnapshot s, string eni) => ReachEndpoint.Of(s.TargetId, s.Interfaces.Single(i => i.Id == eni));

    private static ReachResult Check(IReadOnlyList<NetworkSnapshot> nets, ReachEndpoint from, ReachEndpoint to, int port, string protocol = "tcp") =>
        ReachabilityAnalyzer.Analyze(new ReachRequest(from, to, protocol, port), nets);

    [Fact]
    public void Same_vpc_uses_the_security_group_reference()
    {
        var a = VpcA();
        var ok = Check([a], E(a, "eni-app"), E(a, "eni-db"), 5432);
        Assert.Equal(ReachVerdict.Reachable, ok.Verdict);
        Assert.Contains(ok.Hops, h => h.Detail.Contains("the peer is in sg-app"));

        var wrongPort = Check([a], E(a, "eni-app"), E(a, "eni-db"), 3306);
        Assert.Equal(ReachVerdict.Blocked, wrongPort.Verdict);
        Assert.Equal("Destination security groups (inbound)", wrongPort.Hops.First(h => h.Result == HopResult.Block).Step);
        Assert.Equal("sg-db", wrongPort.Hops.First(h => h.Result == HopResult.Block).GroupId);
    }

    [Fact]
    public void Network_acl_deny_blocks_and_missing_reply_ports_block_too()
    {
        var a = VpcA();
        a.NetworkAcls[0].Entries.Insert(0, new NetworkAclEntryInfo { RuleNumber = 90, Egress = false, Protocol = "6", FromPort = 5432, ToPort = 5432, Cidr = "10.0.1.0/24", Allow = false });
        var denied = Check([a], E(a, "eni-app"), E(a, "eni-db"), 5432);
        Assert.Equal(ReachVerdict.Blocked, denied.Verdict);
        Assert.Contains("rule #90 deny", denied.Hops.First(h => h.Result == HopResult.Block).Detail);

        // Stateless: an ACL that allows only 5432 out of the db subnet drops the replies to the app.
        var b = VpcA();
        var acl = new NetworkAclInfo
        {
            Id = "acl-db", VpcId = "vpc-a", SubnetIds = ["subnet-db"],
            Entries = [new() { RuleNumber = 100, Egress = false, Protocol = "-1", Cidr = "0.0.0.0/0", Allow = true },
                       new() { RuleNumber = 100, Egress = true, Protocol = "6", FromPort = 5432, ToPort = 5432, Cidr = "0.0.0.0/0", Allow = true }],
        };
        b.NetworkAcls = [acl, AllowAll("vpc-a", "subnet-app")];
        var noReply = Check([b], E(b, "eni-app"), E(b, "eni-db"), 5432);
        Assert.Equal(ReachVerdict.Blocked, noReply.Verdict);
        Assert.StartsWith("Reply: destination network ACL", noReply.Hops.First(h => h.Result == HopResult.Block).Step);
    }

    [Fact]
    public void Peering_needs_routes_both_ways()
    {
        var a = VpcA();
        var ok = Check([a, VpcB(routeBack: true)], E(VpcB(true), "eni-w"), E(a, "eni-db"), 5432);
        Assert.Equal(ReachVerdict.Reachable, ok.Verdict);

        var noRouteBack = Check([VpcA(), VpcB(routeBack: false)], E(VpcB(false), "eni-w"), E(VpcA(), "eni-db"), 5432);
        Assert.Equal(ReachVerdict.Blocked, noRouteBack.Verdict);
        Assert.Contains("No route to", noRouteBack.Hops.First(h => h.Result == HopResult.Block).Detail);

        var a2 = VpcA(routeToPeer: false);
        var noReturn = Check([a2, VpcB(routeBack: true)], E(VpcB(true), "eni-w"), E(a2, "eni-db"), 5432);
        Assert.Equal(ReachVerdict.Blocked, noReturn.Verdict);
        Assert.Equal("Reply: route back to the source", noReturn.Hops.First(h => h.Result == HopResult.Block).Step);
    }

    [Fact]
    public void Internet_and_transit_gateway_paths()
    {
        var a = VpcA();
        // From the internet to the app's open HTTPS: the route in is outside the data, the rest passes.
        var fromInternet = Check([a], new ReachEndpoint("203.0.113.5", IPAddress.Parse("203.0.113.5")), E(a, "eni-app"), 443);
        Assert.Equal(ReachVerdict.ProbablyReachable, fromInternet.Verdict);
        Assert.DoesNotContain(fromInternet.Hops, h => h.Result == HopResult.Block);

        // Out to the internet through the internet gateway (the app has a public IP).
        var toInternet = Check([a], E(a, "eni-app"), new ReachEndpoint("1.1.1.1", IPAddress.Parse("1.1.1.1")), 443);
        Assert.Contains(toInternet.Hops, h => h.Detail.Contains("seen on the internet as 54.1.2.3"));

        // The db has no public IP: the internet gateway route does not help it.
        var dbOut = Check([a], E(a, "eni-db"), new ReachEndpoint("1.1.1.1", IPAddress.Parse("1.1.1.1")), 443);
        Assert.Equal(ReachVerdict.Blocked, dbOut.Verdict);

        var viaTgw = Check([a], E(a, "eni-app"), new ReachEndpoint("172.20.0.9", IPAddress.Parse("172.20.0.9")), 443);
        Assert.Contains(viaTgw.Hops, h => h.Result == HopResult.Unknown && h.Detail.Contains("transit gateway"));
    }

    [Fact]
    public void Longest_prefix_wins()
    {
        var a = VpcA();
        a.RouteTables[0].Routes.Add(new RouteInfo { Destination = "10.1.5.0/24", Target = "tgw-2", State = "active" });
        Assert.Equal("tgw-2", ReachabilityAnalyzer.LookupRoute(a, "subnet-app", IPAddress.Parse("10.1.5.5"))!.Target);
        Assert.Equal("pcx-ab", ReachabilityAnalyzer.LookupRoute(a, "subnet-app", IPAddress.Parse("10.1.9.9"))!.Target);
        Assert.Equal("igw-a", ReachabilityAnalyzer.LookupRoute(a, "subnet-app", IPAddress.Parse("8.8.8.8"))!.Target);
    }
}
