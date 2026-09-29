using System.Diagnostics;
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
using Ce = Amazon.CostExplorer.Model;
using Pricing = Amazon.Pricing.Model;
using Secrets = Amazon.SecretsManager.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;
using Sso = Amazon.SSO.Model;

namespace Skypeek.Aws;

/// <summary>Logs every AWS call (one entry per logical call, including retries) without sensitive data.</summary>
public sealed class RequestLogHandler(IRequestLogSink log) : PipelineHandler
{
    private const int MaxMessageLength = 300;

    public override async Task<T> InvokeAsync<T>(IExecutionContext executionContext)
    {
        var request = executionContext.RequestContext.OriginalRequest;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await base.InvokeAsync<T>(executionContext).ConfigureAwait(false);
            Write(request, RequestOutcome.Success, (int?)executionContext.ResponseContext?.HttpResponse?.StatusCode ?? (int)response.HttpStatusCode,
                stopwatch.ElapsedMilliseconds, response.ResponseMetadata?.RequestId, null, null);
            return response;
        }
        catch (AmazonServiceException ex)
        {
            Write(request, RequestOutcome.Error, (int)ex.StatusCode, stopwatch.ElapsedMilliseconds, ex.RequestId, ex.ErrorCode, ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is not WriteOperationBlockedException)
        {
            Write(request, RequestOutcome.Error, null, stopwatch.ElapsedMilliseconds, null, ex.GetType().Name, ex.Message);
            throw;
        }
    }

    private void Write(AmazonWebServiceRequest request, RequestOutcome outcome, int? status, long ms, string? requestId, string? errorCode, string? message)
    {
        var scope = RequestScope.Current;
        log.Write(new RequestLogEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Profile = scope?.Profile,
            AccountId = scope?.AccountId,
            Elevated = scope?.Elevated ?? false,
            Region = scope?.Region,
            Service = ReadOnlyGuard.ServiceName(request),
            Operation = ReadOnlyGuard.OperationName(request),
            Parameters = RequestParameterRedactor.Describe(request),
            Outcome = outcome,
            HttpStatus = status,
            DurationMs = ms,
            RequestId = requestId,
            ErrorCode = errorCode,
            Message = message is null ? null : message.Length > MaxMessageLength ? message[..MaxMessageLength] + "…" : message,
        });
    }
}

/// <summary>
/// Allowlist-based parameter description: only explicitly chosen, non-sensitive fields are ever logged.
/// Unknown request types log nothing.
/// </summary>
public static class RequestParameterRedactor
{
    public static string Describe(AmazonWebServiceRequest? request) => request switch
    {
        Secrets.ListSecretsRequest r => Join(("MaxResults", r.MaxResults), ("Filters", r.Filters?.Count), ("NextToken", Token(r.NextToken))),
        Secrets.DescribeSecretRequest r => Join(("SecretId", r.SecretId)),
        Secrets.GetSecretValueRequest r => Join(("SecretId", r.SecretId), ("VersionStage", r.VersionStage)),
        Ssm.DescribeParametersRequest r => Join(("MaxResults", r.MaxResults), ("Filters", r.ParameterFilters?.Count), ("NextToken", Token(r.NextToken))),
        Ssm.GetParameterRequest r => Join(("Name", r.Name), ("WithDecryption", r.WithDecryption)),
        Eb.DescribeEnvironmentsRequest r => Join(("IncludeDeleted", r.IncludeDeleted), ("NextToken", Token(r.NextToken))),
        Eb.DescribeEventsRequest r => Join(("StartTime", r.StartTime?.ToString("u")), ("Severity", r.Severity?.Value), ("NextToken", Token(r.NextToken))),
        Eb.DescribeEnvironmentResourcesRequest r => Join(("EnvironmentId", r.EnvironmentId)),
        Eb.DescribeEnvironmentHealthRequest r => Join(("EnvironmentId", r.EnvironmentId)),
        Ecs.ListClustersRequest r => Join(("NextToken", Token(r.NextToken))),
        Ecs.ListServicesRequest r => Join(("Cluster", r.Cluster), ("NextToken", Token(r.NextToken))),
        Ecs.DescribeServicesRequest r => Join(("Cluster", r.Cluster), ("Services", r.Services?.Count)),
        Ecs.ListTasksRequest r => Join(("Cluster", r.Cluster), ("ServiceName", r.ServiceName), ("DesiredStatus", r.DesiredStatus?.Value)),
        Ecs.DescribeTasksRequest r => Join(("Cluster", r.Cluster), ("Tasks", r.Tasks?.Count)),
        CloudWatch.GetMetricDataRequest r => Join(("Queries", r.MetricDataQueries?.Count), ("StartTime", r.StartTime?.ToString("u")), ("EndTime", r.EndTime?.ToString("u")), ("NextToken", Token(r.NextToken))),
        CloudWatch.DescribeAlarmsRequest r => Join(("StateValue", r.StateValue?.Value), ("NextToken", Token(r.NextToken))),
        CloudWatch.DescribeAlarmHistoryRequest r => Join(("HistoryItemType", r.HistoryItemType?.Value), ("StartDate", r.StartDate?.ToString("u")), ("NextToken", Token(r.NextToken))),
        CloudWatch.ListMetricsRequest r => Join(("Namespace", r.Namespace), ("MetricName", r.MetricName), ("NextToken", Token(r.NextToken))),
        Ecs.DescribeTaskDefinitionRequest r => Join(("TaskDefinition", r.TaskDefinition)),
        Eb.DescribeInstancesHealthRequest r => Join(("EnvironmentName", r.EnvironmentName), ("NextToken", Token(r.NextToken))),
        Eb.RequestEnvironmentInfoRequest r => Join(("EnvironmentId", r.EnvironmentId), ("InfoType", r.InfoType?.Value)),
        Eb.DescribeApplicationVersionsRequest r => Join(("ApplicationName", r.ApplicationName), ("VersionLabels", r.VersionLabels?.Count), ("MaxRecords", r.MaxRecords), ("NextToken", Token(r.NextToken))),
        Eb.UpdateEnvironmentRequest r => Join(("EnvironmentId", r.EnvironmentId), ("VersionLabel", r.VersionLabel)),
        Eb.RestartAppServerRequest r => Join(("EnvironmentId", r.EnvironmentId)),
        Ec2.DescribeInstancesRequest r => Join(("Instances", r.InstanceIds?.Count)),
        Ec2.RebootInstancesRequest r => Join(("InstanceIds", r.InstanceIds is null ? null : string.Join(",", r.InstanceIds))),
        Ecs.UpdateServiceRequest r => Join(("Cluster", r.Cluster), ("Service", r.Service), ("ForceNewDeployment", r.ForceNewDeployment)),
        Ec2.TerminateInstancesRequest r => Join(("InstanceIds", r.InstanceIds is null ? null : string.Join(",", r.InstanceIds))),
        Eb.RetrieveEnvironmentInfoRequest r => Join(("EnvironmentId", r.EnvironmentId), ("InfoType", r.InfoType?.Value)),
        Logs.DescribeLogGroupsRequest r => Join(("LogGroupNamePrefix", r.LogGroupNamePrefix), ("NextToken", Token(r.NextToken))),
        Logs.FilterLogEventsRequest r => Join(("LogGroupName", r.LogGroupName), ("StreamPrefix", r.LogStreamNamePrefix),
            ("FilterPattern", string.IsNullOrEmpty(r.FilterPattern) ? null : r.FilterPattern), ("NextToken", Token(r.NextToken))),
        Rds.DescribeDBInstancesRequest r => Join(("DBInstanceIdentifier", r.DBInstanceIdentifier), ("Filters", r.Filters?.Count), ("Marker", Token(r.Marker))),
        Rds.DescribeDBClustersRequest r => Join(("DBClusterIdentifier", r.DBClusterIdentifier), ("Marker", Token(r.Marker))),
        Rds.DescribeEventsRequest r => Join(("SourceIdentifier", r.SourceIdentifier), ("SourceType", r.SourceType?.Value), ("StartTime", r.StartTime?.ToString("u")), ("Marker", Token(r.Marker))),
        Rds.DescribeDBParametersRequest r => Join(("DBParameterGroupName", r.DBParameterGroupName), ("Source", r.Source), ("Marker", Token(r.Marker))),
        Rds.DescribeDBLogFilesRequest r => Join(("DBInstanceIdentifier", r.DBInstanceIdentifier), ("Marker", Token(r.Marker))),
        // Only which file and how much; the returned log text is never logged.
        Rds.DownloadDBLogFilePortionRequest r => Join(("DBInstanceIdentifier", r.DBInstanceIdentifier), ("LogFileName", r.LogFileName),
            ("NumberOfLines", r.NumberOfLines), ("Marker", Token(r.Marker))),
        ElastiCache.DescribeReplicationGroupsRequest r => Join(("ReplicationGroupId", r.ReplicationGroupId), ("Marker", Token(r.Marker))),
        ElastiCache.DescribeCacheClustersRequest r => Join(("CacheClusterId", r.CacheClusterId), ("ShowCacheNodeInfo", r.ShowCacheNodeInfo), ("Marker", Token(r.Marker))),
        ElastiCache.DescribeServerlessCachesRequest r => Join(("ServerlessCacheName", r.ServerlessCacheName), ("NextToken", Token(r.NextToken))),
        ElastiCache.DescribeEventsRequest r => Join(("SourceIdentifier", r.SourceIdentifier), ("StartTime", r.StartTime?.ToString("u")), ("Marker", Token(r.Marker))),
        // Never the access token.
        Sso.GetRoleCredentialsRequest r => Join(("AccountId", r.AccountId), ("RoleName", r.RoleName)),

        Ec2.DescribeInstanceStatusRequest r => Join(("Instances", r.InstanceIds?.Count), ("IncludeAllInstances", r.IncludeAllInstances), ("NextToken", Token(r.NextToken))),
        Ec2.DescribeVpcsRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeSubnetsRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeNetworkInterfacesRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeSecurityGroupsRequest r => Join(("GroupIds", r.GroupIds is { Count: > 0 } g ? string.Join(",", g) : null), ("NextToken", Token(r.NextToken))),
        Ec2.DescribeSecurityGroupRulesRequest r => Join(("Filters", r.Filters?.Count), ("RuleIds", r.SecurityGroupRuleIds is { Count: > 0 } ids ? string.Join(",", ids) : null), ("NextToken", Token(r.NextToken))),
        Ec2.DescribeAddressesRequest => "",
        Pricing.GetProductsRequest r => Join(("ServiceCode", r.ServiceCode), ("Filters", r.Filters is null ? null : string.Join(",", r.Filters.Select(f => $"{f.Field}={f.Value}"))), ("NextToken", Token(r.NextToken))),
        Ce.GetCostAndUsageRequest r => Join(("Start", r.TimePeriod?.Start), ("End", r.TimePeriod?.End), ("Granularity", r.Granularity?.Value), ("NextPageToken", Token(r.NextPageToken))),
        Ce.GetCostAndUsageWithResourcesRequest r => Join(("Start", r.TimePeriod?.Start), ("End", r.TimePeriod?.End), ("Granularity", r.Granularity?.Value), ("NextPageToken", Token(r.NextPageToken))),
        Ec2.DescribeRouteTablesRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeInternetGatewaysRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeEgressOnlyInternetGatewaysRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeNatGatewaysRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeVpcEndpointsRequest r => Join(("NextToken", Token(r.NextToken))),
        Ec2.DescribeVpcPeeringConnectionsRequest r => Join(("NextToken", Token(r.NextToken))),
        Elb.DescribeLoadBalancersRequest r => Join(("LoadBalancerArns", r.LoadBalancerArns?.Count), ("Marker", Token(r.Marker))),
        Elb.DescribeTargetGroupsRequest r => Join(("LoadBalancerArn", r.LoadBalancerArn), ("Marker", Token(r.Marker))),
        Elb.DescribeTargetHealthRequest r => Join(("TargetGroupArn", r.TargetGroupArn)),
        Elb.DescribeListenersRequest r => Join(("LoadBalancerArn", r.LoadBalancerArn), ("Marker", Token(r.Marker))),
        Elb.DescribeRulesRequest r => Join(("ListenerArn", r.ListenerArn), ("Marker", Token(r.Marker))),

        // Actions: which instance / group / rule and what the rule allows; rule descriptions are not logged.
        Ec2.StartInstancesRequest r => Join(("InstanceIds", r.InstanceIds is null ? null : string.Join(",", r.InstanceIds))),
        Ec2.StopInstancesRequest r => Join(("InstanceIds", r.InstanceIds is null ? null : string.Join(",", r.InstanceIds)), ("Force", r.Force), ("Hibernate", r.Hibernate)),
        Ec2.AuthorizeSecurityGroupIngressRequest r => Join(("GroupId", r.GroupId), ("Rule", Permission(r.IpPermissions))),
        Ec2.AuthorizeSecurityGroupEgressRequest r => Join(("GroupId", r.GroupId), ("Rule", Permission(r.IpPermissions))),
        Ec2.RevokeSecurityGroupIngressRequest r => Join(("GroupId", r.GroupId), ("RuleIds", r.SecurityGroupRuleIds is null ? null : string.Join(",", r.SecurityGroupRuleIds))),
        Ec2.RevokeSecurityGroupEgressRequest r => Join(("GroupId", r.GroupId), ("RuleIds", r.SecurityGroupRuleIds is null ? null : string.Join(",", r.SecurityGroupRuleIds))),
        Ec2.ModifySecurityGroupRulesRequest r => Join(("GroupId", r.GroupId), ("Rules", r.SecurityGroupRules is null ? null : string.Join("; ", r.SecurityGroupRules.Select(u =>
            $"{u.SecurityGroupRuleId}: {u.SecurityGroupRule?.IpProtocol} {u.SecurityGroupRule?.FromPort}-{u.SecurityGroupRule?.ToPort} " +
            $"{u.SecurityGroupRule?.CidrIpv4 ?? u.SecurityGroupRule?.CidrIpv6 ?? u.SecurityGroupRule?.PrefixListId ?? u.SecurityGroupRule?.ReferencedGroupId}")))),
        _ => "",
    };

    private static string? Permission(List<Ec2.IpPermission>? permissions) => permissions is null ? null : string.Join("; ", permissions.Select(p =>
        $"{p.IpProtocol} {p.FromPort}-{p.ToPort} " + string.Join(",", (p.Ipv4Ranges ?? []).Select(x => x.CidrIp)
            .Concat((p.Ipv6Ranges ?? []).Select(x => x.CidrIpv6))
            .Concat((p.PrefixListIds ?? []).Select(x => x.Id))
            .Concat((p.UserIdGroupPairs ?? []).Select(x => x.GroupId)))));

    private static string? Token(string? nextToken) => string.IsNullOrEmpty(nextToken) ? null : "yes";

    private static string Join(params (string Key, object? Value)[] pairs) =>
        string.Join(", ", pairs.Where(p => p.Value is not null).Select(p => $"{p.Key}={p.Value}"));
}
