using System.Net;
using System.Net.Sockets;
using Skypeek.Core.Models;

namespace Skypeek.Desktop.Views;

/// <summary>Something the reach check can start or end at, resolved to a network interface (or an address) when used.</summary>
/// <param name="Port">The resource's usual port (a database's), suggested when it is picked as the destination.</param>
public sealed record EndpointOption(string Title, string Detail, Func<Task<ReachEndpoint?>> Resolve, string Search, int? Port = null, string? Key = null)
{
    public override string ToString() => Title;
}

/// <summary>
/// Everything that can be picked in "Analyze reach": network interfaces from the Network tab data, dashboard resources
/// (mapped to their interfaces; databases, caches and Redshift through their endpoint's DNS name), typed IP addresses
/// and host names.
/// </summary>
public static class ReachEndpoints
{
    public static List<EndpointOption> All(AppSession session)
    {
        var snapshots = session.Network.Snapshot();
        var names = session.Settings.Targets.ToDictionary(t => t.Id, t => t.DisplayName);
        var options = new List<EndpointOption>();

        (long TargetId, NetworkInterfaceInfo Eni)? ByIp(string? ip) =>
            ip is null ? null : snapshots.SelectMany(s => s.Interfaces.Select(i => (s.TargetId, i))).FirstOrDefault(x => x.i.AllIps.Contains(ip) || x.i.PrivateIp == ip) is var hit && hit.i is not null ? hit : null;
        (long TargetId, NetworkInterfaceInfo Eni)? ById(string id) =>
            snapshots.SelectMany(s => s.Interfaces.Select(i => (s.TargetId, i))).FirstOrDefault(x => x.i.Id == id) is var hit && hit.i is not null ? hit : null;
        Func<Task<ReachEndpoint?>> Fixed((long TargetId, NetworkInterfaceInfo Eni)? eni, string label) =>
            () => Task.FromResult(eni is { } e ? ReachEndpoint.Of(e.TargetId, e.Eni, label) : null);

        foreach (var s in snapshots)
            foreach (var eni in s.Interfaces.Where(i => i.PrivateIp is not null))
                options.Add(new EndpointOption($"{eni.PrivateIp} · {eni.Owner}", $"{eni.Id} · {eni.SubnetId} · {names.GetValueOrDefault(s.TargetId)}",
                    Fixed((s.TargetId, eni), $"{eni.Owner} {eni.PrivateIp}"),
                    $"{string.Join(' ', eni.AllIps)} {eni.Owner} {eni.Id} {eni.Name} {eni.Description} {eni.InstanceId}", Key: $"eni:{eni.Id}"));

        foreach (var health in session.Health.Snapshot())
        {
            var target = names.GetValueOrDefault(health.TargetId) ?? "";
            foreach (var ec2 in health.Ec2)
                if (ec2.Snapshot.Interfaces.FirstOrDefault(i => i.DeviceIndex is 0 or null) is { } primary && ById(primary.Id) is { } eni)
                    options.Add(new EndpointOption($"{ec2.Snapshot.Title} · EC2", $"{primary.PrivateIp} · {target}", Fixed(eni, ec2.Snapshot.Title),
                        $"{ec2.Snapshot.Title} {ec2.Snapshot.InstanceId} {ec2.Snapshot.PrivateIp} {ec2.Snapshot.PublicIp}", Key: ec2.ResourceKey));
            foreach (var db in health.Rds.Where(d => d.Snapshot.Address is not null))
                options.Add(new EndpointOption($"{db.Snapshot.Identifier} · RDS", $"{db.Snapshot.Address} · {target}", ViaDns(db.Snapshot.Address!, db.Snapshot.Identifier),
                    $"{db.Snapshot.Identifier} {db.Snapshot.Address} rds database", db.Snapshot.Port, db.ResourceKey));
            foreach (var cluster in health.RdsClusters.Where(c => c.Snapshot.WriterEndpoint is not null))
                options.Add(new EndpointOption($"{cluster.Snapshot.Identifier} · RDS cluster (writer)", $"{cluster.Snapshot.WriterEndpoint} · {target}",
                    ViaDns(cluster.Snapshot.WriterEndpoint!, cluster.Snapshot.Identifier), $"{cluster.Snapshot.Identifier} {cluster.Snapshot.WriterEndpoint} aurora", cluster.Snapshot.Port, cluster.ResourceKey));
            foreach (var cache in health.Caches)
                if (Host(cache.Snapshot.MainEndpoint) is { } host)
                    options.Add(new EndpointOption($"{cache.Snapshot.Id} · ElastiCache", $"{host} · {target}", ViaDns(host, cache.Snapshot.Id),
                        $"{cache.Snapshot.Id} {host} redis valkey cache", cache.Snapshot.Engine == "memcached" ? 11211 : 6379, cache.ResourceKey));
            foreach (var lb in health.LoadBalancers)
                if (snapshots.SelectMany(s => s.Interfaces.Select(i => (s.TargetId, i))).FirstOrDefault(x => x.i.Description == $"ELB {lb.Snapshot.ArnSuffix}") is var hit && hit.i is not null)
                    options.Add(new EndpointOption($"{lb.Snapshot.Name} · load balancer", $"{hit.i.PrivateIp} (one of its interfaces) · {target}", Fixed(hit, lb.Snapshot.Name),
                        $"{lb.Snapshot.Name} {lb.Snapshot.DnsName}", lb.Snapshot.Type == "application" ? 443 : null, lb.ResourceKey));
            foreach (var r in health.Redshift.Where(r => r.Snapshot.Endpoint is not null))
                options.Add(new EndpointOption($"{r.Snapshot.Id} · Redshift", $"{r.Snapshot.Endpoint} · {target}", ViaDns(r.Snapshot.Endpoint!, r.Snapshot.Id),
                    $"{r.Snapshot.Id} {r.Snapshot.Endpoint} redshift", r.Snapshot.Port, r.ResourceKey));
            foreach (var svc in health.Ecs)
                if (svc.Snapshot.Tasks.Select(t => ByIp(t.PrivateIp)).FirstOrDefault(x => x is not null) is { } task)
                    options.Add(new EndpointOption($"{svc.DisplayName} · ECS service", $"{task.Eni.PrivateIp} (one task) · {target}", Fixed(task, svc.DisplayName),
                        $"{svc.DisplayName} ecs", Key: svc.ResourceKey));
            foreach (var eb in health.Eb)
                if (eb.Snapshot.InstanceIds.Select(id => snapshots.SelectMany(s => s.Interfaces.Select(i => (s.TargetId, i))).FirstOrDefault(x => x.i.InstanceId == id))
                        .FirstOrDefault(x => x.i is not null) is var node && node.i is not null)
                    options.Add(new EndpointOption($"{eb.DisplayName} · Elastic Beanstalk", $"{node.i.PrivateIp} (one instance) · {target}", Fixed(node, eb.DisplayName),
                        $"{eb.DisplayName} beanstalk", Key: eb.ResourceKey));
        }
        return options;

        Func<Task<ReachEndpoint?>> ViaDns(string host, string label) => async () =>
        {
            var address = await ResolveAsync(host);
            if (address is null)
                return null;
            return ByIp(address.ToString()) is { } eni ? ReachEndpoint.Of(eni.TargetId, eni.Eni, label) : new ReachEndpoint(label, address);
        };
    }

    /// <summary>Options for typed text that is an address or a host name.</summary>
    public static IEnumerable<EndpointOption> ForText(AppSession session, string text)
    {
        var t = text.Trim();
        if (IPAddress.TryParse(t, out var ip) && t.Count(c => c == '.') == 3 || t.Contains(':') && IPAddress.TryParse(t, out ip))
        {
            var snapshots = session.Network.Snapshot();
            var hit = snapshots.SelectMany(s => s.Interfaces.Select(i => (s.TargetId, i))).FirstOrDefault(x => x.i.AllIps.Contains(t) || x.i.PrivateIp == t);
            yield return hit.i is not null
                ? new EndpointOption($"{t} · {hit.i.Owner}", hit.i.Id, () => Task.FromResult<ReachEndpoint?>(ReachEndpoint.Of(hit.TargetId, hit.i)), t)
                : new EndpointOption($"{t}", "an address outside the downloaded VPCs (on-premises, internet)", () => Task.FromResult<ReachEndpoint?>(new ReachEndpoint(t, ip!)), t);
        }
        else if (t.Contains('.') && t.Any(char.IsLetter) && !t.Contains(' '))
            yield return new EndpointOption($"{t}", "resolve this name (DNS) and use its address", async () =>
            {
                var address = await ResolveAsync(t);
                if (address is null)
                    return null;
                var snapshots = session.Network.Snapshot();
                var hit = snapshots.SelectMany(s => s.Interfaces.Select(i => (s.TargetId, i))).FirstOrDefault(x => x.i.AllIps.Contains(address.ToString()));
                return hit.i is not null ? ReachEndpoint.Of(hit.TargetId, hit.i, t) : new ReachEndpoint(t, address);
            }, t);
    }

    private static string? Host(string? endpoint) => endpoint?.Split(':')[0] is { Length: > 0 } h ? h : null;

    private static async Task<IPAddress?> ResolveAsync(string host)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
