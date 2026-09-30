namespace Skypeek.Core.Models;

/// <summary>Where a monitored resource sits on the network: its security groups (and, where known, VPC and subnets).</summary>
public static class ResourceNetwork
{
    /// <summary>Security group ids of a resource; EB environments use their instances' groups from <paramref name="network"/>.</summary>
    public static IReadOnlyList<string> SecurityGroupIds(ResourceStatus resource, NetworkSnapshot? network = null) => resource switch
    {
        Ec2InstanceStatus ec2 => ec2.Snapshot.SecurityGroups.Select(g => g.Id).ToList(),
        LoadBalancerStatus lb => lb.Snapshot.SecurityGroups,
        RdsInstanceStatus db => db.Snapshot.SecurityGroups.Select(g => g.Id).ToList(),
        RdsClusterStatus cluster => cluster.Snapshot.SecurityGroups.Select(g => g.Id).ToList(),
        CacheStatus cache => cache.Snapshot.SecurityGroups.Select(g => g.Id).ToList(),
        EcsServiceStatus ecs => ecs.Snapshot.SecurityGroups.Select(g => g.Id).ToList(),
        RedshiftStatus rs => rs.Snapshot.SecurityGroups.Select(g => g.Id).ToList(),
        EbEnvironmentStatus eb when network is not null => network.Interfaces
            .Where(i => i.InstanceId is { } id && eb.Snapshot.InstanceIds.Contains(id))
            .SelectMany(i => i.SecurityGroups.Select(g => g.Id)).Distinct().ToList(),
        _ => [],
    };

    /// <summary>"RDS database", "load balancer", … for "used by" lists.</summary>
    public static string KindName(ResourceStatus resource) => resource switch
    {
        Ec2InstanceStatus => "EC2 instance",
        LoadBalancerStatus => "load balancer",
        RdsInstanceStatus => "RDS database",
        RdsClusterStatus => "RDS cluster",
        CacheStatus => "ElastiCache",
        EcsServiceStatus => "ECS service",
        RedshiftStatus => "Redshift",
        EbEnvironmentStatus => "Elastic Beanstalk environment",
        _ => "resource",
    };
}
