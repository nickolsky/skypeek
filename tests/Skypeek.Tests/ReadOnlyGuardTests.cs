using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SimpleSystemsManagement;
using Skypeek.Aws;
using Skypeek.Core.Logging;
using NetArchTest.Rules;
using Secrets = Amazon.SecretsManager.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;

namespace Skypeek.Tests;

internal static class SharedPipeline
{
    public static readonly CapturingSink Sink = new();

    static SharedPipeline() => AwsPipeline.Install(Sink);

    public static void EnsureInstalled() { }
}

/// <summary>Minimal local HTTP endpoint that answers every request with a canned AWS JSON response.</summary>
internal sealed class FakeAwsEndpoint : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _body;
    private int _requests;

    public FakeAwsEndpoint(string body)
    {
        _body = body;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Loop();
    }

    public int Port { get; }
    public string Url => $"http://localhost:{Port}";
    public int Requests => _requests;

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }
            Interlocked.Increment(ref _requests);
            var bytes = Encoding.UTF8.GetBytes(_body);
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/x-amz-json-1.1";
            ctx.Response.AddHeader("x-amzn-RequestId", "req-123");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}

public class ReadOnlyGuardTests
{
    private static readonly SessionAWSCredentials Creds = new("ASIATESTKEY", "SECRETACCESSKEY123", "SESSIONTOKEN123");

    private static T Config<T>(T config, string url) where T : ClientConfig
    {
        config.ServiceURL = url;
        config.AuthenticationRegion = "us-east-1";
        config.MaxErrorRetry = 0;
        return config;
    }

    [Fact]
    public void Allowlist_contains_only_read_verbs()
    {
        string[] readVerbs = ["List", "Describe", "Get", "Filter", "Retrieve"];
        foreach (var type in ReadOnlyGuard.AllowedRequestTypes)
        {
            var op = type.Name[..^"Request".Length];
            // Reading an RDS log file is the one read whose name does not start with a read verb.
            if (type == typeof(Amazon.RDS.Model.DownloadDBLogFilePortionRequest))
                continue;
            Assert.True(readVerbs.Any(op.StartsWith), $"{type.FullName} is not a read operation");
        }
    }

    [Fact]
    public void Confirmed_only_actions_are_exactly_the_approved_set()
    {
        var names = ReadOnlyGuard.ConfirmedOnlyRequestTypes.Select(t => t.Name).OrderBy(n => n).ToList();
        Assert.Equal(["RebootInstancesRequest", "RequestEnvironmentInfoRequest", "RestartAppServerRequest", "TerminateInstancesRequest", "UpdateEnvironmentRequest", "UpdateServiceRequest"], names);
    }

    [Fact]
    public void Write_actions_are_limited_to_their_narrow_shape()
    {
        Assert.Null(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ElasticBeanstalk.Model.UpdateEnvironmentRequest { EnvironmentId = "e-1", VersionLabel = "v2" }));
        Assert.NotNull(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ElasticBeanstalk.Model.UpdateEnvironmentRequest
        {
            EnvironmentId = "e-1",
            VersionLabel = "v2",
            OptionSettings = [new Amazon.ElasticBeanstalk.Model.ConfigurationOptionSetting { Namespace = "aws:autoscaling:asg", OptionName = "MaxSize", Value = "0" }],
        }));
        Assert.NotNull(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ElasticBeanstalk.Model.UpdateEnvironmentRequest { EnvironmentId = "e-1", SolutionStackName = "x" }));
        Assert.NotNull(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.EC2.Model.TerminateInstancesRequest { InstanceIds = ["i-1", "i-2"] }));
        Assert.Null(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.EC2.Model.TerminateInstancesRequest { InstanceIds = ["i-1"] }));

        // ECS: only "force new deployment"; any other change to the service is refused.
        Assert.Null(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ECS.Model.UpdateServiceRequest { Cluster = "c", Service = "s", ForceNewDeployment = true }));
        Assert.NotNull(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ECS.Model.UpdateServiceRequest { Cluster = "c", Service = "s" }));
        Assert.NotNull(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ECS.Model.UpdateServiceRequest { Cluster = "c", Service = "s", ForceNewDeployment = true, DesiredCount = 0 }));
        Assert.NotNull(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ECS.Model.UpdateServiceRequest { Cluster = "c", Service = "s", ForceNewDeployment = true, TaskDefinition = "evil:1" }));
        Assert.NotNull(ReadOnlyGuard.ConfirmedRequestProblem(new Amazon.ECS.Model.UpdateServiceRequest
        {
            Cluster = "c", Service = "s", ForceNewDeployment = true,
            NetworkConfiguration = new Amazon.ECS.Model.NetworkConfiguration(),
        }));

        // Approval without the elevated key is not enough.
        var reboot = new Amazon.EC2.Model.RebootInstancesRequest { InstanceIds = ["i-1"] };
        Assert.False(ReadOnlyGuard.IsAllowed(reboot, approved: true, elevated: false));
        Assert.False(ReadOnlyGuard.IsAllowed(reboot, approved: false, elevated: true));
        Assert.True(ReadOnlyGuard.IsAllowed(reboot, approved: true, elevated: true));
    }

    [Fact]
    public async Task Eb_log_request_needs_an_approved_elevated_scope()
    {
        SharedPipeline.EnsureInstalled();
        using var endpoint = new FakeAwsEndpoint("{}");
        using var eb = new Amazon.ElasticBeanstalk.AmazonElasticBeanstalkClient(Creds, Config(new Amazon.ElasticBeanstalk.AmazonElasticBeanstalkConfig(), endpoint.Url));
        var request = new Amazon.ElasticBeanstalk.Model.RequestEnvironmentInfoRequest { EnvironmentId = "e-1", InfoType = Amazon.ElasticBeanstalk.EnvironmentInfoType.Tail };

        await Assert.ThrowsAsync<WriteOperationBlockedException>(() => eb.RequestEnvironmentInfoAsync(request));
        using (RequestScope.Begin(new RequestScopeInfo("p", null, "us-east-1", Approved: true)))
            await Assert.ThrowsAsync<WriteOperationBlockedException>(() => eb.RequestEnvironmentInfoAsync(request));
        Assert.Equal(0, endpoint.Requests);

        using (RequestScope.Begin(new RequestScopeInfo("p", null, "us-east-1", Elevated: true, Approved: true)))
        {
            // The fake endpoint's reply does not parse as an EB response; reaching it is what matters.
            var ex = await Record.ExceptionAsync(() => eb.RequestEnvironmentInfoAsync(request));
            Assert.IsNotType<WriteOperationBlockedException>(ex);
        }
        Assert.Equal(1, endpoint.Requests);
    }

    [Fact]
    public async Task Ecs_force_deployment_needs_an_approved_elevated_scope_and_the_narrow_shape()
    {
        SharedPipeline.EnsureInstalled();
        using var endpoint = new FakeAwsEndpoint("{}");
        using var ecs = new Amazon.ECS.AmazonECSClient(Creds, Config(new Amazon.ECS.AmazonECSConfig(), endpoint.Url));
        var force = new Amazon.ECS.Model.UpdateServiceRequest { Cluster = "c", Service = "s", ForceNewDeployment = true };
        var scaleToZero = new Amazon.ECS.Model.UpdateServiceRequest { Cluster = "c", Service = "s", ForceNewDeployment = true, DesiredCount = 0 };

        using (RequestScope.Begin(new RequestScopeInfo("p", null, "us-east-1", Elevated: false, Approved: true)))
            await Assert.ThrowsAsync<WriteOperationBlockedException>(() => ecs.UpdateServiceAsync(force));
        using (RequestScope.Begin(new RequestScopeInfo("p", null, "us-east-1", Elevated: true, Approved: true)))
            await Assert.ThrowsAsync<WriteOperationBlockedException>(() => ecs.UpdateServiceAsync(scaleToZero));
        Assert.Equal(0, endpoint.Requests);

        using (RequestScope.Begin(new RequestScopeInfo("p", null, "us-east-1", Elevated: true, Approved: true)))
        {
            var ex = await Record.ExceptionAsync(() => ecs.UpdateServiceAsync(force));
            Assert.IsNotType<WriteOperationBlockedException>(ex);
        }
        Assert.Equal(1, endpoint.Requests);
    }

    [Fact]
    public async Task Write_request_is_blocked_before_anything_is_sent()
    {
        SharedPipeline.EnsureInstalled();
        using var endpoint = new FakeAwsEndpoint("{}");
        using var ssm = new AmazonSimpleSystemsManagementClient(Creds, Config(new AmazonSimpleSystemsManagementConfig(), endpoint.Url));
        using var ecs = new Amazon.ECS.AmazonECSClient(Creds, Config(new Amazon.ECS.AmazonECSConfig(), endpoint.Url));

        await Assert.ThrowsAsync<WriteOperationBlockedException>(() =>
            ssm.PutParameterAsync(new Ssm.PutParameterRequest { Name = "/blocked", Value = "x", Overwrite = true }));
        await Assert.ThrowsAsync<WriteOperationBlockedException>(() =>
            ecs.UpdateServiceAsync(new Amazon.ECS.Model.UpdateServiceRequest { Cluster = "c", Service = "s", ForceNewDeployment = true }));

        Assert.Equal(0, endpoint.Requests);
        Assert.Contains(SharedPipeline.Sink.Entries, e => e.Operation == "PutParameter" && e.Outcome == RequestOutcome.Blocked);
        Assert.Contains(SharedPipeline.Sink.Entries, e => e.Operation == "UpdateService" && e.Outcome == RequestOutcome.Blocked);
    }

    [Fact]
    public async Task GetParameter_is_allowed_and_log_contains_no_value_or_credentials()
    {
        SharedPipeline.EnsureInstalled();
        var marker = "/app/db-" + Guid.NewGuid().ToString("N");
        using var endpoint = new FakeAwsEndpoint("""{"Parameter":{"Name":"x","Type":"SecureString","Value":"TOPSECRET-PARAM","Version":3}}""");
        using var ssm = new AmazonSimpleSystemsManagementClient(Creds, Config(new AmazonSimpleSystemsManagementConfig(), endpoint.Url));

        var resp = await ssm.GetParameterAsync(new Ssm.GetParameterRequest { Name = marker, WithDecryption = true });

        Assert.Equal("TOPSECRET-PARAM", resp.Parameter.Value);
        var entry = Assert.Single(SharedPipeline.Sink.Entries, e => e.Parameters.Contains(marker));
        Assert.Equal(RequestOutcome.Success, entry.Outcome);
        Assert.Equal(200, entry.HttpStatus);
        Assert.Equal("req-123", entry.RequestId);
        Assert.Contains("WithDecryption=True", entry.Parameters);
        AssertNoSensitiveData(entry, "TOPSECRET-PARAM");
    }

    [Fact]
    public async Task GetSecretValue_log_contains_no_secret_value()
    {
        SharedPipeline.EnsureInstalled();
        var marker = "prod/api-" + Guid.NewGuid().ToString("N");
        using var endpoint = new FakeAwsEndpoint("""{"ARN":"arn:aws:secretsmanager:us-east-1:1:secret:x","Name":"x","SecretString":"{\"password\":\"TOPSECRET-SECRET\"}","VersionId":"v1"}""");
        using var sm = new AmazonSecretsManagerClient(Creds, Config(new AmazonSecretsManagerConfig(), endpoint.Url));

        var resp = await sm.GetSecretValueAsync(new Secrets.GetSecretValueRequest { SecretId = marker });

        Assert.Contains("TOPSECRET-SECRET", resp.SecretString);
        var entry = Assert.Single(SharedPipeline.Sink.Entries, e => e.Parameters.Contains(marker));
        AssertNoSensitiveData(entry, "TOPSECRET-SECRET");
    }

    private static void AssertNoSensitiveData(RequestLogEntry entry, string value)
    {
        var json = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain(value, json);
        Assert.DoesNotContain("SESSIONTOKEN123", json);
        Assert.DoesNotContain("SECRETACCESSKEY123", json);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redactor_logs_only_allowlisted_fields()
    {
        var text = RequestParameterRedactor.Describe(new Secrets.ListSecretsRequest { MaxResults = 100, NextToken = "opaque-token-value" });
        Assert.Contains("NextToken=yes", text);
        Assert.DoesNotContain("opaque-token-value", text);

        Assert.Equal("", RequestParameterRedactor.Describe(new Ssm.PutParameterRequest { Name = "n", Value = "should-not-log" }));
    }

    [Fact]
    public void Source_assemblies_do_not_reference_any_write_request_type()
    {
        var sdkAssemblies = new[]
        {
            typeof(AmazonSecretsManagerClient).Assembly,
            typeof(AmazonSimpleSystemsManagementClient).Assembly,
            typeof(Amazon.ElasticBeanstalk.AmazonElasticBeanstalkClient).Assembly,
            typeof(Amazon.ECS.AmazonECSClient).Assembly,
            typeof(Amazon.CloudWatch.AmazonCloudWatchClient).Assembly,
            typeof(Amazon.SecurityToken.AmazonSecurityTokenServiceClient).Assembly,
            typeof(Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient).Assembly,
            typeof(Amazon.EC2.AmazonEC2Client).Assembly,
            typeof(Amazon.RDS.AmazonRDSClient).Assembly,
            typeof(Amazon.ElastiCache.AmazonElastiCacheClient).Assembly,
            typeof(Amazon.SSO.AmazonSSOClient).Assembly,
        };

        var forbidden = sdkAssemblies
            .SelectMany(a => a.GetExportedTypes())
            .Where(t => typeof(AmazonWebServiceRequest).IsAssignableFrom(t) && !t.IsAbstract && t.Name.EndsWith("Request", StringComparison.Ordinal))
            .Where(t => !ReadOnlyGuard.AllowedRequestTypes.Contains(t) && !ReadOnlyGuard.ConfirmedOnlyRequestTypes.Contains(t))
            .Select(t => t.FullName!)
            .ToArray();
        Assert.Contains("Amazon.ECS.Model.DeleteServiceRequest", forbidden);
        Assert.Contains("Amazon.ECS.Model.StopTaskRequest", forbidden);
        Assert.Contains("Amazon.ElasticBeanstalk.Model.RebuildEnvironmentRequest", forbidden);
        Assert.Contains("Amazon.EC2.Model.StopInstancesRequest", forbidden);
        // Databases and caches are strictly read-only: no reboot, failover, modify or delete.
        Assert.Contains("Amazon.RDS.Model.RebootDBInstanceRequest", forbidden);
        Assert.Contains("Amazon.RDS.Model.FailoverDBClusterRequest", forbidden);
        Assert.Contains("Amazon.RDS.Model.ModifyDBInstanceRequest", forbidden);
        Assert.Contains("Amazon.ElastiCache.Model.ModifyReplicationGroupRequest", forbidden);
        Assert.Contains("Amazon.ElastiCache.Model.RebootCacheClusterRequest", forbidden);

        foreach (var assembly in new[]
                 {
                     typeof(AwsGateway).Assembly,
                     typeof(Skypeek.Core.IAwsGateway).Assembly,
                     typeof(Skypeek.Storage.Vault).Assembly,
                 })
        {
            var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            Assert.True(result.IsSuccessful,
                $"{assembly.GetName().Name} references write operations: {string.Join(", ", result.FailingTypeNames ?? [])}");
        }
    }
}
