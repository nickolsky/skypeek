namespace Skypeek.Core.Models;

public enum NetworkQueryKind { Address, Group }

/// <summary>A Network tab search that means more than text: an IP address, a CIDR, or a security group id.</summary>
public sealed record NetworkQuery(NetworkQueryKind Kind, IpNet? Net, string? GroupId)
{
    public static NetworkQuery? Parse(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith("sg-", StringComparison.OrdinalIgnoreCase) && t.Length > 5 && !t.Contains(' '))
            return new NetworkQuery(NetworkQueryKind.Group, null, t.ToLowerInvariant());
        return IpNet.TryParse(t, out var net) ? new NetworkQuery(NetworkQueryKind.Address, net, null) : null;
    }

    public string Text => Net?.IsSingleAddress == true ? Net.Value.Network.ToString() : Net?.ToString() ?? GroupId ?? "";
}

public sealed record InterfaceMatch(long TargetId, NetworkInterfaceInfo Interface);

public sealed record GroupMatch(long TargetId, SecurityGroupInfo Group);

/// <summary>A rule that lets the searched address (or group) in or out, and why it matches.</summary>
public sealed record RuleMatch(long TargetId, SecurityGroupInfo Group, SecurityGroupRuleInfo Rule, string Why)
{
    public string Text => $"{Rule.DirectionText} {Rule.ProtocolText} {Rule.PortText} {(Rule.IsEgress ? "to" : "from")} {Rule.SourceText} — {Why}";
}

public sealed class NetworkSearchResult
{
    public required NetworkQuery Query { get; init; }
    /// <summary>Interfaces with a matching address (or using the searched group).</summary>
    public List<InterfaceMatch> Interfaces { get; init; } = [];
    /// <summary>For an address: the groups of the interfaces that have it. For a group id: the group itself.</summary>
    public List<GroupMatch> Groups { get; init; } = [];
    public List<RuleMatch> Rules { get; init; } = [];
    /// <summary>Rules open to 0.0.0.0/0 or ::/0 that also match but are left out (they match every address).</summary>
    public int WorldRules { get; init; }

    public int Count => Interfaces.Count + Groups.Count + Rules.Count;
}

/// <summary>"Which security groups let 10.1.2.3 in?" and "who references sg-…?" over the downloaded network data.</summary>
public static class NetworkSearch
{
    public static NetworkSearchResult Search(IReadOnlyList<NetworkSnapshot> snapshots, NetworkQuery query, bool includeWorld)
    {
        var interfaces = new List<InterfaceMatch>();
        var groups = new List<GroupMatch>();
        var rules = new List<RuleMatch>();
        var world = 0;

        if (query.Kind == NetworkQueryKind.Group)
        {
            var id = query.GroupId!;
            foreach (var s in snapshots)
            {
                groups.AddRange(s.SecurityGroups.Where(g => g.Id == id).Select(g => new GroupMatch(s.TargetId, g)));
                interfaces.AddRange(s.Interfaces.Where(i => i.SecurityGroups.Any(g => g.Id == id)).Select(i => new InterfaceMatch(s.TargetId, i)));
                foreach (var g in s.SecurityGroups)
                    foreach (var r in g.Rules.Where(r => r.ReferencedGroupId == id))
                        rules.Add(new RuleMatch(s.TargetId, g, r, $"allows members of {id}"));
            }
            return new NetworkSearchResult { Query = query, Interfaces = interfaces, Groups = groups, Rules = rules };
        }

        var net = query.Net!.Value;
        // Interfaces that have the address (or an address in the range), and their own groups.
        var ownGroupIds = new HashSet<string>();
        foreach (var s in snapshots)
            foreach (var eni in s.Interfaces)
                if (eni.AllIps.Concat(eni.PrivateIp is null ? [] : [eni.PrivateIp]).Distinct().Any(ip => IpNet.Parse(ip) is { } a && net.Contains(a)))
                {
                    interfaces.Add(new InterfaceMatch(s.TargetId, eni));
                    ownGroupIds.UnionWith(eni.SecurityGroups.Select(g => g.Id));
                }
        foreach (var s in snapshots)
            groups.AddRange(s.SecurityGroups.Where(g => ownGroupIds.Contains(g.Id)).Select(g => new GroupMatch(s.TargetId, g)));

        foreach (var s in snapshots)
            foreach (var g in s.SecurityGroups)
                foreach (var r in g.Rules)
                {
                    if (IpNet.Parse(r.CidrIpv4 ?? r.CidrIpv6) is { } cidr)
                    {
                        if (!cidr.Overlaps(net))
                            continue;
                        if (cidr.IsWorld && !net.IsWorld && !includeWorld)
                        {
                            world++;
                            continue;
                        }
                        var why = cidr.Contains(net) ? (net.IsSingleAddress ? $"{cidr} includes {query.Text}" : $"{cidr} covers all of {net}")
                            : net.Contains(cidr) ? $"{cidr} is part of {net}"
                            : $"{cidr} overlaps {net}";
                        rules.Add(new RuleMatch(s.TargetId, g, r, why));
                    }
                    else if (r.ReferencedGroupId is { } refId && ownGroupIds.Contains(refId))
                    {
                        rules.Add(new RuleMatch(s.TargetId, g, r, $"{query.Text} is in {refId}"));
                    }
                }
        return new NetworkSearchResult { Query = query, Interfaces = interfaces, Groups = groups, Rules = rules, WorldRules = world };
    }
}
