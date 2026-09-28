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
using Secrets = Amazon.SecretsManager.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;
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
    };

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
        "Amazon.SecretsManager.Model" => "secretsmanager",
        "Amazon.SimpleSystemsManagement.Model" => "ssm",
        "Amazon.ElasticBeanstalk.Model" => "elasticbeanstalk",
        "Amazon.ECS.Model" => "ecs",
        "Amazon.CloudWatch.Model" => "cloudwatch",
        "Amazon.CloudWatchLogs.Model" => "logs",
        "Amazon.EC2.Model" => "ec2",
        "Amazon.RDS.Model" => "rds",
        "Amazon.ElastiCache.Model" => "elasticache",
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
