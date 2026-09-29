using System.Text.Json;
using Skypeek.Core.Health;
using Skypeek.Core.Models;

namespace Skypeek.Tests;

public class NetworkRulesTests
{
    [Theory]
    [InlineData("tcp", 443, 443, "port 443")]
    [InlineData("tcp", 8000, 8100, "ports 8000-8100")]
    [InlineData("tcp", 0, 65535, "all ports")]
    [InlineData("-1", null, null, "all ports")]
    [InlineData("icmp", -1, -1, "all types")]
    [InlineData("icmp", 8, 0, "type 8 code 0")]
    public void Port_text(string protocol, int? from, int? to, string expected) =>
        Assert.Equal(expected, NetworkRules.PortText(protocol, from, to));

    [Fact]
    public void Rule_validation()
    {
        SecurityGroupRuleSpec Spec(string protocol = "tcp", int? from = 443, int? to = 443, RuleSourceKind kind = RuleSourceKind.Ipv4, string source = "10.0.0.0/16") =>
            new(false, protocol, from, to, kind, source, null);

        Assert.Null(NetworkRules.Validate(Spec()));
        Assert.Null(NetworkRules.Validate(Spec("-1", null, null)));
        Assert.Null(NetworkRules.Validate(Spec(kind: RuleSourceKind.Ipv6, source: "2001:db8::/32")));
        Assert.Null(NetworkRules.Validate(Spec(kind: RuleSourceKind.SecurityGroup, source: "sg-0123")));
        Assert.NotNull(NetworkRules.Validate(Spec(from: 500, to: 400)));
        Assert.NotNull(NetworkRules.Validate(Spec(from: 70000, to: 70000)));
        Assert.NotNull(NetworkRules.Validate(Spec(from: null, to: null)));
        Assert.NotNull(NetworkRules.Validate(Spec(source: "10.0.0.0")));
        Assert.NotNull(NetworkRules.Validate(Spec(source: "10.0.0.0/33")));
        Assert.NotNull(NetworkRules.Validate(Spec(kind: RuleSourceKind.Ipv6, source: "10.0.0.0/16")));
        Assert.NotNull(NetworkRules.Validate(Spec("ftp")));
        Assert.True(Spec(source: "0.0.0.0/0").IsOpenToWorld);
        Assert.False(Spec().IsOpenToWorld);
    }

    [Fact]
    public void Cidr_contains()
    {
        Assert.True(NetworkRules.Contains("10.0.0.0/16", "10.0.42.7"));
        Assert.False(NetworkRules.Contains("10.0.0.0/16", "10.1.0.1"));
        Assert.True(NetworkRules.Contains("0.0.0.0/0", "203.0.113.5"));
        Assert.False(NetworkRules.Contains("10.0.0.0/16", "2001:db8::1"));
    }

    [Theory]
    [InlineData(null, "i-0abc", null, null, "EC2 i-0abc")]
    [InlineData("ELB app/web-alb/0123", null, "interface", null, "Load balancer app/web-alb/0123")]
    [InlineData("RDSNetworkInterface", null, "interface", "amazon-rds", "RDS")]
    [InlineData("Interface for NAT Gateway nat-01", null, "nat_gateway", null, "NAT gateway")]
    [InlineData("arn:aws:ecs:us-east-1:1:attachment/abc", null, "interface", null, "ECS task")]
    [InlineData("AWS Lambda VPC ENI-fn-123", null, "lambda", null, "Lambda")]
    public void Interface_owner(string? description, string? instance, string? type, string? requester, string expected) =>
        Assert.Equal(expected, NetworkRules.InterfaceOwner(new NetworkInterfaceInfo
        {
            Id = "eni-1", Description = description, InstanceId = instance, InterfaceType = type, RequesterId = requester, Status = "in-use",
        }));

    [Fact]
    public void Subnet_usage()
    {
        var subnet = new SubnetInfo { Id = "subnet-1", Cidr = "10.0.1.0/24", AvailableIps = 241 };
        Assert.Equal(251, subnet.UsableIps);
        Assert.Equal(10, subnet.UsedIps);
        Assert.Equal("10/251 IPs used · 241 free", subnet.UsageText);
    }

    [Fact]
    public void Rule_summary_and_world_flag()
    {
        var rule = new SecurityGroupRuleInfo { RuleId = "sgr-1", GroupId = "sg-1", Protocol = "tcp", FromPort = 22, ToPort = 22, CidrIpv4 = "0.0.0.0/0" };
        Assert.True(rule.IsOpenToWorld);
        Assert.Equal("inbound TCP port 22 from 0.0.0.0/0", rule.Summary);
        Assert.Equal(new SecurityGroupRuleSpec(false, "tcp", 22, 22, RuleSourceKind.Ipv4, "0.0.0.0/0", null), rule.ToSpec());
    }

    [Fact]
    public void Network_snapshot_round_trips_through_json()
    {
        var snapshot = new NetworkSnapshot
        {
            TargetId = 3,
            DownloadedUtc = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc),
            Vpcs = [new VpcInfo { Id = "vpc-1", Name = "main", Cidrs = ["10.0.0.0/16"] }],
            SecurityGroups = [new SecurityGroupInfo { Id = "sg-1", Name = "web", Rules = [new SecurityGroupRuleInfo { RuleId = "sgr-1", GroupId = "sg-1", Protocol = "tcp", FromPort = 443, ToPort = 443, ReferencedGroupId = "sg-2" }] }],
        };
        var back = JsonSerializer.Deserialize<NetworkSnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal("main", back.Vpcs.Single().Name);
        Assert.Equal(RuleSourceKind.SecurityGroup, back.SecurityGroups.Single().Rules.Single().SourceKind);
    }

    [Fact]
    public void Ec2_health_rules()
    {
        Assert.Equal(HealthLevel.Critical, HealthRules.EvaluateEc2(new Ec2InstanceSnapshot { InstanceId = "i-1", State = "running", InstanceStatus = "impaired" }).Level);
        Assert.Equal(HealthLevel.Warn, HealthRules.EvaluateEc2(new Ec2InstanceSnapshot { InstanceId = "i-1", State = "running", ScheduledEvents = ["system-reboot after 10/1/2026"] }).Level);
        // Stopped on purpose is not a problem, even with stale status data.
        Assert.Equal(HealthLevel.Ok, HealthRules.EvaluateEc2(new Ec2InstanceSnapshot { InstanceId = "i-1", State = "stopped", SystemStatus = "impaired" }).Level);

        var alarm = new AlarmInfo { Name = "a", Namespace = "AWS/EC2", MetricName = "StatusCheckFailed", Dimensions = new() { ["InstanceId"] = "i-1" } };
        Assert.True(HealthRules.IsRelevantAlarm(alarm));
        Assert.True(HealthRules.AlarmMatchesEc2(alarm, new Ec2InstanceSnapshot { InstanceId = "i-1" }));
        Assert.True(HealthRules.IsRelevantAlarm(new AlarmInfo { Name = "b", Namespace = "AWS/ApplicationELB", MetricName = "HTTPCode_ELB_5XX_Count" }));
    }
}
