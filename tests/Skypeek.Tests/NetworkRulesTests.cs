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
    public void Subnet_internet_access_follows_its_route_table()
    {
        RouteInfo Route(string destination, string? target, string state = "active") => new() { Destination = destination, Target = target, State = state };
        var s = new NetworkSnapshot
        {
            Subnets =
            [
                new SubnetInfo { Id = "pub", VpcId = "v", Cidr = "10.0.1.0/24", MapPublicIpOnLaunch = true },
                new SubnetInfo { Id = "priv", VpcId = "v", Cidr = "10.0.2.0/24" },
                new SubnetInfo { Id = "iso", VpcId = "v", Cidr = "10.0.3.0/24" },
                new SubnetInfo { Id = "dead", VpcId = "v", Cidr = "10.0.4.0/24" },
                new SubnetInfo { Id = "hub", VpcId = "v", Cidr = "10.0.5.0/24" },
            ],
            RouteTables =
            [
                new RouteTableInfo { Id = "main", VpcId = "v", IsMain = true, Routes = [Route("10.0.0.0/16", "local")] },
                new RouteTableInfo { Id = "rt-pub", VpcId = "v", SubnetIds = ["pub"], Routes = [Route("10.0.0.0/16", "local"), Route("0.0.0.0/0", "igw-1"), Route("::/0", "igw-1")] },
                new RouteTableInfo { Id = "rt-priv", VpcId = "v", SubnetIds = ["priv"], Routes = [Route("0.0.0.0/0", "nat-1")] },
                new RouteTableInfo { Id = "rt-dead", VpcId = "v", SubnetIds = ["dead"], Routes = [Route("0.0.0.0/0", "nat-gone", "blackhole")] },
                new RouteTableInfo { Id = "rt-hub", VpcId = "v", SubnetIds = ["hub"], Routes = [Route("0.0.0.0/0", "tgw-1")] },
            ],
            NatGateways = [new NatGatewayInfo { Id = "nat-1", VpcId = "v", SubnetId = "pub", State = "available", PublicIps = ["3.3.3.3"] }],
        };
        NetworkRules.SubnetRoutingOf(s, "pub", out var pub);
        Assert.Equal(InternetAccess.Public, pub.Access);
        Assert.Equal("IPv6: public through igw-1", pub.Ipv6Summary);
        NetworkRules.SubnetRoutingOf(s, "priv", out var priv);
        Assert.Equal(InternetAccess.Nat, priv.Access);
        Assert.Contains("3.3.3.3", priv.Summary);
        Assert.Null(priv.Warning);
        NetworkRules.SubnetRoutingOf(s, "iso", out var iso);
        Assert.Equal(InternetAccess.Isolated, iso.Access);
        Assert.False(iso.ExplicitAssociation);
        Assert.Equal("main", iso.RouteTable!.Id);
        NetworkRules.SubnetRoutingOf(s, "dead", out var dead);
        Assert.Equal(InternetAccess.Blackhole, dead.Access);
        NetworkRules.SubnetRoutingOf(s, "hub", out var hub);
        Assert.Equal(InternetAccess.Other, hub.Access);

        // A NAT gateway placed in a subnet without an internet route cannot reach the internet.
        s.NatGateways[0] = new NatGatewayInfo { Id = "nat-1", VpcId = "v", SubnetId = "iso", State = "available", PublicIps = ["3.3.3.3"] };
        NetworkRules.SubnetRoutingOf(s, "priv", out priv);
        Assert.Contains("cannot reach the internet", priv.Warning);
    }

    [Fact]
    public void Price_list_products_are_parsed_and_the_plain_on_demand_price_is_picked()
    {
        string Product(string usageType, string unit, string usd) =>
            "{\"product\":{\"attributes\":{\"usagetype\":\"" + usageType + "\"}},\"terms\":{\"OnDemand\":{\"x\":{\"priceDimensions\":{\"y\":{\"unit\":\"" + unit
            + "\",\"description\":\"d " + usageType + "\",\"pricePerUnit\":{\"USD\":\"" + usd + "\"}}}}}}}";
        var items = new[]
        {
            Product("USE2-LoadBalancerUsage", "Hrs", "0.0225"),
            Product("USE2-TS-LoadBalancerUsage", "Hrs", "0.005"),
            Product("USE2-LCUUsage", "LCU-Hrs", "0.008"),
        }.SelectMany(CostRules.ParseProduct).ToList();
        Assert.Equal(3, items.Count);
        var query = CostRules.QueryFor(CostRules.LoadBalancerKey("application"), "us-east-2")!;
        Assert.Equal("us-east-2", query.Filters["regionCode"]);
        var pick = CostRules.Pick(query, items, DateTime.UtcNow);
        Assert.Equal(0.0225, pick.Usd);
        Assert.False(pick.Ambiguous);
        Assert.Equal("NatGateway-Hours", CostRules.WithoutRegion("NatGateway-Hours"));
        Assert.Equal("NatGateway-Hours", CostRules.WithoutRegion("EU-NatGateway-Hours"));
    }

    [Fact]
    public void Estimates_for_databases_fargate_and_environments()
    {
        var prices = new Dictionary<string, PriceEntry>
        {
            [CostRules.RdsKey("db.m6g.large", "postgres", CostRules.RdsDeployment.Multi)] = new() { Usd = 0.318 },
            [CostRules.RdsStorageKey("gp3", "postgres", CostRules.RdsDeployment.Multi)] = new() { Usd = 0.23 },
            [CostRules.FargateCpuKey] = new() { Usd = 0.04048 },
            [CostRules.FargateMemoryKey] = new() { Usd = 0.004445 },
            [CostRules.Ec2Key("t3.medium", null)] = new() { Usd = 0.0416 },
        };
        PriceEntry? Price(string key) => prices.GetValueOrDefault(key);

        var db = new RdsInstanceStatus { Snapshot = new RdsInstanceSnapshot { Identifier = "db", Engine = "postgres", InstanceClass = "db.m6g.large", MultiAz = true, StorageType = "gp3", AllocatedStorageGb = 100, Status = "available" } };
        var dbCost = CostRules.Estimate(db, Price)!;
        Assert.Equal(0.318 * 730 + 0.23 * 100, dbCost.MonthlyUsd, precision: 6);
        Assert.True(dbCost.Complete);

        var svc = new EcsServiceStatus { Snapshot = new EcsServiceSnapshot { ServiceName = "api", LaunchType = "FARGATE",
            Tasks = [new EcsTaskInfo { Cpu = "512", Memory = "1024" }, new EcsTaskInfo { Cpu = "512", Memory = "1024" }] } };
        Assert.Equal((1 * 0.04048 + 2 * 0.004445) * 730, CostRules.Estimate(svc, Price)!.MonthlyUsd, precision: 6);

        var env = new EbEnvironmentStatus { Snapshot = new EbEnvironmentSnapshot { EnvironmentName = "shop",
            InstanceHealth = [new EbInstanceHealth { InstanceId = "i-1", InstanceType = "t3.medium", State = "running" }, new EbInstanceHealth { InstanceId = "i-2", InstanceType = "t3.medium", State = "running" }] } };
        Assert.Equal(2 * 0.0416 * 730, CostRules.Estimate(env, Price)!.MonthlyUsd, precision: 6);

        // Billed per-resource costs match instance ids and ARNs.
        var actual = new ActualCosts { ResourceLast14Days = new() { ["i-1"] = 3, ["i-2"] = 4, ["arn:aws:rds:us-east-1:1:db:other"] = 9 } };
        Assert.Equal(7, CostRules.Actual14Days(new EbEnvironmentStatus { Snapshot = new EbEnvironmentSnapshot { InstanceIds = ["i-1", "i-2"] } }, actual));
    }

    [Fact]
    public void Rds_estimates_cover_provisioned_iops_multi_az_clusters_io_optimized_aurora_and_extended_support()
    {
        var prices = new Dictionary<string, PriceEntry>
        {
            [CostRules.RdsKey("db.r7g.large", "mysql", CostRules.RdsDeployment.Multi)] = new() { Usd = 0.478 },
            [CostRules.RdsStorageKey("gp3", "mysql", CostRules.RdsDeployment.Multi)] = new() { Usd = 0.23 },
            [CostRules.RdsIopsKey("gp3", "mysql", CostRules.RdsDeployment.Multi)] = new() { Usd = 0.04 },
            [CostRules.RdsThroughputKey("mysql", CostRules.RdsDeployment.Multi)] = new() { Usd = 0.16 },
            [CostRules.RdsKey("db.m6gd.large", "mysql", CostRules.RdsDeployment.Cluster)] = new() { Usd = 0.522 },
            [CostRules.RdsStorageKey("io1", "mysql", CostRules.RdsDeployment.Cluster)] = new() { Usd = 0.25 },
            [CostRules.RdsIopsKey("io1", "mysql", CostRules.RdsDeployment.Cluster)] = new() { Usd = 0.3 },
            [CostRules.RdsKey("db.r6g.large", "aurora-postgresql", CostRules.RdsDeployment.Single, ioOptimized: true)] = new() { Usd = 0.338 },
        };
        PriceEntry? Price(string key) => prices.GetValueOrDefault(key);
        RdsInstanceStatus Db(RdsInstanceSnapshot s) => new() { Snapshot = s };

        // gp3 at its baseline (12,000 IOPS / 500 MiB/s from 400 GB) costs nothing extra; above it, the rest is billed.
        var atBaseline = new RdsInstanceSnapshot { Identifier = "a", Engine = "mysql", InstanceClass = "db.r7g.large", MultiAz = true, Status = "available",
            StorageType = "gp3", AllocatedStorageGb = 1500, Iops = 12000, StorageThroughputMbps = 500 };
        Assert.Equal((0, 0), CostRules.BilledStoragePerformance(atBaseline));
        Assert.DoesNotContain(CostRules.KeysFor(Db(atBaseline)), k => k.StartsWith("rdsiops") || k.StartsWith("rdstput"));
        Assert.Equal(0.478 * 730 + 0.23 * 1500, CostRules.Estimate(Db(atBaseline), Price)!.MonthlyUsd, precision: 6);
        var small = new RdsInstanceSnapshot { Identifier = "b", Engine = "mysql", InstanceClass = "db.r7g.large", MultiAz = true, Status = "available",
            StorageType = "gp3", AllocatedStorageGb = 200, Iops = 6000, StorageThroughputMbps = 250 };
        Assert.Equal((3000, 125), CostRules.BilledStoragePerformance(small));
        Assert.Contains(CostRules.RdsIopsKey("gp3", "mysql", CostRules.RdsDeployment.Multi), CostRules.KeysFor(Db(small)));
        Assert.Equal(0.478 * 730 + 0.23 * 200 + 0.04 * 3000 + 0.16 * 125, CostRules.Estimate(Db(small), Price)!.MonthlyUsd, precision: 6);
        var iopsQuery = CostRules.QueryFor(CostRules.RdsIopsKey("gp3", "mysql", CostRules.RdsDeployment.Multi), "us-east-2")!;
        Assert.Equal("RDS:Multi-AZ-GP3-PIOPS", iopsQuery.UsageType);
        Assert.Equal("RDS:PIOPS", CostRules.QueryFor(CostRules.RdsIopsKey("io1", "mysql", CostRules.RdsDeployment.Single), "us-east-1")!.UsageType);

        // A Multi-AZ DB cluster is priced once, on the writer; readers show zero.
        var writer = new RdsInstanceSnapshot { Identifier = "w", ClusterIdentifier = "c", IsClusterWriter = true, Engine = "mysql", InstanceClass = "db.m6gd.large",
            Status = "available", StorageType = "io1", AllocatedStorageGb = 100, Iops = 1000 };
        var reader = new RdsInstanceSnapshot { Identifier = "r", ClusterIdentifier = "c", IsClusterWriter = false, Engine = "mysql", InstanceClass = "db.m6gd.large",
            Status = "available", StorageType = "io1", AllocatedStorageGb = 100, Iops = 1000 };
        Assert.Equal(CostRules.RdsDeployment.Cluster, CostRules.Deployment(writer));
        Assert.Equal(0.522 * 730 + 0.25 * 100 + 0.3 * 1000, CostRules.Estimate(Db(writer), Price)!.MonthlyUsd, precision: 6);
        Assert.Empty(CostRules.KeysFor(Db(reader)));
        Assert.Equal(0, CostRules.Estimate(Db(reader), Price)!.MonthlyUsd);
        Assert.Equal("Multi-AZ (readable standbys)", CostRules.QueryFor(CostRules.RdsKey("db.m6gd.large", "mysql", CostRules.RdsDeployment.Cluster), "us-east-1")!.Filters["deploymentOption"]);

        // Aurora I/O-Optimized uses its own instance price, which Pick must not exclude.
        var aurora = new RdsInstanceSnapshot { Identifier = "au", ClusterIdentifier = "ac", IsClusterWriter = true, Engine = "aurora-postgresql", InstanceClass = "db.r6g.large",
            Status = "available", StorageType = "aurora-iopt1" };
        Assert.True(aurora.IsIoOptimized);
        Assert.Equal(0.338 * 730, CostRules.Estimate(Db(aurora), Price)!.MonthlyUsd, precision: 6);
        var ioptQuery = CostRules.QueryFor(CostRules.RdsKey("db.r6g.large", "aurora-postgresql", CostRules.RdsDeployment.Single, ioOptimized: true), "us-east-1")!;
        var items = new[]
        {
            new PriceItem("InstanceUsage:db.r6g.large", "Hrs", 0.26, "standard", new Dictionary<string, string>()),
            new PriceItem("InstanceUsageIOOptimized:db.r6g.large", "Hrs", 0.338, "io-optimized", new Dictionary<string, string>()),
        };
        Assert.Equal(0.338, CostRules.Pick(ioptQuery, items, DateTime.UtcNow).Usd);
        Assert.Equal(0.26, CostRules.Pick(CostRules.QueryFor(CostRules.RdsKey("db.r6g.large", "aurora-postgresql", CostRules.RdsDeployment.Single), "us-east-1")!, items, DateTime.UtcNow).Usd);

        // Extended Support: flagged (and "at least") once it has started, unless the instance opted out.
        Assert.Equal("8.4", CostRules.MajorVersion("8.4.9", ["8.0", "8.4"]));
        Assert.Equal("8.0", CostRules.MajorVersion("8.0.mysql_aurora.3.05.2", ["8.0", "5.7"]));
        Assert.Equal("16", CostRules.MajorVersion("16.3", ["1", "16"]));
        var starts = new Dictionary<string, DateTime> { ["mysql|8.0"] = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) };
        var old = new RdsInstanceSnapshot { Identifier = "o", Engine = "mysql", EngineVersion = "8.0.39", InstanceClass = "db.r7g.large", MultiAz = true, Status = "available",
            StorageType = "gp3", AllocatedStorageGb = 1500, Iops = 12000, StorageThroughputMbps = 500 };
        var flagged = CostRules.WithEngineSupport(CostRules.Estimate(Db(old), Price), old, starts, new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc))!;
        Assert.False(flagged.Complete);
        Assert.Contains("Extended Support since 2026-08-01", flagged.Basis);
        var soon = CostRules.WithEngineSupport(CostRules.Estimate(Db(old), Price), old, starts, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))!;
        Assert.True(soon.Complete);
        Assert.Contains("standard support for mysql 8.0 ends 2026-07-31", soon.Basis);
        var optedOut = new RdsInstanceSnapshot { Identifier = "x", Engine = "mysql", EngineVersion = "8.0.39", InstanceClass = "db.r7g.large", MultiAz = true, Status = "available",
            EngineLifecycleSupport = "open-source-rds-extended-support-disabled" };
        Assert.True(CostRules.WithEngineSupport(CostRules.Estimate(Db(optedOut), Price), optedOut, starts, new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc))!.Complete);
        var current = new RdsInstanceSnapshot { Identifier = "n", Engine = "mysql", EngineVersion = "8.4.9", InstanceClass = "db.r7g.large", MultiAz = true, Status = "available" };
        var unaffected = CostRules.Estimate(Db(current), Price);
        Assert.Same(unaffected, CostRules.WithEngineSupport(unaffected, current, starts, DateTime.UtcNow));
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
