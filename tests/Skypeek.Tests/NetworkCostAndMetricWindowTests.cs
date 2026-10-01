using Skypeek.Core.Health;
using Skypeek.Core.Models;

namespace Skypeek.Tests;

public class NetworkCostAndMetricWindowTests
{
    private static PriceEntry? Prices(string key) => key switch
    {
        CostRules.NatKey => new PriceEntry { Usd = 0.045 },
        CostRules.EndpointKey => new PriceEntry { Usd = 0.01 },
        CostRules.Ipv4Key => new PriceEntry { Usd = 0.005 },
        CostRules.SecretKey => new PriceEntry { Usd = 0.40 },
        CostRules.AdvancedParameterKey => new PriceEntry { Usd = 0.05 / 730 },
        CostRules.VpnKey => new PriceEntry { Usd = 0.05 },
        _ => null,
    };

    [Fact]
    public void Vpc_cost_counts_nat_interface_endpoints_per_zone_and_public_addresses()
    {
        var s = new NetworkSnapshot
        {
            Vpcs = [new VpcInfo { Id = "vpc-1" }, new VpcInfo { Id = "vpc-2" }],
            NatGateways = [new NatGatewayInfo { Id = "nat-1", VpcId = "vpc-1", State = "available" }, new NatGatewayInfo { Id = "nat-old", VpcId = "vpc-1", State = "deleted" }],
            Endpoints =
            [
                new VpcEndpointInfo { Id = "vpce-ssm", VpcId = "vpc-1", Type = "Interface", State = "available", SubnetIds = ["subnet-a", "subnet-b"] },
                new VpcEndpointInfo { Id = "vpce-s3", VpcId = "vpc-1", Type = "Gateway", State = "available" },
            ],
            Interfaces =
            [
                new NetworkInterfaceInfo { Id = "eni-1", VpcId = "vpc-1", PublicIps = ["54.1.1.1"] },
                new NetworkInterfaceInfo { Id = "eni-2", VpcId = "vpc-1", PublicIps = ["54.1.1.2"] },
                new NetworkInterfaceInfo { Id = "eni-3", VpcId = "vpc-2" },
            ],
            ElasticIps = [new ElasticIpInfo { PublicIp = "3.3.3.3" }, new ElasticIpInfo { PublicIp = "54.1.1.1", NetworkInterfaceId = "eni-1" }],
        };
        var vpc = CostRules.VpcEstimate(s, "vpc-1", Prices)!;
        // NAT 0.045 + 2 endpoint zones × 0.01 + 2 addresses × 0.005, × 730 h.
        Assert.Equal((0.045 + 0.02 + 0.01) * 730, vpc.MonthlyUsd, 3);
        Assert.Contains("2 interface endpoint zone(s)", vpc.Basis);
        Assert.Null(CostRules.VpcEstimate(s, "vpc-2", Prices));
        Assert.Equal(0.005 * 730, CostRules.IdleAddressEstimate(s, Prices)!.MonthlyUsd, 3);
        Assert.Equal(vpc.MonthlyUsd + 0.005 * 730, CostRules.NetworkEstimate(s, Prices)!.MonthlyUsd, 3);
        Assert.Contains(CostRules.Ipv4Key, CostRules.KeysFor(new TargetHealth(), s));
        Assert.Contains(CostRules.EndpointKey, CostRules.KeysFor(new TargetHealth(), s));
    }

    [Fact]
    public void Secrets_and_vpn_connections_are_priced()
    {
        var catalog = CostRules.CatalogEstimate(10, 2, Prices)!;
        Assert.Equal(10 * 0.40 + 2 * 0.05, catalog.MonthlyUsd, 3);
        Assert.Null(CostRules.CatalogEstimate(0, 0, Prices));

        var vpn = new VpnConnectionStatus { Snapshot = new VpnConnectionSnapshot { Id = "vpn-1", State = "available" } };
        Assert.Equal(36.5, CostRules.Estimate(vpn, Prices)!.MonthlyUsd, 2);
        Assert.Contains(CostRules.VpnKey, CostRules.KeysFor(vpn));
        Assert.Equal("Load Balancer-Gateway", CostRules.QueryFor(CostRules.LoadBalancerKey("gateway"), "us-east-1")!.Filters["productFamily"]);
        Assert.Equal("PublicIPv4:InUseAddress", CostRules.QueryFor(CostRules.Ipv4Key, "eu-west-1")!.Filters["usagetype~"]);
    }

    [Fact]
    public void Rare_metric_polls_show_the_average_over_their_period_and_the_last_five_minutes()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        // One minute points: the last hour at 50%, the last 5 minutes at 90%.
        var hour = Enumerable.Range(0, 60).Select(i => new MetricPoint(now.AddMinutes(-59 + i), i >= 55 ? 90 : 50)).ToList();
        var recent = HealthRules.EvaluateMetric("CPU", hour, 80, 95, 10, now);
        Assert.Equal(90, recent.Current);
        Assert.False(recent.ShowsAverage);
        Assert.Equal(90, recent.Headline);
        Assert.Contains("last 5 min", recent.Display);

        // Six hours of 5-minute points (an hourly-or-slower poll): the gauge shows the average.
        var sixHours = Enumerable.Range(0, 72).Select(i => new MetricPoint(now.AddMinutes(-355 + i * 5), i % 2 == 0 ? 20 : 40)).ToList();
        var rare = HealthRules.EvaluateMetric("CPU", sixHours, 80, 95, 10, now);
        Assert.True(rare.ShowsAverage);
        Assert.Equal(30, rare.Headline!.Value, 1);
        Assert.Contains("over the last 6 h", rare.Display);
        Assert.Equal(HealthLevel.Ok, rare.Level);
    }
}
