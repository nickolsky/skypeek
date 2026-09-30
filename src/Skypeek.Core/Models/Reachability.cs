using System.Net;

namespace Skypeek.Core.Models;

/// <summary>
/// One end of a reachability check: a network interface in a downloaded VPC, or a bare address (on-premises, the
/// internet, or anything Skypeek has no network data for).
/// </summary>
public sealed record ReachEndpoint(string Label, IPAddress Address, long? TargetId = null, NetworkInterfaceInfo? Interface = null)
{
    public bool InAws => Interface is not null;
    public string? VpcId => Interface?.VpcId;
    public string? SubnetId => Interface?.SubnetId;
    public IReadOnlyList<string> GroupIds => Interface?.SecurityGroups.Select(g => g.Id).ToList() ?? [];
    public string Text => Label == Address.ToString() ? Label : $"{Label} ({Address})";

    public static ReachEndpoint Of(long targetId, NetworkInterfaceInfo eni, string? label = null) =>
        new(label ?? $"{eni.Owner} {eni.PrivateIp}", IPAddress.Parse(eni.PrivateIp ?? eni.PrivateIps.First()), targetId, eni);
}

/// <param name="Protocol">tcp, udp, icmp or -1 (all).</param>
public sealed record ReachRequest(ReachEndpoint Source, ReachEndpoint Destination, string Protocol, int Port)
{
    public string Text => $"{Source.Text} → {Destination.Text} on {NetworkRules.ProtocolText(Protocol)} {(Protocol is "tcp" or "udp" ? $"port {Port}" : "")}".TrimEnd();
}

public enum HopResult { Pass, Block, Unknown, Skipped }

/// <param name="GroupId">The security group that decided (links to it on the Network tab).</param>
/// <param name="SubnetId">The subnet whose route table or network ACL decided.</param>
public sealed record ReachHop(string Step, HopResult Result, string Detail, long? TargetId = null, string? GroupId = null, string? SubnetId = null)
{
    public HealthLevel Level => Result switch
    {
        HopResult.Pass => HealthLevel.Ok,
        HopResult.Block => HealthLevel.Critical,
        HopResult.Unknown => HealthLevel.Warn,
        _ => HealthLevel.Unknown,
    };
    public string ResultText => Result switch { HopResult.Pass => "passes", HopResult.Block => "BLOCKED", HopResult.Unknown => "not checked", _ => "n/a" };
}

public enum ReachVerdict { Reachable, Blocked, ProbablyReachable }

/// <summary>What AWS Reachability Analyzer answered.</summary>
public sealed record AwsReachResult(bool? PathFound, string Status, IReadOnlyList<string> Explanations, IReadOnlyList<string> Path)
{
    public string Summary => PathFound switch
    {
        true => "AWS Reachability Analyzer: reachable.",
        false => $"AWS Reachability Analyzer: not reachable{(Explanations.Count > 0 ? $" — {Explanations[0]}" : "")}.",
        _ => $"AWS Reachability Analyzer: {Status}.",
    };
}

public sealed record ReachResult(ReachRequest Request, IReadOnlyList<ReachHop> Hops, ReachVerdict Verdict)
{
    public string Summary => Verdict switch
    {
        ReachVerdict.Reachable => "Every security group, network ACL and route on the way allows it.",
        ReachVerdict.Blocked => $"At {Hops.First(h => h.Result == HopResult.Block).Step.ToLowerInvariant()}: {Hops.First(h => h.Result == HopResult.Block).Detail}",
        _ => "Everything Skypeek can see allows it, but some hops could not be checked (see below). "
             + "Firewalls on the hosts themselves are never visible.",
    };
    public HealthLevel Level => Verdict switch { ReachVerdict.Reachable => HealthLevel.Ok, ReachVerdict.Blocked => HealthLevel.Critical, _ => HealthLevel.Warn };
}

/// <summary>
/// Works out whether one endpoint can open a connection to another from the downloaded network data: security groups
/// (stateful) on both ends, network ACLs (stateless, so the reply is checked too) and the routes in between (local,
/// peering, internet and NAT gateways; transit gateways, VPNs and appliances are reported as not checked).
/// </summary>
public static class ReachabilityAnalyzer
{
    private const int EphemeralLow = 32768;
    private const int EphemeralHigh = 60999;

    public static ReachResult Analyze(ReachRequest request, IReadOnlyList<NetworkSnapshot> snapshots)
    {
        var hops = new List<ReachHop>();
        var src = request.Source;
        var dst = request.Destination;
        var srcNet = Snapshot(snapshots, src);
        var dstNet = Snapshot(snapshots, dst);
        var protocol = NormalizeProtocol(request.Protocol);

        // What the destination sees as the source address (a NAT gateway or public IP translates it).
        var seenSource = src.Address;
        var viaInternet = false;

        // 1. Source security groups (outbound).
        if (src.InAws && srcNet is not null)
            hops.Add(GroupCheck("Source security groups (outbound)", srcNet, src, dst.Address, dst.GroupIds, protocol, request.Port, egress: true));
        else
            hops.Add(new ReachHop("Source security groups", HopResult.Skipped, "The source is outside the downloaded VPCs (no security group)."));

        // 2. Source subnet network ACL (outbound).
        if (src.InAws && srcNet is not null && src.SubnetId is { } srcSubnet)
            hops.Add(AclCheck("Source network ACL (outbound)", srcNet, srcSubnet, dst.Address, protocol, request.Port, request.Port, egress: true));

        // 3. Routing from the source's subnet.
        var sameVpc = src.InAws && dst.InAws && src.VpcId == dst.VpcId && src.TargetId == dst.TargetId;
        if (sameVpc)
            hops.Add(new ReachHop("Route", HopResult.Pass, $"Both are in {src.VpcId}: the local route applies."));
        else if (src.InAws && srcNet is not null && src.SubnetId is { } subnetId)
        {
            var (hop, translated, internet) = RouteCheck(srcNet, subnetId, dst, dstNet, src);
            hops.Add(hop);
            if (translated is not null)
                seenSource = translated;
            viaInternet = internet;
        }
        else if (!src.InAws && dst.InAws)
            hops.Add(new ReachHop("Route to the destination", HopResult.Unknown,
                $"How {src.Address} reaches {dst.VpcId} (VPN, Direct Connect, transit gateway, internet) is outside the downloaded data."));

        // 4 and 5. Destination network ACL (inbound) and security groups (inbound).
        if (dst.InAws && dstNet is not null)
        {
            if (dst.SubnetId is { } dstSubnet)
                hops.Add(AclCheck("Destination network ACL (inbound)", dstNet, dstSubnet, seenSource, protocol, request.Port, request.Port, egress: false));
            // Security group references work within a VPC (and across peerings), never through the internet or NAT.
            var sourceGroups = viaInternet || !src.InAws ? [] : src.GroupIds;
            hops.Add(GroupCheck("Destination security groups (inbound)", dstNet, dst, seenSource, sourceGroups, protocol, request.Port, egress: false));
        }
        else if (dst.InAws)
            hops.Add(new ReachHop("Destination", HopResult.Unknown, $"The network data of {dst.VpcId} has not been downloaded."));
        else
            hops.Add(new ReachHop("Destination", HopResult.Unknown, $"{dst.Address} is outside the downloaded VPCs: its own firewall is not visible."));

        // 6. The reply: network ACLs are stateless, so the ephemeral ports must be open the other way.
        if (protocol is "6" or "17")
        {
            if (dst.InAws && dstNet is not null && dst.SubnetId is { } dstSubnet)
                hops.Add(AclCheck("Reply: destination network ACL (outbound)", dstNet, dstSubnet, seenSource, protocol, EphemeralLow, EphemeralHigh, egress: true));
            if (src.InAws && srcNet is not null && src.SubnetId is { } sourceSubnet)
                hops.Add(AclCheck("Reply: source network ACL (inbound)", srcNet, sourceSubnet, dst.Address, protocol, EphemeralLow, EphemeralHigh, egress: false));
            if (!sameVpc && dst.InAws && dstNet is not null && dst.SubnetId is { } back && !viaInternet)
                hops.Add(ReturnRouteCheck(dstNet, back, src));
        }

        var verdict = hops.Any(h => h.Result == HopResult.Block) ? ReachVerdict.Blocked
            : hops.Any(h => h.Result == HopResult.Unknown) ? ReachVerdict.ProbablyReachable
            : ReachVerdict.Reachable;
        return new ReachResult(request, hops, verdict);
    }

    private static NetworkSnapshot? Snapshot(IReadOnlyList<NetworkSnapshot> snapshots, ReachEndpoint e) =>
        e.TargetId is { } id ? snapshots.FirstOrDefault(s => s.TargetId == id) : null;

    private static readonly IpNet[] PrivateRanges =
        new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "fc00::/7" }.Select(c => IpNet.Parse(c)!.Value).ToArray();

    /// <summary>RFC 1918, carrier-grade NAT and IPv6 unique local addresses: never routed on the internet.</summary>
    public static bool IsPrivate(IPAddress address) => PrivateRanges.Any(r => r.Contains(address));

    public static string NormalizeProtocol(string protocol) => protocol.ToLowerInvariant() switch
    {
        "tcp" => "6",
        "udp" => "17",
        "icmp" => "1",
        "icmpv6" => "58",
        "all" => "-1",
        var p => p,
    };

    private static bool ProtocolMatches(string ruleProtocol, string protocol, int? from, int? to, int portLow, int portHigh)
    {
        var rule = NormalizeProtocol(ruleProtocol);
        if (rule == "-1")
            return true;
        if (rule != protocol && protocol != "-1")
            return false;
        if (rule is not ("6" or "17"))
            return true; // ICMP and others: no ports
        if (from is null && to is null)
            return true;
        return (from ?? 0) <= portLow && portHigh <= (to ?? 65535);
    }

    private static IEnumerable<string> RuleCidrs(NetworkSnapshot s, SecurityGroupRuleInfo r) =>
        r.CidrIpv4 is { } v4 ? [v4] : r.CidrIpv6 is { } v6 ? [v6]
        : r.PrefixListId is { } pl ? s.PrefixLists.FirstOrDefault(p => p.Id == pl)?.Cidrs ?? [] : [];

    private static ReachHop GroupCheck(string step, NetworkSnapshot s, ReachEndpoint self, IPAddress peer, IReadOnlyList<string> peerGroups,
        string protocol, int port, bool egress)
    {
        var groups = s.SecurityGroups.Where(g => self.GroupIds.Contains(g.Id)).ToList();
        if (groups.Count == 0)
            return new ReachHop(step, self.GroupIds.Count == 0 ? HopResult.Skipped : HopResult.Unknown,
                self.GroupIds.Count == 0 ? "No security group on this interface." : $"{string.Join(", ", self.GroupIds)} not in the downloaded data.", s.TargetId);
        var peerNet = new IpNet(peer, peer.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        var unresolvedPrefixList = false;
        foreach (var g in groups)
            foreach (var r in g.Rules.Where(r => r.IsEgress == egress))
            {
                if (!ProtocolMatches(r.Protocol, protocol, r.FromPort, r.ToPort, port, port))
                    continue;
                if (r.ReferencedGroupId is { } refId && peerGroups.Contains(refId))
                    return new ReachHop(step, HopResult.Pass, $"{g.Title} ({g.Id}): {r.Summary} — the peer is in {refId}", s.TargetId, g.Id);
                if (r.PrefixListId is { } pl && s.PrefixLists.All(p => p.Id != pl))
                    unresolvedPrefixList = true;
                if (RuleCidrs(s, r).Any(c => IpNet.Parse(c) is { } cidr && cidr.Contains(peerNet)))
                    return new ReachHop(step, HopResult.Pass, $"{g.Title} ({g.Id}): {r.Summary}", s.TargetId, g.Id);
            }
        if (unresolvedPrefixList)
            return new ReachHop(step, HopResult.Unknown, "Only a rule with a prefix list whose entries were not downloaded could allow it.", s.TargetId, groups[0].Id);
        return new ReachHop(step, HopResult.Block,
            $"No {(egress ? "outbound" : "inbound")} rule in {string.Join(", ", groups.Select(g => g.Title))} allows {NetworkRules.ProtocolText(protocol)}"
            + $"{(protocol is "6" or "17" ? $" port {port}" : "")} {(egress ? "to" : "from")} {peer}{(peerGroups.Count > 0 ? $" or its groups ({string.Join(", ", peerGroups)})" : "")}.",
            s.TargetId, groups[0].Id);
    }

    private static ReachHop AclCheck(string step, NetworkSnapshot s, string subnetId, IPAddress peer, string protocol, int portLow, int portHigh, bool egress)
    {
        var subnet = s.Subnets.FirstOrDefault(x => x.Id == subnetId);
        var acl = s.NetworkAcls.FirstOrDefault(a => a.SubnetIds.Contains(subnetId))
                  ?? (subnet is null ? null : s.NetworkAcls.FirstOrDefault(a => a.VpcId == subnet.VpcId && a.IsDefault));
        if (acl is null)
            return new ReachHop(step, HopResult.Unknown, "Network ACLs have not been downloaded (download the network data again).", s.TargetId, SubnetId: subnetId);
        var what = portLow == portHigh ? $"port {portLow}" : $"reply ports {portLow}-{portHigh}";
        foreach (var e in acl.Entries.Where(e => e.Egress == egress).OrderBy(e => e.RuleNumber))
        {
            if (IpNet.Parse(e.Cidr) is not { } cidr || !cidr.Contains(peer))
                continue;
            var rule = NormalizeProtocol(e.Protocol);
            if (rule != "-1" && rule != protocol)
                continue;
            if (rule is "6" or "17" && e.FromPort is { } from && e.ToPort is { } to)
            {
                if (to < portLow || from > portHigh)
                    continue;
                if (from > portLow || to < portHigh)
                    return new ReachHop(step, e.Allow ? HopResult.Unknown : HopResult.Block,
                        $"{acl.Title}: rule {e.Text} covers only part of {what}.", s.TargetId, SubnetId: subnetId);
            }
            return new ReachHop(step, e.Allow ? HopResult.Pass : HopResult.Block, $"{acl.Title}: rule {e.Text}", s.TargetId, SubnetId: subnetId);
        }
        return new ReachHop(step, HopResult.Block, $"{acl.Title}: no rule allows {what}; the final * rule denies it.", s.TargetId, SubnetId: subnetId);
    }

    /// <summary>Longest-prefix match in a subnet's route table (CIDR routes and prefix-list routes).</summary>
    public static RouteInfo? LookupRoute(NetworkSnapshot s, string subnetId, IPAddress address)
    {
        if (s.Subnets.FirstOrDefault(x => x.Id == subnetId) is not { } subnet || NetworkRules.RouteTableOf(s, subnet).Table is not { } table)
            return null;
        var best = (Route: (RouteInfo?)null, Length: -1);
        foreach (var route in table.Routes)
        {
            var cidrs = route.Destination.StartsWith("pl-", StringComparison.Ordinal)
                ? s.PrefixLists.FirstOrDefault(p => p.Id == route.Destination)?.Cidrs ?? []
                : [route.Destination];
            foreach (var c in cidrs)
                if (IpNet.Parse(c) is { } net && net.Contains(address) && net.PrefixLength > best.Length)
                    best = (route, net.PrefixLength);
        }
        return best.Route;
    }

    private static (ReachHop Hop, IPAddress? SeenAs, bool ViaInternet) RouteCheck(NetworkSnapshot s, string subnetId, ReachEndpoint dst, NetworkSnapshot? dstNet, ReachEndpoint src)
    {
        const string step = "Route from the source subnet";
        var route = LookupRoute(s, subnetId, dst.Address);
        if (route is null)
            return (new ReachHop(step, HopResult.Block, $"No route to {dst.Address} in the source subnet's route table.", s.TargetId, SubnetId: subnetId), null, false);
        if (route.IsBlackhole || route.Target is null)
            return (new ReachHop(step, HopResult.Block, $"{route.Text}: the target no longer exists (blackhole).", s.TargetId, SubnetId: subnetId), null, false);
        var target = route.Target;
        var label = NetworkRules.TargetLabel(s, target);
        if (target == "local")
            return dst.InAws
                ? (new ReachHop(step, HopResult.Pass, $"{route.Text}: local.", s.TargetId, SubnetId: subnetId), null, false)
                : (new ReachHop(step, HopResult.Unknown, $"{route.Text}: inside the VPC, but no downloaded interface has {dst.Address}.", s.TargetId, SubnetId: subnetId), null, false);
        if (target.StartsWith("pcx-", StringComparison.Ordinal))
        {
            var peering = s.Peerings.FirstOrDefault(p => p.Id == target);
            if (peering is { Status: not "active" })
                return (new ReachHop(step, HopResult.Block, $"{route.Text}: peering {target} is {peering.Status}.", s.TargetId, SubnetId: subnetId), null, false);
            return (new ReachHop(step, HopResult.Pass, $"{route.Text}: {label}.", s.TargetId, SubnetId: subnetId), null, false);
        }
        if (target.StartsWith("igw-", StringComparison.Ordinal))
        {
            if (IsPrivate(dst.Address))
                return (new ReachHop(step, HopResult.Block, $"{route.Text}: the best route is the internet gateway, which cannot reach the private address {dst.Address}.", s.TargetId, SubnetId: subnetId), null, true);
            var publicIp = src.Interface?.PublicIps.FirstOrDefault();
            return publicIp is null
                ? (new ReachHop(step, HopResult.Block, $"{route.Text}: through {label}, but the source has no public or Elastic IP, so its traffic cannot use it.", s.TargetId, SubnetId: subnetId), null, true)
                : (new ReachHop(step, HopResult.Pass, $"{route.Text}: through {label}, seen on the internet as {publicIp}.", s.TargetId, SubnetId: subnetId), IPAddress.Parse(publicIp), true);
        }
        if (target.StartsWith("nat-", StringComparison.Ordinal))
        {
            var nat = s.NatGateways.FirstOrDefault(n => n.Id == target);
            if (nat is { State: not "available" })
                return (new ReachHop(step, HopResult.Block, $"{route.Text}: NAT gateway {target} is {nat.State}.", s.TargetId, SubnetId: subnetId), null, false);
            var seen = nat?.IsPublic == true ? nat.PublicIps.FirstOrDefault() : nat?.PrivateIps.FirstOrDefault();
            return (new ReachHop(step, HopResult.Pass, $"{route.Text}: through {label}{(seen is null ? "" : $", seen as {seen}")}. Replies come back through it.", s.TargetId, SubnetId: subnetId),
                seen is null ? null : IPAddress.Parse(seen), nat?.IsPublic != false);
        }
        if (target.StartsWith("vpce-", StringComparison.Ordinal))
            return (new ReachHop(step, HopResult.Pass, $"{route.Text}: {label}.", s.TargetId, SubnetId: subnetId), null, false);
        var why = target.Split('-')[0] switch
        {
            "tgw" => "transit gateway route tables and attachments are not checked",
            "vgw" => "the other end of the VPN (on-premises routes and firewalls) is not visible",
            "eni" or "i" => "traffic goes through an appliance or NAT instance whose rules are not visible",
            _ => "this kind of route target is not checked",
        };
        return (new ReachHop(step, HopResult.Unknown, $"{route.Text}: {label} — {why}.", s.TargetId, SubnetId: subnetId), null, false);
    }

    private static ReachHop ReturnRouteCheck(NetworkSnapshot s, string subnetId, ReachEndpoint src)
    {
        const string step = "Reply: route back to the source";
        var route = LookupRoute(s, subnetId, src.Address);
        if (route is null)
            return new ReachHop(step, HopResult.Block, $"The destination subnet has no route back to {src.Address}, so replies are lost.", s.TargetId, SubnetId: subnetId);
        if (route.IsBlackhole || route.Target is null)
            return new ReachHop(step, HopResult.Block, $"{route.Text}: the route back is a blackhole.", s.TargetId, SubnetId: subnetId);
        if (route.Target.StartsWith("igw-", StringComparison.Ordinal) && IsPrivate(src.Address))
            return new ReachHop(step, HopResult.Block, $"The route back to {src.Address} is {route.Text}: an internet gateway cannot reach a private address, so replies are lost.", s.TargetId, SubnetId: subnetId);
        var known = route.Target == "local" || route.Target.StartsWith("pcx-", StringComparison.Ordinal);
        return new ReachHop(step, known ? HopResult.Pass : HopResult.Unknown, $"{route.Text}: {NetworkRules.TargetLabel(s, route.Target)}.", s.TargetId, SubnetId: subnetId);
    }
}
