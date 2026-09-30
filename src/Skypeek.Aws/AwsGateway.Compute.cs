using Amazon.Runtime;
using Skypeek.Core.Models;
using Ec2 = Amazon.EC2.Model;
using Elb = Amazon.ElasticLoadBalancingV2.Model;

namespace Skypeek.Aws;

/// <summary>EC2 instances, load balancers and the network inventory (reads), plus the approved EC2 and security group actions.</summary>
public sealed partial class AwsGateway
{
    private static readonly List<string> LiveInstanceStates = ["pending", "running", "stopping", "stopped", "shutting-down"];
    private const int TargetHealthParallelism = 8;

    // ---------------- EC2 instances ----------------

    public Task<IReadOnlyList<Ec2InstanceSnapshot>> GetEc2InstancesAsync(Target target, CancellationToken ct, IReadOnlyList<string>? onlyIds = null) =>
        Call<IReadOnlyList<Ec2InstanceSnapshot>>(target, "DescribeInstances", async c =>
        {
            var instances = new List<Ec2.Instance>();
            string? token = null;
            var pages = 0;
            do
            {
                // MaxResults cannot be combined with instance ids.
                var resp = await c.Ec2.DescribeInstancesAsync(new Ec2.DescribeInstancesRequest
                {
                    InstanceIds = onlyIds?.ToList(),
                    Filters = onlyIds is null ? [new Ec2.Filter { Name = "instance-state-name", Values = LiveInstanceStates }] : null,
                    MaxResults = onlyIds is null ? 1000 : null,
                    NextToken = token,
                }, ct);
                instances.AddRange((resp.Reservations ?? []).SelectMany(r => r.Instances ?? []));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);

            var statuses = new Dictionary<string, Ec2.InstanceStatus>();
            if (instances.Count > 0)
            {
                try
                {
                    token = null;
                    pages = 0;
                    do
                    {
                        var resp = await c.Ec2.DescribeInstanceStatusAsync(new Ec2.DescribeInstanceStatusRequest
                        {
                            IncludeAllInstances = true,
                            InstanceIds = onlyIds?.ToList(),
                            MaxResults = onlyIds is null ? 1000 : null,
                            NextToken = token,
                        }, ct);
                        foreach (var s in resp.InstanceStatuses ?? [])
                            if (s.InstanceId is not null)
                                statuses[s.InstanceId] = s;
                        token = resp.NextToken;
                    } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
                }
                catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
                {
                    // Status checks are optional detail; the instance state is still known.
                }
            }

            return instances
                .Where(i => i.InstanceId is not null && i.State?.Name?.Value != "terminated")
                .Select(i => MapInstance(i, statuses.GetValueOrDefault(i.InstanceId!)))
                .OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });

    private static Ec2InstanceSnapshot MapInstance(Ec2.Instance i, Ec2.InstanceStatus? status)
    {
        var tags = (i.Tags ?? []).Where(t => t.Key is not null).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First().Value ?? "");
        var details = new List<string>();
        foreach (var (label, summary) in new[] { ("system", status?.SystemStatus), ("instance", status?.Status) })
            foreach (var d in summary?.Details ?? [])
                if (d.Status?.Value is { } s && s != "passed")
                    details.Add($"{label} {d.Name?.Value ?? "check"}: {s}{(d.ImpairedSince is { } since ? $" since {since.ToLocalTime():g}" : "")}");
        return new Ec2InstanceSnapshot
        {
            InstanceId = i.InstanceId ?? "",
            Name = tags.GetValueOrDefault("Name"),
            InstanceType = i.InstanceType?.Value,
            State = i.State?.Name?.Value ?? "",
            StateReason = i.StateReason?.Message ?? (string.IsNullOrEmpty(i.StateTransitionReason) ? null : i.StateTransitionReason),
            AvailabilityZone = i.Placement?.AvailabilityZone,
            VpcId = i.VpcId,
            SubnetId = i.SubnetId,
            PrivateIp = i.PrivateIpAddress,
            PublicIp = i.PublicIpAddress,
            PrivateDns = string.IsNullOrEmpty(i.PrivateDnsName) ? null : i.PrivateDnsName,
            PublicDns = string.IsNullOrEmpty(i.PublicDnsName) ? null : i.PublicDnsName,
            LaunchTime = i.LaunchTime,
            Platform = i.PlatformDetails,
            ImageId = i.ImageId,
            KeyName = i.KeyName,
            IamInstanceProfile = i.IamInstanceProfile?.Arn is { } arn ? arn[(arn.LastIndexOf('/') + 1)..] : null,
            Lifecycle = i.InstanceLifecycle?.Value,
            RootDeviceType = i.RootDeviceType?.Value,
            Interfaces = (i.NetworkInterfaces ?? []).OrderBy(n => n.Attachment?.DeviceIndex ?? 0).Select(n => new Ec2InterfaceInfo
            {
                Id = n.NetworkInterfaceId ?? "",
                SubnetId = n.SubnetId,
                PrivateIp = n.PrivateIpAddress,
                PrivateIps = (n.PrivateIpAddresses ?? []).Select(p => p.PrivateIpAddress).Where(p => p is not null).ToList()!,
                PublicIp = n.Association?.PublicIp,
                // Amazon-owned addresses are the auto-assigned ones that change on stop/start.
                IsElasticIp = n.Association?.PublicIp is not null && n.Association.IpOwnerId is { } owner && owner != "amazon",
                Ipv6 = (n.Ipv6Addresses ?? []).Select(a => a.Ipv6Address).Where(a => a is not null).ToList()!,
                SecurityGroups = (n.Groups ?? []).Select(g => new SecurityGroupRef(g.GroupId ?? "", g.GroupName ?? "")).ToList(),
                DeviceIndex = n.Attachment?.DeviceIndex,
            }).ToList(),
            SecurityGroups = (i.SecurityGroups ?? []).Select(g => new SecurityGroupRef(g.GroupId ?? "", g.GroupName ?? "")).ToList(),
            Tags = tags,
            SystemStatus = status?.SystemStatus?.Status?.Value,
            InstanceStatus = status?.Status?.Status?.Value,
            StatusDetails = details,
            ScheduledEvents = (status?.Events ?? [])
                .Where(e => e.Description is null || !(e.Description.StartsWith("[Completed]", StringComparison.Ordinal) || e.Description.StartsWith("[Canceled]", StringComparison.Ordinal)))
                .Select(e => $"{e.Code?.Value} {(e.NotBefore is { } nb ? $"after {nb.ToLocalTime():g}" : "")} {e.Description}".Trim())
                .ToList(),
        };
    }

    // ---------------- Load balancers ----------------

    public Task<IReadOnlyList<LoadBalancerSnapshot>> GetLoadBalancersAsync(Target target, CancellationToken ct, string? onlyArn = null) =>
        Call<IReadOnlyList<LoadBalancerSnapshot>>(target, "DescribeLoadBalancers", async c =>
        {
            var lbs = new List<Elb.LoadBalancer>();
            string? marker = null;
            var pages = 0;
            do
            {
                var resp = await c.Elb.DescribeLoadBalancersAsync(new Elb.DescribeLoadBalancersRequest
                {
                    LoadBalancerArns = onlyArn is null ? null : [onlyArn],
                    PageSize = onlyArn is null ? 400 : null,
                    Marker = marker,
                }, ct);
                lbs.AddRange(resp.LoadBalancers ?? []);
                marker = resp.NextMarker;
            } while (!string.IsNullOrEmpty(marker) && ++pages < MaxPages);
            if (lbs.Count == 0)
                return [];

            var groups = new List<Elb.TargetGroup>();
            marker = null;
            pages = 0;
            do
            {
                var resp = await c.Elb.DescribeTargetGroupsAsync(new Elb.DescribeTargetGroupsRequest
                {
                    LoadBalancerArn = onlyArn,
                    PageSize = 400,
                    Marker = marker,
                }, ct);
                groups.AddRange(resp.TargetGroups ?? []);
                marker = resp.NextMarker;
            } while (!string.IsNullOrEmpty(marker) && ++pages < MaxPages);

            // Target health per group that is attached to a load balancer: one call each, a few at a time
            // (accounts with many microservices easily have hundreds of target groups).
            using var gate = new SemaphoreSlim(TargetHealthParallelism);
            var mapped = await Task.WhenAll(groups.Where(g => g.LoadBalancerArns is { Count: > 0 }).Select(async g =>
            {
                List<LbTargetInfo> targets = [];
                string? error = null;
                await gate.WaitAsync(ct);
                try
                {
                    var health = await c.Elb.DescribeTargetHealthAsync(new Elb.DescribeTargetHealthRequest { TargetGroupArn = g.TargetGroupArn }, ct);
                    targets = (health.TargetHealthDescriptions ?? []).Select(t => new LbTargetInfo
                    {
                        Id = t.Target?.Id ?? "",
                        Port = t.Target?.Port,
                        AvailabilityZone = t.Target?.AvailabilityZone,
                        State = t.TargetHealth?.State?.Value ?? "",
                        Reason = t.TargetHealth?.Reason?.Value,
                        Description = t.TargetHealth?.Description,
                    }).OrderBy(t => t.Level == Core.Models.HealthLevel.Ok).ThenBy(t => t.Id).ToList();
                }
                catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
                {
                    error = $"{ex.ErrorCode}: {ex.Message}";
                }
                finally
                {
                    gate.Release();
                }
                return new TargetGroupInfo
                {
                    Name = g.TargetGroupName ?? "",
                    Arn = g.TargetGroupArn ?? "",
                    Protocol = g.Protocol?.Value,
                    Port = g.Port,
                    TargetType = g.TargetType?.Value,
                    VpcId = g.VpcId,
                    HealthCheck = HealthCheckText(g),
                    LoadBalancerArns = g.LoadBalancerArns ?? [],
                    Targets = targets,
                    Error = error,
                };
            }));

            return lbs.Select(lb => new LoadBalancerSnapshot
            {
                Name = lb.LoadBalancerName ?? "",
                Arn = lb.LoadBalancerArn ?? "",
                Type = lb.Type?.Value ?? "",
                Scheme = lb.Scheme?.Value,
                State = lb.State?.Code?.Value ?? "",
                StateReason = lb.State?.Reason,
                DnsName = lb.DNSName,
                VpcId = lb.VpcId,
                IpAddressType = lb.IpAddressType?.Value,
                Created = lb.CreatedTime,
                Zones = (lb.AvailabilityZones ?? []).Select(z => $"{z.ZoneName} ({z.SubnetId})").ToList(),
                SecurityGroups = lb.SecurityGroups ?? [],
                TargetGroups = mapped.Where(g => g.LoadBalancerArns.Contains(lb.LoadBalancerArn ?? "")).OrderBy(g => g.Name).ToList(),
            }).OrderBy(lb => lb.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });

    private static string HealthCheckText(Elb.TargetGroup g)
    {
        if (g.HealthCheckEnabled == false)
            return "health checks off";
        var parts = new List<string> { $"{g.HealthCheckProtocol?.Value} port {g.HealthCheckPort}" };
        if (!string.IsNullOrEmpty(g.HealthCheckPath))
            parts.Add(g.HealthCheckPath);
        parts.Add($"every {g.HealthCheckIntervalSeconds}s (timeout {g.HealthCheckTimeoutSeconds}s)");
        parts.Add($"healthy after {g.HealthyThresholdCount}, unhealthy after {g.UnhealthyThresholdCount}");
        if (g.Matcher?.HttpCode is { } http)
            parts.Add($"expects {http}");
        else if (g.Matcher?.GrpcCode is { } grpc)
            parts.Add($"expects gRPC {grpc}");
        return string.Join(" · ", parts);
    }

    public Task<IReadOnlyList<LbListenerInfo>> GetLoadBalancerListenersAsync(Target target, LoadBalancerSnapshot lb, CancellationToken ct) =>
        Call<IReadOnlyList<LbListenerInfo>>(target, "DescribeListeners", async c =>
        {
            var listeners = new List<Elb.Listener>();
            string? marker = null;
            var pages = 0;
            do
            {
                var resp = await c.Elb.DescribeListenersAsync(new Elb.DescribeListenersRequest { LoadBalancerArn = lb.Arn, Marker = marker }, ct);
                listeners.AddRange(resp.Listeners ?? []);
                marker = resp.NextMarker;
            } while (!string.IsNullOrEmpty(marker) && ++pages < 20);

            var result = new List<LbListenerInfo>();
            foreach (var l in listeners.OrderBy(l => l.Port))
            {
                var rules = new List<LbRuleInfo>();
                if (lb.Type == "application")
                {
                    marker = null;
                    pages = 0;
                    do
                    {
                        var resp = await c.Elb.DescribeRulesAsync(new Elb.DescribeRulesRequest { ListenerArn = l.ListenerArn, Marker = marker }, ct);
                        rules.AddRange((resp.Rules ?? []).Select(r => new LbRuleInfo(
                            r.IsDefault == true ? "default" : r.Priority ?? "?",
                            r.IsDefault == true,
                            r.IsDefault == true ? "any request not matched above" : ConditionsText(r.Conditions),
                            ActionsText(r.Actions))));
                        marker = resp.NextMarker;
                    } while (!string.IsNullOrEmpty(marker) && ++pages < 20);
                }
                else
                {
                    rules.Add(new LbRuleInfo("default", true, "all traffic", ActionsText(l.DefaultActions)));
                }
                // Numeric priority order, the default rule last.
                rules = rules.OrderBy(r => r.IsDefault).ThenBy(r => int.TryParse(r.Priority, out var p) ? p : int.MaxValue).ToList();
                result.Add(new LbListenerInfo(l.ListenerArn ?? "", l.Protocol?.Value ?? "", l.Port, l.SslPolicy,
                    (l.Certificates ?? []).Select(x => x.CertificateArn ?? "").ToList(), rules));
            }
            return result;
        });

    private static string ConditionsText(List<Elb.RuleCondition>? conditions) =>
        conditions is not { Count: > 0 } ? "any request" : string.Join(" AND ", conditions.Select(c => c.Field switch
        {
            "host-header" => $"host is {OneOf(c.HostHeaderConfig?.Values ?? c.Values)}",
            "path-pattern" => $"path is {OneOf(c.PathPatternConfig?.Values ?? c.Values)}",
            "http-header" => $"header {c.HttpHeaderConfig?.HttpHeaderName} is {OneOf(c.HttpHeaderConfig?.Values)}",
            "http-request-method" => $"method is {OneOf(c.HttpRequestMethodConfig?.Values ?? c.Values)}",
            "query-string" => $"query has {OneOf(c.QueryStringConfig?.Values?.Select(v => string.IsNullOrEmpty(v.Key) ? v.Value : $"{v.Key}={v.Value}").ToList())}",
            "source-ip" => $"source IP in {OneOf(c.SourceIpConfig?.Values ?? c.Values)}",
            _ => $"{c.Field} {OneOf(c.Values)}",
        }));

    private static string OneOf(List<string>? values) => values is not { Count: > 0 } ? "?" : values.Count == 1 ? values[0] : $"one of {string.Join(", ", values)}";

    private static string ActionsText(List<Elb.Action>? actions) =>
        actions is not { Count: > 0 } ? "no action" : string.Join(", then ", actions.OrderBy(a => a.Order ?? 0).Select(a => a.Type?.Value switch
        {
            "forward" when a.ForwardConfig?.TargetGroups is { Count: > 1 } groups =>
                "forward to " + string.Join(", ", groups.Select(g => $"{TargetGroupName(g.TargetGroupArn)} (weight {g.Weight ?? 1})")),
            "forward" => $"forward to {TargetGroupName(a.TargetGroupArn ?? a.ForwardConfig?.TargetGroups?.FirstOrDefault()?.TargetGroupArn)}",
            "redirect" when a.RedirectConfig is { } r =>
                $"redirect {r.StatusCode?.Value?.Replace("HTTP_", "")} to {r.Protocol}://{r.Host}:{r.Port}{r.Path}{(string.IsNullOrEmpty(r.Query) ? "" : "?" + r.Query)}",
            "fixed-response" when a.FixedResponseConfig is { } f => $"fixed response {f.StatusCode}{(string.IsNullOrEmpty(f.ContentType) ? "" : $" ({f.ContentType})")}",
            "authenticate-oidc" => "authenticate (OIDC)",
            "authenticate-cognito" => "authenticate (Cognito)",
            var type => type ?? "?",
        }));

    /// <summary>arn:aws:elasticloadbalancing:region:account:targetgroup/name/id → name.</summary>
    private static string TargetGroupName(string? arn) =>
        arn?.Split(':').LastOrDefault()?.Split('/') is [_, var name, ..] ? name : arn ?? "?";

    // ---------------- Network (VPC) ----------------

    public Task<NetworkSnapshot> GetNetworkAsync(Target target, CancellationToken ct) =>
        Call(target, "DescribeVpcs", async c =>
        {
            var snapshot = new NetworkSnapshot { TargetId = target.Id, DownloadedUtc = DateTime.UtcNow };
            var errors = new List<string>();

            async Task Part(string name, Func<Task> read)
            {
                try
                {
                    await read();
                }
                catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
                {
                    errors.Add($"{name}: {ex.ErrorCode}");
                }
            }

            await Part("VPCs", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeVpcsAsync(new Ec2.DescribeVpcsRequest { MaxResults = 1000, NextToken = token }, ct);
                    snapshot.Vpcs.AddRange((resp.Vpcs ?? []).Select(v => new VpcInfo
                    {
                        Id = v.VpcId ?? "",
                        Name = TagName(v.Tags),
                        Cidrs = (v.CidrBlockAssociationSet ?? []).Where(a => a.CidrBlockState?.State?.Value is null or "associated").Select(a => a.CidrBlock).Where(x => x is not null).ToList()!,
                        Ipv6Cidrs = (v.Ipv6CidrBlockAssociationSet ?? []).Select(a => a.Ipv6CidrBlock).Where(x => x is not null).ToList()!,
                        IsDefault = v.IsDefault == true,
                        State = v.State?.Value,
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("subnets", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeSubnetsAsync(new Ec2.DescribeSubnetsRequest { MaxResults = 1000, NextToken = token }, ct);
                    snapshot.Subnets.AddRange((resp.Subnets ?? []).Select(s => new SubnetInfo
                    {
                        Id = s.SubnetId ?? "",
                        Name = TagName(s.Tags),
                        VpcId = s.VpcId ?? "",
                        Cidr = s.CidrBlock ?? "",
                        Ipv6Cidrs = (s.Ipv6CidrBlockAssociationSet ?? []).Select(a => a.Ipv6CidrBlock).Where(x => x is not null).ToList()!,
                        AvailabilityZone = s.AvailabilityZone,
                        AvailableIps = s.AvailableIpAddressCount,
                        MapPublicIpOnLaunch = s.MapPublicIpOnLaunch == true,
                        DefaultForAz = s.DefaultForAz == true,
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("network interfaces", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeNetworkInterfacesAsync(new Ec2.DescribeNetworkInterfacesRequest { MaxResults = 1000, NextToken = token }, ct);
                    snapshot.Interfaces.AddRange((resp.NetworkInterfaces ?? []).Select(MapInterface));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("Elastic IPs", async () =>
            {
                var resp = await c.Ec2.DescribeAddressesAsync(new Ec2.DescribeAddressesRequest(), ct);
                snapshot.ElasticIps.AddRange((resp.Addresses ?? []).Select(a => new ElasticIpInfo
                {
                    PublicIp = a.PublicIp ?? "",
                    AllocationId = a.AllocationId,
                    InstanceId = string.IsNullOrEmpty(a.InstanceId) ? null : a.InstanceId,
                    NetworkInterfaceId = a.NetworkInterfaceId,
                    PrivateIp = a.PrivateIpAddress,
                    Name = TagName(a.Tags),
                }));
            });

            await Part("security groups", async () => snapshot.SecurityGroups = await ReadSecurityGroupsAsync(c, null, ct));

            await Part("route tables", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeRouteTablesAsync(new Ec2.DescribeRouteTablesRequest { MaxResults = 100, NextToken = token }, ct);
                    snapshot.RouteTables.AddRange((resp.RouteTables ?? []).Select(t => new RouteTableInfo
                    {
                        Id = t.RouteTableId ?? "",
                        Name = TagName(t.Tags),
                        VpcId = t.VpcId ?? "",
                        IsMain = (t.Associations ?? []).Any(a => a.Main == true),
                        SubnetIds = (t.Associations ?? []).Select(a => a.SubnetId).Where(x => !string.IsNullOrEmpty(x)).ToList()!,
                        Routes = (t.Routes ?? []).Select(r => new RouteInfo
                        {
                            Destination = r.DestinationCidrBlock ?? r.DestinationIpv6CidrBlock ?? r.DestinationPrefixListId ?? "?",
                            Target = new[] { r.GatewayId, r.NatGatewayId, r.TransitGatewayId, r.VpcPeeringConnectionId, r.EgressOnlyInternetGatewayId,
                                             r.NetworkInterfaceId, r.InstanceId, r.CarrierGatewayId, r.LocalGatewayId, r.CoreNetworkArn }
                                .FirstOrDefault(x => !string.IsNullOrEmpty(x)),
                            State = r.State?.Value,
                            Origin = r.Origin?.Value,
                        }).ToList(),
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("internet gateways", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeInternetGatewaysAsync(new Ec2.DescribeInternetGatewaysRequest { MaxResults = 1000, NextToken = token }, ct);
                    snapshot.InternetGateways.AddRange((resp.InternetGateways ?? []).Select(g => new InternetGatewayInfo
                    {
                        Id = g.InternetGatewayId ?? "",
                        Name = TagName(g.Tags),
                        VpcIds = (g.Attachments ?? []).Select(a => a.VpcId).Where(x => x is not null).ToList()!,
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
                token = null;
                pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeEgressOnlyInternetGatewaysAsync(new Ec2.DescribeEgressOnlyInternetGatewaysRequest { MaxResults = 255, NextToken = token }, ct);
                    snapshot.InternetGateways.AddRange((resp.EgressOnlyInternetGateways ?? []).Select(g => new InternetGatewayInfo
                    {
                        Id = g.EgressOnlyInternetGatewayId ?? "",
                        Name = TagName(g.Tags),
                        VpcIds = (g.Attachments ?? []).Select(a => a.VpcId).Where(x => x is not null).ToList()!,
                        EgressOnly = true,
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("NAT gateways", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeNatGatewaysAsync(new Ec2.DescribeNatGatewaysRequest { MaxResults = 1000, NextToken = token }, ct);
                    snapshot.NatGateways.AddRange((resp.NatGateways ?? []).Where(n => n.State?.Value != "deleted").Select(n => new NatGatewayInfo
                    {
                        Id = n.NatGatewayId ?? "",
                        Name = TagName(n.Tags),
                        VpcId = n.VpcId,
                        SubnetId = n.SubnetId,
                        State = n.State?.Value ?? "",
                        ConnectivityType = n.ConnectivityType?.Value ?? "public",
                        PublicIps = (n.NatGatewayAddresses ?? []).Select(a => a.PublicIp).Where(x => !string.IsNullOrEmpty(x)).ToList()!,
                        PrivateIps = (n.NatGatewayAddresses ?? []).Select(a => a.PrivateIp).Where(x => !string.IsNullOrEmpty(x)).ToList()!,
                        FailureMessage = n.FailureMessage,
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("VPC endpoints", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeVpcEndpointsAsync(new Ec2.DescribeVpcEndpointsRequest { MaxResults = 1000, NextToken = token }, ct);
                    snapshot.Endpoints.AddRange((resp.VpcEndpoints ?? []).Select(e => new VpcEndpointInfo
                    {
                        Id = e.VpcEndpointId ?? "",
                        Name = TagName(e.Tags),
                        VpcId = e.VpcId ?? "",
                        ServiceName = e.ServiceName ?? "",
                        Type = e.VpcEndpointType?.Value ?? "",
                        State = e.State?.Value,
                        RouteTableIds = e.RouteTableIds ?? [],
                        SubnetIds = e.SubnetIds ?? [],
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("peering connections", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeVpcPeeringConnectionsAsync(new Ec2.DescribeVpcPeeringConnectionsRequest { MaxResults = 1000, NextToken = token }, ct);
                    snapshot.Peerings.AddRange((resp.VpcPeeringConnections ?? []).Where(p => p.Status?.Code?.Value is not ("deleted" or "rejected" or "expired")).Select(p => new PeeringInfo
                    {
                        Id = p.VpcPeeringConnectionId ?? "",
                        Name = TagName(p.Tags),
                        RequesterVpcId = p.RequesterVpcInfo?.VpcId,
                        RequesterOwner = p.RequesterVpcInfo?.OwnerId,
                        RequesterRegion = p.RequesterVpcInfo?.Region,
                        RequesterCidr = p.RequesterVpcInfo?.CidrBlock,
                        AccepterVpcId = p.AccepterVpcInfo?.VpcId,
                        AccepterOwner = p.AccepterVpcInfo?.OwnerId,
                        AccepterRegion = p.AccepterVpcInfo?.Region,
                        AccepterCidr = p.AccepterVpcInfo?.CidrBlock,
                        Status = p.Status?.Code?.Value,
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            await Part("network ACLs", async () =>
            {
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.Ec2.DescribeNetworkAclsAsync(new Ec2.DescribeNetworkAclsRequest { MaxResults = 100, NextToken = token }, ct);
                    snapshot.NetworkAcls.AddRange((resp.NetworkAcls ?? []).Select(a => new NetworkAclInfo
                    {
                        Id = a.NetworkAclId ?? "",
                        Name = TagName(a.Tags),
                        VpcId = a.VpcId ?? "",
                        IsDefault = a.IsDefault == true,
                        SubnetIds = (a.Associations ?? []).Select(x => x.SubnetId).Where(x => x is not null).ToList()!,
                        Entries = (a.Entries ?? []).Select(e => new NetworkAclEntryInfo
                        {
                            RuleNumber = e.RuleNumber ?? 0,
                            Egress = e.Egress == true,
                            Protocol = e.Protocol ?? "-1",
                            FromPort = e.PortRange?.From,
                            ToPort = e.PortRange?.To,
                            Cidr = e.CidrBlock ?? e.Ipv6CidrBlock,
                            Allow = e.RuleAction?.Value == "allow",
                        }).OrderBy(e => e.RuleNumber).ToList(),
                    }));
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
            });

            // Prefix lists that rules and routes use: their CIDRs decide what those rules allow.
            var usedLists = snapshot.SecurityGroups.SelectMany(g => g.Rules).Select(r => r.PrefixListId)
                .Concat(snapshot.RouteTables.SelectMany(t => t.Routes).Select(r => r.Destination.StartsWith("pl-", StringComparison.Ordinal) ? r.Destination : null))
                .Where(id => id is not null).Distinct().Take(50).ToList();
            if (usedLists.Count > 0)
                await Part("prefix lists", async () =>
                {
                    var names = new Dictionary<string, string?>();
                    var resp = await c.Ec2.DescribeManagedPrefixListsAsync(new Ec2.DescribeManagedPrefixListsRequest { PrefixListIds = usedLists! }, ct);
                    foreach (var pl in resp.PrefixLists ?? [])
                        if (pl.PrefixListId is not null)
                            names[pl.PrefixListId] = pl.PrefixListName;
                    foreach (var id in usedLists)
                    {
                        var cidrs = new List<string>();
                        string? token = null;
                        var pages = 0;
                        do
                        {
                            var entries = await c.Ec2.GetManagedPrefixListEntriesAsync(new Ec2.GetManagedPrefixListEntriesRequest { PrefixListId = id, MaxResults = 100, NextToken = token }, ct);
                            cidrs.AddRange((entries.Entries ?? []).Select(e => e.Cidr).Where(x => x is not null)!);
                            token = entries.NextToken;
                        } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
                        snapshot.PrefixLists.Add(new PrefixListInfo { Id = id!, Name = names.GetValueOrDefault(id!), Cidrs = cidrs });
                    }
                });

            snapshot.Error = errors.Count > 0 ? $"Could not read {string.Join(", ", errors)}" : null;
            return snapshot;
        });

    public Task<IReadOnlyList<SecurityGroupInfo>> GetSecurityGroupsAsync(Target target, IReadOnlyList<string> groupIds, CancellationToken ct) =>
        Call<IReadOnlyList<SecurityGroupInfo>>(target, "DescribeSecurityGroups", async c => await ReadSecurityGroupsAsync(c, groupIds, ct));

    /// <summary>Groups and their rules (the rules call returns the rule ids that edits need).</summary>
    private static async Task<List<SecurityGroupInfo>> ReadSecurityGroupsAsync(AwsClientSet c, IReadOnlyList<string>? groupIds, CancellationToken ct)
    {
        var groups = new List<Ec2.SecurityGroup>();
        string? token = null;
        var pages = 0;
        do
        {
            var resp = await c.Ec2.DescribeSecurityGroupsAsync(new Ec2.DescribeSecurityGroupsRequest
            {
                GroupIds = groupIds?.ToList(),
                MaxResults = groupIds is null ? 1000 : null,
                NextToken = token,
            }, ct);
            groups.AddRange(resp.SecurityGroups ?? []);
            token = resp.NextToken;
        } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);

        var rules = new List<Ec2.SecurityGroupRule>();
        token = null;
        pages = 0;
        do
        {
            var resp = await c.Ec2.DescribeSecurityGroupRulesAsync(new Ec2.DescribeSecurityGroupRulesRequest
            {
                Filters = groupIds is null ? null : [new Ec2.Filter { Name = "group-id", Values = groupIds.ToList() }],
                MaxResults = 1000,
                NextToken = token,
            }, ct);
            rules.AddRange(resp.SecurityGroupRules ?? []);
            token = resp.NextToken;
        } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);

        var byGroup = rules.Where(r => r.GroupId is not null).ToLookup(r => r.GroupId!);
        return groups.Select(g => new SecurityGroupInfo
        {
            Id = g.GroupId ?? "",
            Name = g.GroupName ?? "",
            Description = g.Description,
            VpcId = g.VpcId,
            OwnerId = g.OwnerId,
            NameTag = TagName(g.Tags),
            Rules = byGroup[g.GroupId ?? ""].Select(MapRule).ToList(),
        }).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SecurityGroupRuleInfo MapRule(Ec2.SecurityGroupRule r) => new()
    {
        RuleId = r.SecurityGroupRuleId ?? "",
        GroupId = r.GroupId ?? "",
        IsEgress = r.IsEgress == true,
        Protocol = r.IpProtocol ?? "-1",
        FromPort = r.FromPort,
        ToPort = r.ToPort,
        CidrIpv4 = r.CidrIpv4,
        CidrIpv6 = r.CidrIpv6,
        PrefixListId = r.PrefixListId,
        ReferencedGroupId = r.ReferencedGroupInfo?.GroupId,
        ReferencedGroupUserId = r.ReferencedGroupInfo?.UserId is { } user && user != r.GroupOwnerId ? user : null,
        Description = string.IsNullOrEmpty(r.Description) ? null : r.Description,
    };

    private static NetworkInterfaceInfo MapInterface(Ec2.NetworkInterface n) => new()
    {
        Id = n.NetworkInterfaceId ?? "",
        VpcId = n.VpcId,
        SubnetId = n.SubnetId,
        AvailabilityZone = n.AvailabilityZone,
        PrivateIp = n.PrivateIpAddress,
        PrivateIps = (n.PrivateIpAddresses ?? []).Select(p => p.PrivateIpAddress).Where(p => p is not null).ToList()!,
        PublicIps = (n.PrivateIpAddresses ?? []).Select(p => p.Association?.PublicIp).Where(p => p is not null).Distinct().ToList()!,
        Ipv6 = (n.Ipv6Addresses ?? []).Select(a => a.Ipv6Address).Where(a => a is not null).ToList()!,
        InterfaceType = n.InterfaceType?.Value,
        Description = string.IsNullOrEmpty(n.Description) ? null : n.Description,
        Status = n.Status?.Value,
        InstanceId = string.IsNullOrEmpty(n.Attachment?.InstanceId) ? null : n.Attachment.InstanceId,
        RequesterId = n.RequesterId,
        RequesterManaged = n.RequesterManaged == true,
        Name = TagName(n.TagSet),
        SecurityGroups = (n.Groups ?? []).Select(g => new SecurityGroupRef(g.GroupId ?? "", g.GroupName ?? "")).ToList(),
    };

    private static string? TagName(List<Ec2.Tag>? tags) => tags?.FirstOrDefault(t => t.Key == "Name")?.Value is { Length: > 0 } name ? name : null;

    // ---------------- EC2 actions (elevated key + per-call approval) ----------------

    public Task StartEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct) =>
        Call(target, "ec2:StartInstances", async c =>
        {
            await EnsureInstanceStateAsync(c, instance.InstanceId, ["stopped"], ct);
            await c.Ec2.StartInstancesAsync(new Ec2.StartInstancesRequest { InstanceIds = [instance.InstanceId] }, ct);
            return true;
        }, elevated: true, InstanceLabel(instance),
        confirmation: $"Starts EC2 instance {InstanceLabel(instance)}. It is billed again while it runs"
                      + (instance.HasElasticIp || instance.PublicIp is null ? "." : ", and it gets a new public IP address (it has no Elastic IP)."));

    public Task StopEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct) =>
        Call(target, "ec2:StopInstances", async c =>
        {
            await EnsureInstanceStateAsync(c, instance.InstanceId, ["running", "pending"], ct);
            // A normal shutdown only: the guard rejects Force, Hibernate and anything else.
            await c.Ec2.StopInstancesAsync(new Ec2.StopInstancesRequest { InstanceIds = [instance.InstanceId] }, ct);
            return true;
        }, elevated: true, InstanceLabel(instance),
        confirmation: StopExplanation(instance), confirmPhrase: instance.InstanceId);

    public Task RebootEc2InstanceAsync(Target target, Ec2InstanceSnapshot instance, CancellationToken ct) =>
        Call(target, "ec2:RebootInstances", async c =>
        {
            await EnsureInstanceStateAsync(c, instance.InstanceId, ["running"], ct);
            await c.Ec2.RebootInstancesAsync(new Ec2.RebootInstancesRequest { InstanceIds = [instance.InstanceId] }, ct);
            return true;
        }, elevated: true, InstanceLabel(instance),
        confirmation: $"Reboots EC2 instance {InstanceLabel(instance)}. It is unavailable for a few minutes while the operating system restarts; its disks and addresses stay the same.");

    private static string InstanceLabel(Ec2InstanceSnapshot i) => i.Name is { Length: > 0 } n ? $"{i.InstanceId} ({n})" : i.InstanceId;

    public static string StopExplanation(Ec2InstanceSnapshot i)
    {
        var lines = new List<string> { $"Stops EC2 instance {InstanceLabel(i)} (a normal operating-system shutdown). It stays stopped until someone starts it." };
        if (i.RootDeviceType == "instance-store")
            lines.Add("It boots from an instance store volume; AWS does not allow stopping it.");
        lines.Add("Data on instance store (ephemeral) volumes is lost; EBS volumes are kept.");
        if (i.PublicIp is not null && !i.HasElasticIp)
            lines.Add($"Its public IP {i.PublicIp} is released; it gets a different one when started again.");
        if (i.AutoScalingGroup is { } asg)
            lines.Add($"It belongs to Auto Scaling group {asg}, which may mark it unhealthy and launch a replacement.");
        if (i.EbEnvironment is { } eb)
            lines.Add($"Elastic Beanstalk environment {eb} manages it and may replace it.");
        if (i.Lifecycle == "spot")
            lines.Add("It is a spot instance; a stopped spot instance may not get capacity again right away.");
        return string.Join(" ", lines);
    }

    /// <summary>Checks right before acting that the instance still exists and is in a state the action applies to.</summary>
    private static async Task EnsureInstanceStateAsync(AwsClientSet c, string instanceId, IReadOnlyList<string> states, CancellationToken ct)
    {
        var resp = await c.Ec2.DescribeInstancesAsync(new Ec2.DescribeInstancesRequest { InstanceIds = [instanceId] }, ct);
        var state = (resp.Reservations ?? []).SelectMany(r => r.Instances ?? []).FirstOrDefault(i => i.InstanceId == instanceId)?.State?.Name?.Value;
        if (state is null)
            throw new InvalidOperationException($"{instanceId} no longer exists; nothing was changed.");
        if (!states.Contains(state))
            throw new InvalidOperationException($"{instanceId} is {state} now; nothing was changed. Refresh and try again.");
    }

    // ---------------- Security group rules (elevated key + per-call approval) ----------------

    public Task AddSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleSpec rule, CancellationToken ct)
    {
        if (NetworkRules.Validate(rule) is { } problem)
            throw new ArgumentException(problem);
        var permission = ToPermission(rule);
        return Call(target, rule.IsEgress ? "ec2:AuthorizeSecurityGroupEgress" : "ec2:AuthorizeSecurityGroupIngress", async c =>
        {
            if (rule.IsEgress)
                await c.Ec2.AuthorizeSecurityGroupEgressAsync(new Ec2.AuthorizeSecurityGroupEgressRequest { GroupId = group.Id, IpPermissions = [permission] }, ct);
            else
                await c.Ec2.AuthorizeSecurityGroupIngressAsync(new Ec2.AuthorizeSecurityGroupIngressRequest { GroupId = group.Id, IpPermissions = [permission] }, ct);
            return true;
        }, elevated: true, GroupLabel(group),
        confirmation: $"Adds a rule to security group {GroupLabel(group)}: {rule.Summary}{DescriptionText(rule.Description)}. "
                      + "It applies immediately to every resource that uses this group." + WorldWarning(rule),
        confirmPhrase: rule.IsOpenToWorld ? group.Id : null);
    }

    public Task UpdateSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleInfo current, SecurityGroupRuleSpec updated, CancellationToken ct)
    {
        if (NetworkRules.Validate(updated) is { } problem)
            throw new ArgumentException(problem);
        if (updated.IsEgress != current.IsEgress)
            throw new ArgumentException("A rule cannot change direction; add a new rule and delete this one instead.");
        var request = new Ec2.SecurityGroupRuleRequest
        {
            IpProtocol = updated.Protocol,
            FromPort = PortsFor(updated).From,
            ToPort = PortsFor(updated).To,
            CidrIpv4 = updated.SourceKind == RuleSourceKind.Ipv4 ? updated.Source.Trim() : null,
            CidrIpv6 = updated.SourceKind == RuleSourceKind.Ipv6 ? updated.Source.Trim() : null,
            PrefixListId = updated.SourceKind == RuleSourceKind.PrefixList ? updated.Source.Trim() : null,
            ReferencedGroupId = updated.SourceKind == RuleSourceKind.SecurityGroup ? updated.Source.Trim() : null,
            Description = string.IsNullOrWhiteSpace(updated.Description) ? null : updated.Description.Trim(),
        };
        return Call(target, "ec2:ModifySecurityGroupRules", async c =>
        {
            await EnsureRuleUnchangedAsync(c, current, ct);
            await c.Ec2.ModifySecurityGroupRulesAsync(new Ec2.ModifySecurityGroupRulesRequest
            {
                GroupId = group.Id,
                SecurityGroupRules = [new Ec2.SecurityGroupRuleUpdate { SecurityGroupRuleId = current.RuleId, SecurityGroupRule = request }],
            }, ct);
            return true;
        }, elevated: true, $"{current.RuleId} in {GroupLabel(group)}",
        confirmation: $"Changes rule {current.RuleId} of security group {GroupLabel(group)} from \"{current.Summary}{DescriptionText(current.Description)}\" "
                      + $"to \"{updated.Summary}{DescriptionText(updated.Description)}\". It applies immediately to every resource that uses this group." + WorldWarning(updated),
        confirmPhrase: updated.IsOpenToWorld && !current.IsOpenToWorld ? group.Id : null);
    }

    public Task DeleteSecurityGroupRuleAsync(Target target, SecurityGroupInfo group, SecurityGroupRuleInfo rule, CancellationToken ct) =>
        Call(target, rule.IsEgress ? "ec2:RevokeSecurityGroupEgress" : "ec2:RevokeSecurityGroupIngress", async c =>
        {
            await EnsureRuleUnchangedAsync(c, rule, ct);
            if (rule.IsEgress)
                await c.Ec2.RevokeSecurityGroupEgressAsync(new Ec2.RevokeSecurityGroupEgressRequest { GroupId = group.Id, SecurityGroupRuleIds = [rule.RuleId] }, ct);
            else
                await c.Ec2.RevokeSecurityGroupIngressAsync(new Ec2.RevokeSecurityGroupIngressRequest { GroupId = group.Id, SecurityGroupRuleIds = [rule.RuleId] }, ct);
            return true;
        }, elevated: true, $"{rule.RuleId} in {GroupLabel(group)}",
        confirmation: $"Deletes rule {rule.RuleId} from security group {GroupLabel(group)}: {rule.Summary}{DescriptionText(rule.Description)}. "
                      + "Traffic that relies on it is blocked immediately for every resource that uses this group (connections already open may continue).",
        confirmPhrase: group.Id);

    private static string GroupLabel(SecurityGroupInfo g) => $"{g.Id} ({g.Name})";

    private static string DescriptionText(string? description) => string.IsNullOrWhiteSpace(description) ? "" : $" (\"{description.Trim()}\")";

    private static string WorldWarning(SecurityGroupRuleSpec rule) => !rule.IsOpenToWorld ? ""
        : rule.IsEgress ? " The destination is the whole internet."
        : " ⚠ This opens the port(s) to the WHOLE INTERNET. Anyone can connect; prefer a specific address range.";

    private static (int? From, int? To) PortsFor(SecurityGroupRuleSpec rule) => rule.Protocol switch
    {
        "-1" => (null, null),
        "icmp" or "icmpv6" or "1" or "58" => (rule.FromPort ?? -1, rule.ToPort ?? -1),
        _ => (rule.FromPort, rule.ToPort),
    };

    private static Ec2.IpPermission ToPermission(SecurityGroupRuleSpec rule)
    {
        var (from, to) = PortsFor(rule);
        var source = rule.Source.Trim();
        var description = string.IsNullOrWhiteSpace(rule.Description) ? null : rule.Description.Trim();
        var permission = new Ec2.IpPermission { IpProtocol = rule.Protocol, FromPort = from, ToPort = to };
        switch (rule.SourceKind)
        {
            case RuleSourceKind.Ipv4: permission.Ipv4Ranges = [new Ec2.IpRange { CidrIp = source, Description = description }]; break;
            case RuleSourceKind.Ipv6: permission.Ipv6Ranges = [new Ec2.Ipv6Range { CidrIpv6 = source, Description = description }]; break;
            case RuleSourceKind.PrefixList: permission.PrefixListIds = [new Ec2.PrefixListId { Id = source, Description = description }]; break;
            case RuleSourceKind.SecurityGroup: permission.UserIdGroupPairs = [new Ec2.UserIdGroupPair { GroupId = source, Description = description }]; break;
        }
        return permission;
    }

    /// <summary>Re-reads the rule right before changing it: it must still exist and match what the user saw.</summary>
    private static async Task EnsureRuleUnchangedAsync(AwsClientSet c, SecurityGroupRuleInfo expected, CancellationToken ct)
    {
        var resp = await c.Ec2.DescribeSecurityGroupRulesAsync(new Ec2.DescribeSecurityGroupRulesRequest { SecurityGroupRuleIds = [expected.RuleId] }, ct);
        if ((resp.SecurityGroupRules ?? []).FirstOrDefault() is not { } rule)
            throw new InvalidOperationException($"Rule {expected.RuleId} no longer exists; nothing was changed. Refresh the group.");
        var actual = MapRule(rule);
        if (actual.Summary != expected.Summary || actual.Description != expected.Description)
            throw new InvalidOperationException($"Rule {expected.RuleId} was changed since it was downloaded (now: {actual.Summary}); nothing was changed. Refresh the group and try again.");
    }
}
