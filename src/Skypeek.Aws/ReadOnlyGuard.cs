using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Skypeek.Core.Logging;
using CloudWatch = Amazon.CloudWatch.Model;
using Logs = Amazon.CloudWatchLogs.Model;
using Ec2 = Amazon.EC2.Model;
using Ecs = Amazon.ECS.Model;
using ElastiCache = Amazon.ElastiCache.Model;
using Rds = Amazon.RDS.Model;
using Eb = Amazon.ElasticBeanstalk.Model;
using Elb = Amazon.ElasticLoadBalancingV2.Model;
using Secrets = Amazon.SecretsManager.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;
using Sso = Amazon.SSO.Model;
using Sts = Amazon.SecurityToken.Model;

namespace Skypeek.Aws;

public sealed class WriteOperationBlockedException(string operation)
    : InvalidOperationException($"Blocked non-read-only AWS operation '{operation}'. Skypeek only performs allowlisted read operations.")
{
    public string Operation { get; } = operation;
}

/// <summary>
/// Exact allowlist of request types the app may send. Anything else is rejected before signing or sending.
/// </summary>
public static class ReadOnlyGuard
{
    public static readonly IReadOnlySet<Type> AllowedRequestTypes = new HashSet<Type>
    {
        typeof(Sts.GetCallerIdentityRequest),
        typeof(Sso.GetRoleCredentialsRequest), // SSO profiles: exchange the sign-in token for role credentials

        typeof(Secrets.ListSecretsRequest),
        typeof(Secrets.DescribeSecretRequest),
        typeof(Secrets.GetSecretValueRequest),

        typeof(Ssm.DescribeParametersRequest),
        typeof(Ssm.GetParameterRequest),

        typeof(Eb.DescribeEnvironmentsRequest),
        typeof(Eb.DescribeEventsRequest),
        typeof(Eb.DescribeEnvironmentHealthRequest),
        typeof(Eb.DescribeEnvironmentResourcesRequest),
        typeof(Eb.DescribeInstancesHealthRequest),
        typeof(Eb.RetrieveEnvironmentInfoRequest),
        typeof(Eb.DescribeApplicationVersionsRequest),

        typeof(Ec2.DescribeInstancesRequest),
        typeof(Ec2.DescribeInstanceStatusRequest),
        typeof(Ec2.DescribeVpcsRequest),
        typeof(Ec2.DescribeSubnetsRequest),
        typeof(Ec2.DescribeNetworkInterfacesRequest),
        typeof(Ec2.DescribeSecurityGroupsRequest),
        typeof(Ec2.DescribeSecurityGroupRulesRequest),
        typeof(Ec2.DescribeAddressesRequest),

        typeof(Elb.DescribeLoadBalancersRequest),
        typeof(Elb.DescribeTargetGroupsRequest),
        typeof(Elb.DescribeTargetHealthRequest),
        typeof(Elb.DescribeListenersRequest),
        typeof(Elb.DescribeRulesRequest),

        typeof(Ecs.ListClustersRequest),
        typeof(Ecs.ListServicesRequest),
        typeof(Ecs.DescribeServicesRequest),
        typeof(Ecs.ListTasksRequest),
        typeof(Ecs.DescribeTasksRequest),
        typeof(Ecs.DescribeTaskDefinitionRequest),

        typeof(CloudWatch.GetMetricDataRequest),
        typeof(CloudWatch.DescribeAlarmsRequest),
        typeof(CloudWatch.DescribeAlarmHistoryRequest),
        typeof(CloudWatch.ListMetricsRequest),

        typeof(Logs.DescribeLogGroupsRequest),
        typeof(Logs.FilterLogEventsRequest),

        typeof(Rds.DescribeDBInstancesRequest),
        typeof(Rds.DescribeDBClustersRequest),
        typeof(Rds.DescribeEventsRequest),
        typeof(Rds.DescribeDBParametersRequest),
        typeof(Rds.DescribeDBLogFilesRequest),
        typeof(Rds.DownloadDBLogFilePortionRequest), // reads a log file; nothing is changed

        typeof(ElastiCache.DescribeReplicationGroupsRequest),
        typeof(ElastiCache.DescribeCacheClustersRequest),
        typeof(ElastiCache.DescribeServerlessCachesRequest),
        typeof(ElastiCache.DescribeEventsRequest),
    };

    /// <summary>
    /// The only non-read actions. Each is allowed only for a call that uses the elevated profile and that the user
    /// approved in the permission dialog, and only with the narrow parameters checked in <see cref="ConfirmedRequestProblem"/>.
    /// </summary>
    public static readonly IReadOnlySet<Type> ConfirmedOnlyRequestTypes = new HashSet<Type>
    {
        typeof(Eb.RequestEnvironmentInfoRequest), // copy instance logs to EB's S3 bucket
        typeof(Eb.UpdateEnvironmentRequest),      // deploy an existing application version (nothing else)
        typeof(Eb.RestartAppServerRequest),       // restart the app server on the environment's instances
        typeof(Ec2.RebootInstancesRequest),       // reboot one instance
        typeof(Ec2.TerminateInstancesRequest),    // terminate one instance (its Auto Scaling group replaces it)
        typeof(Ecs.UpdateServiceRequest),         // force a new deployment of one service (nothing else)
        typeof(Ec2.StartInstancesRequest),        // start one stopped instance
        typeof(Ec2.StopInstancesRequest),         // stop one instance (no force, no hibernation)
        typeof(Ec2.AuthorizeSecurityGroupIngressRequest), // add one inbound rule with one source
        typeof(Ec2.AuthorizeSecurityGroupEgressRequest),  // add one outbound rule with one source
        typeof(Ec2.RevokeSecurityGroupIngressRequest),    // delete one inbound rule by id
        typeof(Ec2.RevokeSecurityGroupEgressRequest),     // delete one outbound rule by id
        typeof(Ec2.ModifySecurityGroupRulesRequest),      // change one rule in place
    };

    /// <summary>The only UpdateService fields the app sets; anything else would change the service's configuration.</summary>
    private static readonly HashSet<string> ForceDeploymentFields = [nameof(Ecs.UpdateServiceRequest.Cluster), nameof(Ecs.UpdateServiceRequest.Service), nameof(Ecs.UpdateServiceRequest.ForceNewDeployment)];

    /// <summary>
    /// Checks every public field by reflection, so fields added by future SDK versions (desired count, task definition,
    /// networking, …) are refused too.
    /// </summary>
    private static bool OnlyForcesNewDeployment(Ecs.UpdateServiceRequest r) =>
        r.ForceNewDeployment == true && !string.IsNullOrEmpty(r.Cluster) && !string.IsNullOrEmpty(r.Service) &&
        OnlySetFields(r, ForceDeploymentFields);

    /// <summary>
    /// True when every public property of <paramref name="value"/> outside <paramref name="allowed"/> is unset (null
    /// or an empty collection). Reflection covers properties that later SDK versions add.
    /// </summary>
    public static bool OnlySetFields(object value, IReadOnlySet<string> allowed) =>
        value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(p => !allowed.Contains(p.Name))
            .All(p => p.GetValue(value) switch
            {
                null => true,
                System.Collections.ICollection { Count: 0 } => true,
                _ => false,
            });

    private static readonly HashSet<string> InstanceActionFields = ["InstanceIds"];
    private static readonly HashSet<string> RuleGroupFields = ["GroupId", "IpPermissions"];
    private static readonly HashSet<string> RevokeFields = ["GroupId", "SecurityGroupRuleIds"];
    private static readonly HashSet<string> ModifyFields = ["GroupId", "SecurityGroupRules"];
    private static readonly HashSet<string> PermissionFields = ["IpProtocol", "FromPort", "ToPort", "Ipv4Ranges", "Ipv6Ranges", "PrefixListIds", "UserIdGroupPairs"];
    private static readonly HashSet<string> RuleUpdateFields = ["SecurityGroupRuleId", "SecurityGroupRule"];
    private static readonly HashSet<string> RuleRequestFields = ["IpProtocol", "FromPort", "ToPort", "CidrIpv4", "CidrIpv6", "PrefixListId", "ReferencedGroupId", "Description"];

    /// <summary>One IpPermission with exactly one source (one CIDR, one prefix list or one security group).</summary>
    private static bool IsSingleRule(List<Ec2.IpPermission>? permissions)
    {
        if (permissions is not { Count: 1 } || permissions[0] is not { } p || string.IsNullOrEmpty(p.IpProtocol) || !OnlySetFields(p, PermissionFields))
            return false;
        var sources = (p.Ipv4Ranges?.Count ?? 0) + (p.Ipv6Ranges?.Count ?? 0) + (p.PrefixListIds?.Count ?? 0) + (p.UserIdGroupPairs?.Count ?? 0);
        if (sources != 1)
            return false;
        // A referenced group may only name the group (and its account for peering); no peering or VPC changes.
        return p.UserIdGroupPairs is not { Count: 1 } pairs
               || (!string.IsNullOrEmpty(pairs[0].GroupId) && OnlySetFields(pairs[0], new HashSet<string> { "GroupId", "UserId", "Description" }));
    }

    private static bool IsSingleRuleUpdate(Ec2.ModifySecurityGroupRulesRequest r) =>
        r.SecurityGroupRules is { Count: 1 } rules && rules[0] is { } update && !string.IsNullOrEmpty(update.SecurityGroupRuleId)
        && update.SecurityGroupRule is { } rule && !string.IsNullOrEmpty(rule.IpProtocol)
        && OnlySetFields(update, RuleUpdateFields) && OnlySetFields(rule, RuleRequestFields)
        && new[] { rule.CidrIpv4, rule.CidrIpv6, rule.PrefixListId, rule.ReferencedGroupId }.Count(s => !string.IsNullOrEmpty(s)) == 1;

    public static bool IsAllowed(AmazonWebServiceRequest? request, bool approved = false, bool elevated = false) =>
        request is not null && (AllowedRequestTypes.Contains(request.GetType())
                                || (approved && elevated && ConfirmedOnlyRequestTypes.Contains(request.GetType()) && ConfirmedRequestProblem(request) is null));

    /// <summary>Why a confirmed-only request is shaped wider than the app ever sends, or null when it is fine.</summary>
    public static string? ConfirmedRequestProblem(AmazonWebServiceRequest request) => request switch
    {
        Eb.UpdateEnvironmentRequest r when string.IsNullOrEmpty(r.VersionLabel) => "UpdateEnvironment without a version label",
        Eb.UpdateEnvironmentRequest r when r.OptionSettings is { Count: > 0 } || r.OptionsToRemove is { Count: > 0 } || r.TemplateName is not null
                                           || r.SolutionStackName is not null || r.PlatformArn is not null || r.Tier is not null || r.Description is not null
            => "UpdateEnvironment may only change the application version",
        Ec2.RebootInstancesRequest r when r.InstanceIds is not { Count: 1 } => "RebootInstances must target exactly one instance",
        Ec2.TerminateInstancesRequest r when r.InstanceIds is not { Count: 1 } => "TerminateInstances must target exactly one instance",
        Ecs.UpdateServiceRequest r when !OnlyForcesNewDeployment(r) => "UpdateService may only force a new deployment of one service",
        Ec2.StartInstancesRequest r when r.InstanceIds is not { Count: 1 } || !OnlySetFields(r, InstanceActionFields)
            => "StartInstances must target exactly one instance",
        Ec2.StopInstancesRequest r when r.InstanceIds is not { Count: 1 } || !OnlySetFields(r, InstanceActionFields)
            => "StopInstances must target exactly one instance, without force or hibernation",
        Ec2.AuthorizeSecurityGroupIngressRequest r when string.IsNullOrEmpty(r.GroupId) || !OnlySetFields(r, RuleGroupFields) || !IsSingleRule(r.IpPermissions)
            => "AuthorizeSecurityGroupIngress must add exactly one rule with one source to one group (by id)",
        Ec2.AuthorizeSecurityGroupEgressRequest r when string.IsNullOrEmpty(r.GroupId) || !OnlySetFields(r, RuleGroupFields) || !IsSingleRule(r.IpPermissions)
            => "AuthorizeSecurityGroupEgress must add exactly one rule with one source to one group",
        Ec2.RevokeSecurityGroupIngressRequest r when string.IsNullOrEmpty(r.GroupId) || r.SecurityGroupRuleIds is not { Count: 1 } || !OnlySetFields(r, RevokeFields)
            => "RevokeSecurityGroupIngress must delete exactly one rule by its id",
        Ec2.RevokeSecurityGroupEgressRequest r when string.IsNullOrEmpty(r.GroupId) || r.SecurityGroupRuleIds is not { Count: 1 } || !OnlySetFields(r, RevokeFields)
            => "RevokeSecurityGroupEgress must delete exactly one rule by its id",
        Ec2.ModifySecurityGroupRulesRequest r when string.IsNullOrEmpty(r.GroupId) || !OnlySetFields(r, ModifyFields) || !IsSingleRuleUpdate(r)
            => "ModifySecurityGroupRules must change exactly one rule with one source",
        _ => null,
    };

    public static string OperationName(AmazonWebServiceRequest? request)
    {
        var name = request?.GetType().Name ?? "Unknown";
        return name.EndsWith("Request", StringComparison.Ordinal) ? name[..^"Request".Length] : name;
    }

    public static string ServiceName(AmazonWebServiceRequest? request) => request?.GetType().Namespace switch
    {
        "Amazon.SecurityToken.Model" => "sts",
        "Amazon.SSO.Model" => "sso",
        "Amazon.SecretsManager.Model" => "secretsmanager",
        "Amazon.SimpleSystemsManagement.Model" => "ssm",
        "Amazon.ElasticBeanstalk.Model" => "elasticbeanstalk",
        "Amazon.ECS.Model" => "ecs",
        "Amazon.CloudWatch.Model" => "cloudwatch",
        "Amazon.CloudWatchLogs.Model" => "logs",
        "Amazon.EC2.Model" => "ec2",
        "Amazon.RDS.Model" => "rds",
        "Amazon.ElastiCache.Model" => "elasticache",
        "Amazon.ElasticLoadBalancingV2.Model" => "elasticloadbalancing",
        { } ns => ns,
        null => "unknown",
    };
}

/// <summary>Outermost pipeline handler: rejects any request type that is not on the allowlist.</summary>
public sealed class ReadOnlyGuardHandler(IRequestLogSink log) : PipelineHandler
{
    public override void InvokeSync(IExecutionContext executionContext)
    {
        Check(executionContext);
        base.InvokeSync(executionContext);
    }

    public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        Check(executionContext);
        return base.InvokeAsync<T>(executionContext);
    }

    private void Check(IExecutionContext executionContext)
    {
        var request = executionContext.RequestContext.OriginalRequest;
        var scope = RequestScope.Current;
        if (ReadOnlyGuard.IsAllowed(request, scope?.Approved == true, scope?.Elevated == true))
            return;

        var operation = ReadOnlyGuard.OperationName(request);
        log.Write(new RequestLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Profile = scope?.Profile,
            AccountId = scope?.AccountId,
            Elevated = scope?.Elevated ?? false,
            Region = scope?.Region,
            Service = ReadOnlyGuard.ServiceName(request),
            Operation = operation,
            Outcome = RequestOutcome.Blocked,
            Message = "blocked: operation is not on the read-only allowlist",
        });
        throw new WriteOperationBlockedException(operation);
    }
}
