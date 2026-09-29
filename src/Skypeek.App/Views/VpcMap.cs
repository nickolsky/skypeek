using CommunityToolkit.Mvvm.ComponentModel;
using Skypeek.Core.Models;

namespace Skypeek.App.Views;

public enum MapColumn { Subnet, RouteTable, Connection }

/// <summary>One box of the VPC resource map.</summary>
public sealed partial class MapBox : ObservableObject
{
    public required string Id { get; init; }
    public required MapColumn Column { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    /// <summary>public, nat, other, isolated, blackhole (subnets); igw, nat, eigw, tgw, pcx, vpce, vgw, other (connections); table.</summary>
    public string Kind { get; init; } = "";
    /// <summary>Boxes in the next column this one routes to.</summary>
    public HashSet<string> Next { get; } = [];
    /// <summary>Boxes in the previous column that route to this one.</summary>
    public HashSet<string> Previous { get; } = [];
    public string Detail { get; init; } = "";
    public string? Warning { get; init; }

    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _isDimmed;
    public bool HasWarning => Warning is not null;
}

/// <summary>VPC → subnets → route tables → network connections, like the console's resource map.</summary>
public sealed partial class VpcMap : ObservableObject
{
    public required IReadOnlyList<MapBox> Subnets { get; init; }
    public required IReadOnlyList<MapBox> RouteTables { get; init; }
    public required IReadOnlyList<MapBox> Connections { get; init; }

    [ObservableProperty] private MapBox? _selected;

    public IEnumerable<MapBox> All => Subnets.Concat(RouteTables).Concat(Connections);
    public MapBox? Find(string id) => All.FirstOrDefault(b => b.Id == id);

    public string Legend => "Click a box to follow its routes. Public: default route to an internet gateway. NAT: outbound only, through a NAT gateway. Isolated: no default route.";

    /// <summary>Highlights the box and everything it is routed through (or that routes to it); click again to clear.</summary>
    public void Select(MapBox? box)
    {
        Selected = box == Selected ? null : box;
        var lit = new HashSet<string>();
        if (Selected is { } s)
        {
            lit.Add(s.Id);
            Walk(s, forward: true, lit);
            Walk(s, forward: false, lit);
        }
        foreach (var b in All)
        {
            b.IsHighlighted = Selected is not null && lit.Contains(b.Id);
            b.IsDimmed = Selected is not null && !lit.Contains(b.Id);
        }
    }

    private void Walk(MapBox box, bool forward, HashSet<string> lit)
    {
        foreach (var id in forward ? box.Next : box.Previous)
            if (lit.Add(id) && Find(id) is { } next)
                Walk(next, forward, lit);
    }

    public static VpcMap Build(NetworkSnapshot s, VpcInfo vpc)
    {
        var subnets = s.Subnets.Where(x => x.VpcId == vpc.Id).OrderBy(x => x.AvailabilityZone).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
        var tables = s.RouteTables.Where(t => t.VpcId == vpc.Id).ToList();
        var subnetBoxes = new List<MapBox>();
        var tableBoxes = new Dictionary<string, MapBox>();
        var connectionBoxes = new Dictionary<string, MapBox>();

        MapBox Connection(string target)
        {
            if (connectionBoxes.TryGetValue(target, out var existing))
                return existing;
            var nat = s.NatGateways.FirstOrDefault(n => n.Id == target);
            var igw = s.InternetGateways.FirstOrDefault(g => g.Id == target);
            var endpoint = s.Endpoints.FirstOrDefault(e => e.Id == target);
            var peering = s.Peerings.FirstOrDefault(p => p.Id == target);
            var kind = target.Split('-')[0] switch
            {
                "igw" => "igw", "nat" => "nat", "eigw" => "eigw", "tgw" => "tgw", "pcx" => "pcx", "vpce" => "vpce", "vgw" => "vgw", _ => "other",
            };
            var (title, subtitle, detail) = kind switch
            {
                "igw" => (igw?.Name ?? "Internet gateway", target, $"Internet gateway {target}: two-way internet access for resources with a public IP."),
                "eigw" => (igw?.Name ?? "Egress-only gateway", target, $"Egress-only internet gateway {target}: outbound-only IPv6."),
                "nat" when nat is not null => (nat.Name ?? (nat.IsPublic ? "NAT gateway" : "Private NAT gateway"),
                    $"{target} · {nat.PublicIpText}{(nat.State == "available" ? "" : $" · {nat.State}")}",
                    $"{(nat.IsPublic ? "NAT gateway" : "Private NAT gateway")} {nat.Id} ({nat.State}) in {s.Subnets.FirstOrDefault(x => x.Id == nat.SubnetId)?.Title ?? nat.SubnetId}. "
                    + (nat.IsPublic ? $"Outbound internet traffic from private subnets leaves with public IP {nat.PublicIpText}." : "Translates to its private IP for other networks; no internet.")
                    + (nat.PrivateIps.Count > 0 ? $" Private IP {string.Join(", ", nat.PrivateIps)}." : "")),
                "nat" => ("NAT gateway", target, $"NAT gateway {target} (not found; deleted?)."),
                "tgw" => ("Transit gateway", target, $"Transit gateway {target}: traffic continues to other VPCs, VPNs or a shared egress VPC."),
                "pcx" => (peering?.Name ?? "Peering", target, peering is null ? $"Peering {target}." : $"Peering {target} ({peering.Status}) with {peering.PeerText(vpc.Id)}."),
                "vpce" => (endpoint is null ? "VPC endpoint" : $"Endpoint · {endpoint.ShortService}", target, $"Gateway endpoint {target} for {endpoint?.ServiceName}: private access to the service, no internet needed."),
                "vgw" => ("VPN gateway", target, $"Virtual private gateway {target}: site-to-site VPN or Direct Connect."),
                _ => (NetworkRules.TargetLabel(s, target), target, NetworkRules.TargetLabel(s, target)),
            };
            return connectionBoxes[target] = new MapBox { Id = target, Column = MapColumn.Connection, Title = title, Subtitle = subtitle, Kind = kind, Detail = detail };
        }

        foreach (var table in tables.OrderByDescending(t => t.IsMain).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
        {
            var routes = table.Routes.Where(r => r.Target != "local").ToList();
            var box = new MapBox
            {
                Id = table.Id,
                Column = MapColumn.RouteTable,
                Title = table.Name ?? (table.IsMain ? "Main route table" : "Route table"),
                Subtitle = $"{table.Id}{(table.IsMain ? " · main" : "")} · {table.Routes.Count} route(s)",
                Kind = "table",
                Detail = $"{table.Title}{(table.IsMain ? " (main: used by subnets without their own table)" : "")}\n" + string.Join("\n", table.Routes.Select(r => r.Text)),
                Warning = table.Routes.Any(r => r.IsBlackhole) ? "Has blackhole routes (their target was deleted)." : null,
            };
            foreach (var route in routes.Where(r => r.Target is not null && !r.IsBlackhole))
            {
                var connection = Connection(route.Target!);
                box.Next.Add(connection.Id);
                connection.Previous.Add(box.Id);
            }
            tableBoxes[table.Id] = box;
        }
        // Gateways attached to the VPC that no route uses still appear (e.g. an internet gateway of an all-private VPC).
        foreach (var igw in s.InternetGateways.Where(g => g.VpcIds.Contains(vpc.Id)))
            Connection(igw.Id);
        foreach (var nat in s.NatGateways.Where(n => n.VpcId == vpc.Id))
            Connection(nat.Id);

        foreach (var subnet in subnets)
        {
            var routing = NetworkRules.Routing(s, subnet);
            var box = new MapBox
            {
                Id = subnet.Id,
                Column = MapColumn.Subnet,
                Title = subnet.Name ?? subnet.Id,
                Subtitle = $"{subnet.Cidr} · {subnet.AvailabilityZone?.Split('-').LastOrDefault()} · {AccessLabel(routing.Access)}",
                Kind = routing.Access.ToString().ToLowerInvariant(),
                Detail = $"{subnet.Title} · {subnet.Cidr} · {subnet.AvailabilityZone} · {subnet.UsageText}\n{routing.Summary}"
                         + (routing.Ipv6Summary is { } v6 ? $"\n{v6}" : "")
                         + $"\nRoute table: {routing.RouteTable?.Title ?? "none"}{(routing.ExplicitAssociation ? "" : " (the VPC's main table)")}"
                         + (routing.Note is { } note ? $"\n{note}" : ""),
                Warning = routing.Warning,
            };
            if (routing.RouteTable is { } table && tableBoxes.TryGetValue(table.Id, out var tableBox))
            {
                box.Next.Add(tableBox.Id);
                tableBox.Previous.Add(box.Id);
            }
            subnetBoxes.Add(box);
        }

        static int Order(string kind) => kind switch { "igw" => 0, "eigw" => 1, "nat" => 2, "tgw" => 3, "vgw" => 4, "pcx" => 5, "vpce" => 6, _ => 7 };
        return new VpcMap
        {
            Subnets = subnetBoxes,
            // Route tables no subnet uses (and that are not main) are clutter.
            RouteTables = tableBoxes.Values.Where(t => t.Previous.Count > 0 || tables.First(x => x.Id == t.Id).IsMain).ToList(),
            Connections = connectionBoxes.Values.OrderBy(c => Order(c.Kind)).ThenBy(c => c.Title).ToList(),
        };
    }

    public static string AccessLabel(InternetAccess access) => access switch
    {
        InternetAccess.Public => "public",
        InternetAccess.Nat => "private + NAT",
        InternetAccess.Other => "routed elsewhere",
        InternetAccess.Blackhole => "blackhole",
        _ => "isolated",
    };
}
