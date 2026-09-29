using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.App.Views;

/// <summary>A target on the Network tab: when its inventory was downloaded and what it contains.</summary>
public sealed record NetworkTargetDetail(Target Target, NetworkSnapshot? Snapshot, string Status)
{
    public string Summary => Snapshot is not { } s ? "Not downloaded yet."
        : $"{s.Vpcs.Count} VPC(s), {s.Subnets.Count} subnet(s), {s.Interfaces.Count} network interface(s), {s.SecurityGroups.Count} security group(s), {s.ElasticIps.Count} Elastic IP(s).";
}

public sealed record VpcDetail(Target Target, VpcInfo Vpc, int Subnets, int Interfaces, int Groups)
{
    public string ConsoleUrl => $"https://{Target.Region}.console.aws.amazon.com/vpcconsole/home?region={Target.Region}#VpcDetails:VpcId={Vpc.Id}";
    public string CidrText => string.Join(", ", Vpc.Cidrs.Concat(Vpc.Ipv6Cidrs));
}

public sealed record SubnetDetail(Target Target, SubnetInfo Subnet, IReadOnlyList<NetworkInterfaceInfo> Interfaces)
{
    public string ConsoleUrl => $"https://{Target.Region}.console.aws.amazon.com/vpcconsole/home?region={Target.Region}#SubnetDetails:subnetId={Subnet.Id}";
}

public sealed record InterfaceDetail(Target Target, NetworkInterfaceInfo Interface, SubnetInfo? Subnet, ElasticIpInfo? ElasticIp)
{
    public string ConsoleUrl => $"https://{Target.Region}.console.aws.amazon.com/ec2/home?region={Target.Region}#NetworkInterface:networkInterfaceId={Interface.Id}";
    public bool HasInstance => Interface.InstanceId is not null;
}

public sealed record ElasticIpDetail(Target Target, ElasticIpInfo Address, NetworkInterfaceInfo? Interface)
{
    public string AttachedText => Interface is { } eni ? $"{eni.Owner} · {eni.Id} · {Address.PrivateIp}" : Address.InstanceId ?? "not associated (billed while idle)";
}

/// <summary>One rule row with what the security group details need (edit, delete, go to the referenced group).</summary>
public sealed record RuleRow(SecurityGroupRuleInfo Rule, string? SourceName)
{
    public string SourceText => SourceName is { } name ? $"{Rule.SourceText} ({name})" : Rule.SourceText;
    public bool IsGroupSource => Rule.SourceKind == RuleSourceKind.SecurityGroup;
}

public sealed record SecurityGroupDetail(Target Target, SecurityGroupInfo Group, IReadOnlyList<RuleRow> Inbound, IReadOnlyList<RuleRow> Outbound,
    IReadOnlyList<NetworkInterfaceInfo> UsedBy, IReadOnlyList<SecurityGroupInfo> ReferencedBy, bool CanEdit, DateTime DownloadedUtc)
{
    public string ConsoleUrl => Group.ConsoleUrl(Target.Region);
    public string Summary => $"Security group · {Group.VpcId} · {Target.DisplayName} · downloaded {DownloadedUtc.ToLocalTime():g}";
    public string UsedByText => UsedBy.Count == 0 ? "No network interface uses this group." : $"{UsedBy.Count} network interface(s) use this group:";
    public string EditNote => CanEdit
        ? "Adding, changing or deleting a rule uses the elevated key and asks for confirmation each time. Deleting, or opening to 0.0.0.0/0 or ::/0, asks you to type the group ID."
        : "Set an elevated key for this target (Settings → Accounts & regions) to edit rules.";
}

public static class NetworkTreeBuilder
{
    public static List<DashNode> Build(AppSession session)
    {
        var roots = new List<DashNode>();
        foreach (var target in session.Settings.Targets.Where(t => t.NetworkEnabled).OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = session.Network.Get(target.Id);
            var job = session.Scheduler.GetState(target.Id, JobKind.Network);
            var status = job.Running ? "downloading…"
                : snapshot is null ? (job.LastFailed ? $"download failed: {job.LastError}" : "not downloaded yet")
                : $"downloaded {snapshot.DownloadedUtc.ToLocalTime():g}" + (job.LastFailed ? $" · last download failed: {job.LastError}" : snapshot.Error is { } e ? $" · {e}" : "");
            var root = new DashNode
            {
                Key = $"{target.Id}",
                Kind = NodeKind.Target,
                Title = target.DisplayName,
                Subtitle = $"{target.Region} · {status}",
                SearchText = target.ProfileName,
                Level = job.LastFailed || snapshot?.Error is not null ? HealthLevel.Warn : HealthLevel.Ok,
                Payload = new NetworkTargetDetail(target, snapshot, status),
            };
            if (snapshot is not null)
                AddVpcs(root, target, snapshot);
            roots.Add(root);
        }
        return roots;
    }

    private static void AddVpcs(DashNode root, Target target, NetworkSnapshot s)
    {
        var canEdit = target.ElevatedProfileName is { Length: > 0 };
        var groupsById = s.SecurityGroups.ToDictionary(g => g.Id);
        var eips = s.ElasticIps.Where(a => a.NetworkInterfaceId is not null).GroupBy(a => a.NetworkInterfaceId!).ToDictionary(g => g.Key, g => g.First());
        var subnetsById = s.Subnets.ToDictionary(x => x.Id);

        foreach (var vpc in s.Vpcs.OrderByDescending(v => v.Name is not null).ThenBy(v => v.Title, StringComparer.OrdinalIgnoreCase))
        {
            var subnets = s.Subnets.Where(x => x.VpcId == vpc.Id).OrderBy(x => x.AvailabilityZone).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
            var groups = s.SecurityGroups.Where(g => g.VpcId == vpc.Id).ToList();
            var interfaces = s.Interfaces.Where(i => i.VpcId == vpc.Id).ToList();
            var vpcNode = new DashNode
            {
                Key = $"{target.Id}:vpc:{vpc.Id}",
                Kind = NodeKind.Vpc,
                Title = vpc.Title,
                Subtitle = $"{string.Join(", ", vpc.Cidrs)}{(vpc.IsDefault ? " · default VPC" : "")} · {subnets.Count} subnet(s) · {interfaces.Count} interface(s)",
                SearchText = $"{vpc.Id} {string.Join(' ', vpc.Ipv6Cidrs)}",
                Payload = new VpcDetail(target, vpc, subnets.Count, interfaces.Count, groups.Count),
            };

            var subnetGroup = new DashNode
            {
                Key = $"{vpcNode.Key}:subnets",
                Kind = NodeKind.Group,
                Title = "Subnets",
                Right = $"{subnets.Count}",
                Payload = new MessageDetail("Subnets with their address ranges and how many addresses are in use. Each subnet lists the network interfaces (IP addresses) inside it."),
            };
            foreach (var subnet in subnets)
            {
                var inSubnet = interfaces.Where(i => i.SubnetId == subnet.Id).OrderBy(i => IpSortKey(i.PrivateIp)).ToList();
                var level = subnet.UsageLevel;
                var subnetNode = new DashNode
                {
                    Key = $"{target.Id}:subnet:{subnet.Id}",
                    Kind = NodeKind.Subnet,
                    Title = subnet.Title,
                    Subtitle = $"{subnet.Cidr} · {subnet.AvailabilityZone} · {subnet.Kind}",
                    Gauges = subnet.UsedPercent is { } used ? [new Gauge("IPs", used, $"{subnet.UsedIps}/{subnet.UsableIps}", level)] : [],
                    SearchText = $"{subnet.Id} {string.Join(' ', subnet.Ipv6Cidrs)}",
                    Level = level,
                    Payload = new SubnetDetail(target, subnet, inSubnet),
                };
                foreach (var eni in inSubnet)
                    subnetNode.Children.Add(InterfaceNode(target, eni, subnet, eips.GetValueOrDefault(eni.Id)));
                subnetGroup.Children.Add(subnetNode);
            }
            vpcNode.Children.Add(subnetGroup);

            var sgGroup = new DashNode
            {
                Key = $"{vpcNode.Key}:sgs",
                Kind = NodeKind.Group,
                Title = "Security groups",
                Right = $"{groups.Count}",
                Payload = new MessageDetail("Security groups of this VPC with their inbound and outbound rules. Select one to see, add, change or delete rules."),
            };
            foreach (var group in groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                sgGroup.Children.Add(GroupNode(target, group, s, groupsById, canEdit));
            vpcNode.Children.Add(sgGroup);
            root.Children.Add(vpcNode);
        }

        // Groups and interfaces outside any listed VPC (EC2-Classic leftovers, or a VPC that could not be read).
        var orphanGroups = s.SecurityGroups.Where(g => g.VpcId is null || s.Vpcs.All(v => v.Id != g.VpcId)).ToList();
        if (orphanGroups.Count > 0)
        {
            var other = new DashNode { Key = $"{target.Id}:sgs:other", Kind = NodeKind.Group, Title = "Other security groups", Right = $"{orphanGroups.Count}", Payload = new MessageDetail("Security groups whose VPC is not listed.") };
            foreach (var group in orphanGroups)
                other.Children.Add(GroupNode(target, group, s, groupsById, canEdit));
            root.Children.Add(other);
        }

        if (s.ElasticIps.Count > 0)
        {
            var eipNode = new DashNode
            {
                Key = $"{target.Id}:eips",
                Kind = NodeKind.Group,
                Title = "Elastic IPs",
                Right = $"{s.ElasticIps.Count}",
                Payload = new MessageDetail("Static public addresses of the account in this region. An Elastic IP that is not associated is billed while idle."),
            };
            var interfacesById = s.Interfaces.ToDictionary(i => i.Id);
            foreach (var a in s.ElasticIps.OrderBy(a => IpSortKey(a.PublicIp)))
            {
                var eni = a.NetworkInterfaceId is { } id ? interfacesById.GetValueOrDefault(id) : null;
                var detail = new ElasticIpDetail(target, a, eni);
                eipNode.Children.Add(new DashNode
                {
                    Key = $"{target.Id}:eip:{a.AllocationId ?? a.PublicIp}",
                    Kind = NodeKind.ElasticIp,
                    Title = a.PublicIp,
                    Subtitle = $"{a.Name}{(a.Name is null ? "" : " · ")}{detail.AttachedText}",
                    SearchText = $"{a.AllocationId} {a.PrivateIp} {a.InstanceId}",
                    Level = a.IsAssociated ? HealthLevel.Ok : HealthLevel.Warn,
                    Payload = detail,
                });
            }
            root.Children.Add(eipNode);
        }
    }

    private static DashNode InterfaceNode(Target target, NetworkInterfaceInfo eni, SubnetInfo? subnet, ElasticIpInfo? eip) => new()
    {
        Key = $"{target.Id}:eni:{eni.Id}",
        Kind = NodeKind.NetworkInterface,
        Title = eni.PrivateIp ?? eni.Id,
        Subtitle = string.Join(" · ", new[]
        {
            eni.Owner,
            eni.PublicIps.Count > 0 ? $"public {string.Join(", ", eni.PublicIps)}" : null,
            eni.PrivateIps.Count > 1 ? $"+{eni.PrivateIps.Count - 1} secondary" : null,
            eni.Id,
        }.Where(x => !string.IsNullOrEmpty(x))),
        SearchText = string.Join(' ', eni.AllIps.Concat([eni.InstanceId ?? "", eni.Description ?? "", eni.Name ?? "", eni.InterfaceType ?? ""])
            .Concat(eni.SecurityGroups.SelectMany(g => new[] { g.Id, g.Name }))),
        Level = eni.Status == "available" ? HealthLevel.Unknown : HealthLevel.Ok,
        Dim = eni.Status == "available",
        Payload = new InterfaceDetail(target, eni, subnet, eip),
    };

    private static DashNode GroupNode(Target target, SecurityGroupInfo group, NetworkSnapshot s, Dictionary<string, SecurityGroupInfo> groupsById, bool canEdit)
    {
        RuleRow Row(SecurityGroupRuleInfo rule) => new(rule, rule.ReferencedGroupId is { } id && groupsById.TryGetValue(id, out var g) ? g.Name : null);
        var usedBy = s.Interfaces.Where(i => i.SecurityGroups.Any(x => x.Id == group.Id)).OrderBy(i => i.Owner).ToList();
        var referencedBy = s.SecurityGroups.Where(g => g.Id != group.Id && g.Rules.Any(r => r.ReferencedGroupId == group.Id)).ToList();
        var open = group.OpenToWorldCount;
        return new DashNode
        {
            Key = SecurityGroupKey(target.Id, group.Id),
            Kind = NodeKind.SecurityGroup,
            Title = group.Title,
            Subtitle = $"{group.Id} · {group.Inbound.Count()} in / {group.Outbound.Count()} out · used by {usedBy.Count}{(open > 0 ? $" · {open} open to the internet" : "")}",
            SearchText = $"{group.Description} {string.Join(' ', group.Rules.Select(r => $"{r.Source} {r.PortText} {r.Description}"))}",
            Level = open > 0 && group.Inbound.Any(r => r.IsOpenToWorld && r.Protocol == "-1") ? HealthLevel.Warn : HealthLevel.Ok,
            Payload = new SecurityGroupDetail(target, group, group.Inbound.Select(Row).ToList(), group.Outbound.Select(Row).ToList(), usedBy, referencedBy, canEdit, s.DownloadedUtc),
        };
    }

    public static string SecurityGroupKey(long targetId, string groupId) => $"{targetId}:sg:{groupId}";

    /// <summary>Numeric order for IPv4 addresses (10.0.0.9 before 10.0.0.10).</summary>
    private static string IpSortKey(string? ip) =>
        ip is not null && System.Net.IPAddress.TryParse(ip, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? string.Concat(address.GetAddressBytes().Select(b => b.ToString("D3")))
            : ip ?? "";
}
