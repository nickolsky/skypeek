using Skypeek.Core.Credentials;
using Skypeek.Core.Health;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Tests;

public class HealthRulesTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private static List<MetricPoint> Series(int periodMinutes, params double[] values) =>
        values.Select((v, i) => new MetricPoint(Now.AddMinutes(-(values.Length - 1 - i) * periodMinutes), v)).ToList();

    [Fact]
    public void Single_spike_is_ok()
    {
        var eval = HealthRules.EvaluateMetric("CPU", Series(1, 20, 20, 20, 20, 20, 20, 20, 20, 20, 99), 80, 90, 10, Now);
        Assert.Equal(HealthLevel.Ok, eval.Level);
        Assert.Equal(99, eval.Peak);
    }

    [Fact]
    public void Sustained_breach_on_one_minute_data()
    {
        var values = Enumerable.Repeat(50.0, 10).Concat(Enumerable.Repeat(85.0, 10)).ToArray();
        Assert.Equal(HealthLevel.Warn, HealthRules.EvaluateMetric("CPU", Series(1, values), 80, 90, 10, Now).Level);

        var critical = Enumerable.Repeat(95.0, 10).ToArray();
        Assert.Equal(HealthLevel.Critical, HealthRules.EvaluateMetric("CPU", Series(1, critical), 80, 90, 10, Now).Level);
    }

    [Fact]
    public void Sustained_breach_on_five_minute_ec2_data()
    {
        // 10 sustained minutes = 2 points at a 5-minute period.
        var eval = HealthRules.EvaluateMetric("CPU", Series(5, 10, 10, 10, 92, 93), 80, 90, 10, Now);
        Assert.Equal(300, eval.PeriodSeconds);
        Assert.Equal(HealthLevel.Critical, eval.Level);

        Assert.Equal(HealthLevel.Ok, HealthRules.EvaluateMetric("CPU", Series(5, 10, 10, 10, 10, 93), 80, 90, 10, Now).Level);
    }

    [Fact]
    public void Stale_data_is_unknown()
    {
        var old = Series(1, 95, 95, 95).Select(p => p with { Timestamp = p.Timestamp.AddHours(-2) }).ToList();
        Assert.Equal(HealthLevel.Unknown, HealthRules.EvaluateMetric("CPU", old, 80, 90, 1, Now).Level);
    }

    [Fact]
    public void Per_resource_override_beats_target_and_global()
    {
        var settings = new AppSettings { EcsThresholds = new(80, 90, 80, 90) };
        var target = new Target { Id = 3, EcsThresholds = new(70, 75, 70, 75) };
        var key = ResourceKeys.Ecs(3, "c", "s");

        Assert.Equal(70, HealthRules.ResolveThresholds(settings, target, key, true).CpuWarn);
        settings.ResourceThresholds[key] = new(50, 60, 50, 60);
        Assert.Equal(50, HealthRules.ResolveThresholds(settings, target, key, true).CpuWarn);
        Assert.Equal(80, HealthRules.ResolveThresholds(settings, new Target { Id = 4 }, "other", true).CpuWarn);
    }

    [Fact]
    public void Alarm_matching_for_ecs_and_eb()
    {
        var svc = new EcsServiceSnapshot { ClusterName = "prod", ServiceName = "api" };
        var ecsAlarm = new AlarmInfo { Name = "api-cpu", Namespace = "AWS/ECS", MetricName = "CPUUtilization", Dimensions = new() { ["ClusterName"] = "prod", ["ServiceName"] = "api" } };
        Assert.True(HealthRules.AlarmMatchesEcs(ecsAlarm, svc));
        Assert.False(HealthRules.AlarmMatchesEcs(ecsAlarm, new EcsServiceSnapshot { ClusterName = "prod", ServiceName = "web" }));

        var env = new EbEnvironmentSnapshot { EnvironmentName = "web-prod", InstanceIds = ["i-1"], AutoScalingGroups = ["asg-web"] };
        Assert.True(HealthRules.AlarmMatchesEb(new AlarmInfo { Dimensions = new() { ["InstanceId"] = "i-1" } }, env));
        Assert.True(HealthRules.AlarmMatchesEb(new AlarmInfo { Dimensions = new() { ["AutoScalingGroupName"] = "asg-web" } }, env));
        Assert.True(HealthRules.AlarmMatchesEb(new AlarmInfo { Dimensions = new() { ["EnvironmentName"] = "web-prod" } }, env));
        Assert.False(HealthRules.AlarmMatchesEb(new AlarmInfo { Dimensions = new() { ["InstanceId"] = "i-2" } }, env));
    }

    [Fact]
    public void Active_alarm_is_critical_and_recent_alarm_is_badge_only()
    {
        var status = new EcsServiceStatus { BaseLevel = HealthLevel.Ok };
        status.Alarms = [new AlarmInfo { Name = "old", State = "OK", RecentAlarmAt = Now.AddHours(-3) }];
        HealthRules.Recompute(status, ThresholdSettings.Default, 10);
        Assert.Equal(HealthLevel.Ok, status.Level);
        Assert.Equal(1, status.RecentAlarmCount);

        status.Alarms.Add(new AlarmInfo { Name = "now", State = "ALARM" });
        HealthRules.Recompute(status, ThresholdSettings.Default, 10);
        Assert.Equal(HealthLevel.Critical, status.Level);
        Assert.Contains(status.Reasons, r => r.Contains("now"));
    }

    [Fact]
    public void Eb_failed_deploy_after_last_success_is_critical()
    {
        var env = new EbEnvironmentSnapshot { Health = "Green", Status = "Ready" };
        var events = new List<EbEvent>
        {
            new() { Date = Now.AddHours(-5), Severity = "INFO", Message = "Environment update completed successfully." },
            new() { Date = Now.AddHours(-1), Severity = "ERROR", Message = "Failed to deploy application." },
        };
        var (level, reasons) = HealthRules.EvaluateEb(env, events, Now);
        Assert.Equal(HealthLevel.Critical, level);
        Assert.Contains(reasons, r => r.StartsWith("Deploy failed"));

        events.Add(new EbEvent { Date = Now.AddMinutes(-40), Severity = "INFO", Message = "Environment update completed successfully." });
        Assert.Equal(HealthLevel.Ok, HealthRules.EvaluateEb(env, events, Now).Level);
    }

    [Fact]
    public void Eb_enhanced_health_causes_become_reasons()
    {
        var env = new EbEnvironmentSnapshot
        {
            Health = "Red",
            HealthStatus = "Degraded",
            Causes = ["30.0 % of the requests are erroring with HTTP 5xx.", "Impaired services on 2 out of 6 instances.", "third"],
        };
        var (level, reasons) = HealthRules.EvaluateEb(env, [], Now);
        Assert.Equal(HealthLevel.Critical, level);
        Assert.Contains("30.0 % of the requests are erroring with HTTP 5xx.", reasons);
        Assert.DoesNotContain("third", reasons);
    }

    [Fact]
    public void Ecs_rules()
    {
        var failed = new EcsServiceSnapshot { Status = "ACTIVE", Desired = 2, Running = 2, Deployments = [new() { Status = "PRIMARY", RolloutState = "FAILED" }] };
        Assert.Equal(HealthLevel.Critical, HealthRules.EvaluateEcs(failed, Now).Level);

        var down = new EcsServiceSnapshot { Status = "ACTIVE", Desired = 2, Running = 0 };
        Assert.Equal(HealthLevel.Critical, HealthRules.EvaluateEcs(down, Now).Level);

        var rolling = new EcsServiceSnapshot { Status = "ACTIVE", Desired = 2, Running = 1, Deployments = [new() { Status = "PRIMARY", RolloutState = "IN_PROGRESS", CreatedAt = Now.AddMinutes(-5) }] };
        Assert.Equal(HealthLevel.Ok, HealthRules.EvaluateEcs(rolling, Now).Level);

        var stuck = new EcsServiceSnapshot { Status = "ACTIVE", Desired = 2, Running = 2, Deployments = [new() { Status = "PRIMARY", RolloutState = "IN_PROGRESS", CreatedAt = Now.AddMinutes(-45) }] };
        Assert.Equal(HealthLevel.Warn, HealthRules.EvaluateEcs(stuck, Now).Level);
    }

    [Fact]
    public void Cost_estimate()
    {
        // 50 services x 2 + 20 instances = 120 metrics every 5 minutes.
        Assert.Equal(10.368m, HealthRules.EstimateMonthlyMetricsCostUsd(120, 5));
    }
}

public class TrayStatusTests
{
    private static readonly Target Target = new() { Id = 1, ProfileName = "p", Alias = "Prod" };

    private static TargetHealth HealthWith(HealthLevel level) => new()
    {
        TargetId = 1,
        Ecs = [new EcsServiceStatus { TargetId = 1, Level = level, Snapshot = new EcsServiceSnapshot { ClusterName = "c", ServiceName = "s" }, Reasons = ["x"] }],
    };

    private static TrayStatus Compute(TargetHealth health, bool warningsRed = true, CredentialState cred = CredentialState.Valid) =>
        TrayStatusCalculator.Compute([Target], [health], [new ProfileStatus("p", cred, null, "ExpiredToken", null)], (_, _) => null, warningsRed);

    [Fact]
    public void No_problems_is_normal_icon()
    {
        var status = Compute(HealthWith(HealthLevel.Ok));
        Assert.False(status.IsRed);
        Assert.Equal(0, status.Count);
    }

    [Fact]
    public void Critical_problem_is_red()
    {
        var status = Compute(HealthWith(HealthLevel.Critical));
        Assert.True(status.IsRed);
        Assert.Equal(1, status.Count);
    }

    [Fact]
    public void Halted_credentials_are_red()
    {
        var status = Compute(HealthWith(HealthLevel.Ok), cred: CredentialState.Halted);
        Assert.True(status.IsRed);
        Assert.Contains(status.Problems, p => p.Resource == "credentials");
    }

    [Fact]
    public void Warning_only_red_when_setting_enabled()
    {
        Assert.True(Compute(HealthWith(HealthLevel.Warn), warningsRed: true).IsRed);
        var off = Compute(HealthWith(HealthLevel.Warn), warningsRed: false);
        Assert.False(off.IsRed);
        Assert.Equal(1, off.Count);
    }
}

public class SearchIndexTests
{
    [Fact]
    public void Tokens_must_all_match_and_name_matches_rank_first()
    {
        var target = new Target { Id = 1, ProfileName = "p", Alias = "Main", Region = "us-east-1" };
        var index = new SearchIndex();
        index.Load(
        [
            new CatalogItem { TargetId = 1, Name = "/prod/db/password", Kind = CatalogKind.Parameter, Type = "SecureString" },
            new CatalogItem { TargetId = 1, Name = "/prod/api/key", Kind = CatalogKind.Parameter, Description = "db access" },
            new CatalogItem { TargetId = 1, Name = "stage/db", Kind = CatalogKind.Secret },
        ], [target], _ => "123456789012");

        var results = index.Search("prod db", new SearchFilter());
        Assert.Equal(["/prod/db/password", "/prod/api/key"], results.Select(r => r.Item.Name));

        Assert.Single(index.Search("db", new SearchFilter(Kind: CatalogKind.Secret)));
        Assert.Empty(index.Search("db", new SearchFilter(AccountId: "999")));
    }
}
