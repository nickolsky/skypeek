using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Health;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Tests;

public class SuppressionTests
{
    private static AlarmInfo Alarm(string name) => new() { Name = name, State = "ALARM" };

    [Theory]
    [InlineData("BackendCPUUtilizationLow", "BackendCPUUtilizationLow", true)]
    [InlineData("*Low", "FrontendCPUUtilizationLow", true)]
    [InlineData("*low", "FrontendCPUUtilizationLow", true)]
    [InlineData("Backend*", "FrontendCPUUtilizationLow", false)]
    [InlineData("api-??-cpu", "api-eu-cpu", true)]
    [InlineData("api.cpu", "apiXcpu", false)]
    public void Wildcards(string pattern, string name, bool expected) =>
        Assert.Equal(expected, HealthRules.WildcardMatch(pattern, name));

    [Fact]
    public void Target_tracking_alarms_are_ignored_by_default_only()
    {
        var alarm = Alarm("TargetTracking-service/PROD/api-AlarmLow-1234");
        Assert.NotNull(HealthRules.SuppressionReason(alarm, 1, new AppSettings()));
        Assert.Null(HealthRules.SuppressionReason(alarm, 1, new AppSettings { IgnoreTargetTrackingAlarms = false }));
    }

    [Fact]
    public void Target_scoped_rule_applies_only_to_that_target()
    {
        var settings = new AppSettings { SuppressedAlarms = [new AlarmSuppression("*Low", 2, DateTime.UtcNow)] };
        Assert.Null(HealthRules.SuppressionReason(Alarm("CpuLow"), 1, settings));
        Assert.NotNull(HealthRules.SuppressionReason(Alarm("CpuLow"), 2, settings));
    }

    [Fact]
    public void Suppressed_alarm_does_not_raise_level_or_tray()
    {
        var target = new Target { Id = 1, ProfileName = "p", Alias = "QA" };
        var service = new EcsServiceStatus
        {
            TargetId = 1,
            BaseLevel = HealthLevel.Ok,
            Snapshot = new EcsServiceSnapshot { ClusterName = "c", ServiceName = "s" },
            Alarms = [Alarm("BackendCPUUtilizationLow")],
        };
        var health = new TargetHealth { TargetId = 1, Ecs = [service], OtherAlarms = [Alarm("OtherLow")] };
        var settings = new AppSettings { SuppressedAlarms = [new AlarmSuppression("*Low", null, DateTime.UtcNow)] };

        HealthRules.ApplySuppression(health, 1, settings);
        HealthRules.Recompute(service, ThresholdSettings.Default, 10);

        Assert.Equal(HealthLevel.Ok, service.Level);
        Assert.Equal(0, service.ActiveAlarmCount);
        Assert.Equal(1, service.SuppressedAlarmCount);
        var tray = TrayStatusCalculator.Compute([target], [health], [], (_, _) => null, true);
        Assert.False(tray.IsRed);
        Assert.Equal(0, tray.Count);
    }
}

public class CauseSuppressionTests
{
    private const string TargetGroupCause = "One or more TargetGroups associated with the environment are in a reduced health state:\n - awseb-shop-default-qpsgx - Severe";

    private static EbEnvironmentStatus Env(params string[] causes) => new()
    {
        TargetId = 1,
        Snapshot = new EbEnvironmentSnapshot { EnvironmentName = "shop-qa-ui", Health = "Red", HealthStatus = "Severe", Causes = causes.ToList() },
    };

    [Fact]
    public void All_causes_suppressed_makes_environment_ok()
    {
        var eb = Env(TargetGroupCause);
        // Pattern typed on one line still matches the multi-line cause.
        var settings = new AppSettings { SuppressedCauses = [new CauseSuppression("One or more TargetGroups * reduced health state:*", 1, "shop-qa-ui", DateTime.UtcNow)] };

        HealthRules.ApplyCauseSuppression(eb, 1, settings, DateTime.UtcNow);
        HealthRules.Recompute(eb, ThresholdSettings.Default, 10);

        Assert.Equal(HealthLevel.Ok, eb.Level);
        Assert.True(Assert.Single(eb.CauseItems).Suppressed);
    }

    [Fact]
    public void Remaining_cause_keeps_environment_red()
    {
        var eb = Env(TargetGroupCause, "30 % of the requests are erroring with HTTP 5xx.");
        var settings = new AppSettings { SuppressedCauses = [new CauseSuppression(HealthRules.NormalizeCause(TargetGroupCause), null, null, DateTime.UtcNow)] };

        HealthRules.ApplyCauseSuppression(eb, 1, settings, DateTime.UtcNow);
        HealthRules.Recompute(eb, ThresholdSettings.Default, 10);

        Assert.Equal(HealthLevel.Critical, eb.Level);
        Assert.Contains(eb.Reasons, r => r.Contains("5xx"));
        Assert.DoesNotContain(eb.Reasons, r => r.Contains("TargetGroups"));
    }

    [Fact]
    public void Scope_limits_to_the_environment()
    {
        var settings = new AppSettings { SuppressedCauses = [new CauseSuppression("*TargetGroups*", 1, "shop-qa-ui", DateTime.UtcNow)] };
        Assert.NotNull(HealthRules.MatchCause(TargetGroupCause, 1, "shop-qa-ui", settings));
        Assert.Null(HealthRules.MatchCause(TargetGroupCause, 1, "shop-prod-ui", settings));
        Assert.Null(HealthRules.MatchCause(TargetGroupCause, 2, "shop-qa-ui", settings));
    }
}

public class ElevationTests
{
    private sealed class Approver(bool answer) : IElevationApprover
    {
        public List<ElevationRequest> Requests { get; } = new();

        public Task<bool> ApproveAsync(ElevationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer);
        }
    }

    private static async Task<CredentialMonitor> Monitor(TempDir dir)
    {
        var path = dir.File("credentials");
        await File.WriteAllTextAsync(path, CredentialsFile.Content("t"));
        var monitor = new CredentialMonitor(path, new MemoryHaltStore(), new FakeValidator(), _ => "us-east-1");
        await monitor.StartAsync(watch: false);
        return monitor;
    }

    private static readonly Target Target = new()
    {
        Id = 1,
        ProfileName = CredentialsFile.Profile,
        ElevatedProfileName = "123456789012_AWSAdministratorAccess",
        Region = "us-east-1",
    };

    [Fact]
    public async Task Denied_elevation_sends_nothing_and_is_logged()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        var sink = new CapturingSink();
        var approver = new Approver(false);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, sink, approver);

        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.GetSecretValueAsync(Target, "prod/db", elevated: true, CancellationToken.None));

        var request = Assert.Single(approver.Requests);
        Assert.Equal("123456789012_AWSAdministratorAccess", request.Profile);
        Assert.Equal("prod/db", request.Resource);
        var entry = Assert.Single(sink.Entries);
        Assert.Equal(RequestOutcome.Skipped, entry.Outcome);
        Assert.True(entry.Elevated);
    }

    [Fact]
    public async Task Elevation_requires_a_configured_profile()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, new CapturingSink(), new Approver(true));
        var noElevated = new Target { Id = 2, ProfileName = CredentialsFile.Profile, Region = "us-east-1" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.GetParameterValueAsync(noElevated, "/x", true, elevated: true, CancellationToken.None));
    }

    [Fact]
    public async Task Non_read_actions_always_use_the_elevated_key()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        var approver = new Approver(false);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, new CapturingSink(), approver);

        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.RequestEbLogsAsync(Target, "e-1", "api-prod", bundle: false, CancellationToken.None));
        var request = Assert.Single(approver.Requests);
        Assert.True(request.Elevated);
        Assert.Equal("123456789012_AWSAdministratorAccess", request.Profile);

        // ECS force deployment: same rules.
        var service = new EcsServiceSnapshot { ClusterName = "prod", ClusterArn = "arn:aws:ecs:us-east-1:1:cluster/prod", ServiceName = "api", Running = 2 };
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.ForceNewEcsDeploymentAsync(Target, service, CancellationToken.None));
        Assert.Equal(2, approver.Requests.Count);
        Assert.True(approver.Requests[1].Elevated);
        Assert.Contains("replaces all 2 running task(s)", approver.Requests[1].Explanation);
        approver.Requests.RemoveAt(1);

        // Without an elevated profile the action is refused outright; the read-only key is never used for it.
        var readOnlyOnly = new Target { Id = 3, ProfileName = CredentialsFile.Profile, Region = "us-east-1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.RequestEbLogsAsync(readOnlyOnly, "e-1", "api-prod", bundle: false, CancellationToken.None));
        Assert.Single(approver.Requests);
    }

    [Fact]
    public async Task Ec2_power_and_security_group_changes_use_the_elevated_key_and_typed_confirmation_where_it_matters()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        var approver = new Approver(false);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, new CapturingSink(), approver);
        var instance = new Ec2InstanceSnapshot
        {
            InstanceId = "i-0abc", Name = "bastion", State = "running", PublicIp = "203.0.113.9",
            Tags = new() { ["aws:autoscaling:groupName"] = "bastion-asg" },
        };

        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.RebootEc2InstanceAsync(Target, instance, CancellationToken.None));
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.StopEc2InstanceAsync(Target, instance, CancellationToken.None));
        Assert.All(approver.Requests, r => Assert.Equal("123456789012_AWSAdministratorAccess", r.Profile));
        Assert.Null(approver.Requests[0].ConfirmPhrase);
        var stop = approver.Requests[1];
        Assert.Equal("i-0abc", stop.ConfirmPhrase);
        Assert.Contains("public IP 203.0.113.9 is released", stop.Explanation);
        Assert.Contains("Auto Scaling group bastion-asg", stop.Explanation);

        var group = new SecurityGroupInfo { Id = "sg-1", Name = "web", VpcId = "vpc-1" };
        var https = new SecurityGroupRuleSpec(false, "tcp", 443, 443, RuleSourceKind.Ipv4, "10.0.0.0/16", "internal");
        var world = https with { Source = "0.0.0.0/0" };
        approver.Requests.Clear();
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.AddSecurityGroupRuleAsync(Target, group, https, CancellationToken.None));
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.AddSecurityGroupRuleAsync(Target, group, world, CancellationToken.None));
        var rule = new SecurityGroupRuleInfo { RuleId = "sgr-1", GroupId = "sg-1", Protocol = "tcp", FromPort = 22, ToPort = 22, CidrIpv4 = "10.0.0.0/8" };
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.DeleteSecurityGroupRuleAsync(Target, group, rule, CancellationToken.None));
        Assert.Null(approver.Requests[0].ConfirmPhrase);
        Assert.Equal("sg-1", approver.Requests[1].ConfirmPhrase);
        Assert.Contains("WHOLE INTERNET", approver.Requests[1].Explanation);
        Assert.Equal("sg-1", approver.Requests[2].ConfirmPhrase);
        Assert.Contains("inbound TCP port 22 from 10.0.0.0/8", approver.Requests[2].Explanation);

        // Invalid rules never reach the approval dialog.
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.AddSecurityGroupRuleAsync(Target, group, https with { Source = "10.0.0.300/16" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.UpdateSecurityGroupRuleAsync(Target, group, rule, rule.ToSpec() with { IsEgress = true }, CancellationToken.None));
        Assert.Equal(3, approver.Requests.Count);
    }

    [Fact]
    public async Task Secret_and_parameter_changes_use_the_elevated_key_and_deleting_needs_the_name_typed()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        var approver = new Approver(false);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, new CapturingSink(), approver);
        var secret = new CatalogItem { TargetId = 1, Kind = CatalogKind.Secret, Name = "prod/db" };
        var parameter = new CatalogItem { TargetId = 1, Kind = CatalogKind.Parameter, Name = "/app/url", Type = "String", Version = 3 };

        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.UpdateSecretValueAsync(Target, secret, "s3cr3t-value", CancellationToken.None));
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.CreateSecretAsync(Target, "prod/new", "s3cr3t-value", null, null, CancellationToken.None));
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.DeleteSecretAsync(Target, secret, 30, CancellationToken.None));
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.PutParameterValueAsync(Target, parameter, "https://example.test", CancellationToken.None));
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.DeleteParameterAsync(Target, parameter, CancellationToken.None));
        Assert.Equal(5, approver.Requests.Count);
        Assert.All(approver.Requests, r =>
        {
            Assert.True(r.Elevated);
            Assert.Equal("123456789012_AWSAdministratorAccess", r.Profile);
            Assert.DoesNotContain("s3cr3t-value", r.Explanation ?? "");
            Assert.DoesNotContain("https://example.test", r.Explanation ?? "");
        });
        Assert.Null(approver.Requests[0].ConfirmPhrase);
        Assert.Equal("prod/db", approver.Requests[2].ConfirmPhrase);
        Assert.Contains("30 days", approver.Requests[2].Explanation);
        Assert.Contains("version 4", approver.Requests[3].Explanation);
        Assert.Equal("/app/url", approver.Requests[4].ConfirmPhrase);

        // Invalid input never reaches the approval dialog.
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.DeleteSecretAsync(Target, secret, 3, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.PutParameterValueAsync(Target, parameter, new string('x', 5000), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => gateway.CreateParameterAsync(Target, "/a", "v", "Binary", null, null, null, CancellationToken.None));
        Assert.Equal(5, approver.Requests.Count);
    }

    [Fact]
    public async Task Reachability_analyzer_is_one_approved_elevated_call_with_its_cost()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        var approver = new Approver(false);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, new CapturingSink(), approver);
        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.VerifyReachAsync(Target, "eni-1", "eni-2", null, "tcp", 5432, CancellationToken.None));
        var request = Assert.Single(approver.Requests);
        Assert.True(request.Elevated);
        Assert.Contains("$0.10", request.Explanation);
        Assert.Contains("deletes both", request.Explanation);
    }

    [Fact]
    public async Task Same_key_for_both_still_asks_before_every_elevated_action()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        var approver = new Approver(false);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, new CapturingSink(), approver);
        var sameKey = new Target { Id = 4, ProfileName = CredentialsFile.Profile, ElevatedProfileName = CredentialsFile.Profile, Region = "us-east-1" };
        Assert.True(sameKey.UsesSameKey);

        await Assert.ThrowsAsync<ElevationDeniedException>(() => gateway.RequestEbLogsAsync(sameKey, "e-1", "api-prod", bundle: false, CancellationToken.None));
        var request = Assert.Single(approver.Requests);
        Assert.True(request.Elevated);
        Assert.Equal(CredentialsFile.Profile, request.Profile);
    }

    [Fact]
    public async Task Normal_calls_never_ask_for_approval()
    {
        using var dir = new TempDir();
        var monitor = await Monitor(dir);
        monitor.ReportAuthFailure(CredentialsFile.Profile, monitor.Acquire(CredentialsFile.Profile).Fingerprint, "ExpiredToken");
        var approver = new Approver(true);
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, new CapturingSink(), approver);

        await Assert.ThrowsAsync<CredentialsUnavailableException>(() => gateway.GetSecretValueAsync(Target, "x", elevated: false, CancellationToken.None));
        Assert.Empty(approver.Requests);
    }
}
