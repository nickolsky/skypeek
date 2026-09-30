using Amazon.Runtime;
using Skypeek.Core.Models;
using Cb = Amazon.CodeBuild.Model;
using Cfn = Amazon.CloudFormation.Model;
using Ec2 = Amazon.EC2.Model;
using Rs = Amazon.Redshift.Model;
using Rss = Amazon.RedshiftServerless.Model;

namespace Skypeek.Aws;

/// <summary>Site-to-Site VPN, CodeBuild, CloudFormation and Redshift (all reads).</summary>
public sealed partial class AwsGateway
{
    // ---------------- Site-to-Site VPN ----------------

    public Task<IReadOnlyList<VpnConnectionSnapshot>> GetVpnConnectionsAsync(Target target, CancellationToken ct, string? onlyId = null) =>
        Call<IReadOnlyList<VpnConnectionSnapshot>>(target, "DescribeVpnConnections", async c =>
        {
            var resp = await c.Ec2.DescribeVpnConnectionsAsync(new Ec2.DescribeVpnConnectionsRequest
            {
                VpnConnectionIds = onlyId is null ? null : [onlyId],
            }, ct);
            var vpns = (resp.VpnConnections ?? []).Where(v => v.State?.Value != "deleted").ToList();
            if (vpns.Count == 0)
                return [];

            var gateways = new Dictionary<string, Ec2.CustomerGateway>();
            try
            {
                var ids = vpns.Select(v => v.CustomerGatewayId).Where(id => id is not null).Distinct().ToList();
                var cgw = await c.Ec2.DescribeCustomerGatewaysAsync(new Ec2.DescribeCustomerGatewaysRequest { CustomerGatewayIds = ids! }, ct);
                foreach (var g in cgw.CustomerGateways ?? [])
                    if (g.CustomerGatewayId is not null)
                        gateways[g.CustomerGatewayId] = g;
            }
            catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
            {
                // The customer gateway's address is extra detail.
            }

            return vpns.Select(v =>
            {
                var cgw = v.CustomerGatewayId is { } id ? gateways.GetValueOrDefault(id) : null;
                return new VpnConnectionSnapshot
                {
                    Id = v.VpnConnectionId ?? "",
                    Name = NameTag(v.Tags),
                    State = v.State?.Value ?? "",
                    Type = v.Type?.Value,
                    CustomerGatewayId = v.CustomerGatewayId,
                    CustomerGatewayIp = cgw?.IpAddress,
                    CustomerGatewayName = cgw is null ? null : NameTag(cgw.Tags) ?? cgw.DeviceName,
                    VpnGatewayId = v.VpnGatewayId,
                    TransitGatewayId = v.TransitGatewayId,
                    StaticRoutesOnly = v.Options?.StaticRoutesOnly == true,
                    StaticRoutes = (v.Routes ?? []).Select(r => r.DestinationCidrBlock).Where(r => r is not null).ToList()!,
                    Tunnels = (v.VgwTelemetry ?? []).Select(t => new VpnTunnelInfo
                    {
                        OutsideIp = t.OutsideIpAddress ?? "",
                        Status = t.Status?.Value ?? "",
                        StatusMessage = t.StatusMessage,
                        LastChange = t.LastStatusChange,
                        AcceptedRoutes = t.AcceptedRouteCount ?? 0,
                    }).OrderBy(t => t.OutsideIp).ToList(),
                };
            }).OrderBy(v => v.Title, StringComparer.OrdinalIgnoreCase).ToList();
        });

    private static string? NameTag(List<Ec2.Tag>? tags) => tags?.FirstOrDefault(t => t.Key == "Name")?.Value is { Length: > 0 } n ? n : null;

    // ---------------- CodeBuild ----------------

    private const int MaxRecentBuildPages = 5;
    private const int MaxProjectLookupsPerPoll = 20;

    /// <summary>
    /// Every project with its newest build, cheaply: one list of projects, one list of the account's newest builds and
    /// one batch read. Projects without a build in that list keep <paramref name="knownLatest"/> (their newest build id
    /// from the previous poll); only projects never seen before are looked up one by one.
    /// </summary>
    public Task<IReadOnlyList<CodeBuildProjectSnapshot>> GetCodeBuildProjectsAsync(Target target, IReadOnlyDictionary<string, string> knownLatest,
        CancellationToken ct, string? onlyProject = null) =>
        Call<IReadOnlyList<CodeBuildProjectSnapshot>>(target, "ListProjects", async c =>
        {
            List<string> projects;
            var idsByProject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (onlyProject is not null)
            {
                projects = [onlyProject];
                var resp = await c.CodeBuild.ListBuildsForProjectAsync(new Cb.ListBuildsForProjectRequest
                {
                    ProjectName = onlyProject,
                    SortOrder = Amazon.CodeBuild.SortOrderType.DESCENDING,
                }, ct);
                idsByProject[onlyProject] = (resp.Ids ?? []).Take(2).ToList();
            }
            else
            {
                projects = [];
                string? token = null;
                var pages = 0;
                do
                {
                    var resp = await c.CodeBuild.ListProjectsAsync(new Cb.ListProjectsRequest { NextToken = token }, ct);
                    projects.AddRange(resp.Projects ?? []);
                    token = resp.NextToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
                if (projects.Count == 0)
                    return [];

                // The account's newest builds, newest first: one page covers what happened since the last poll; the
                // first poll reads a few more pages so most projects are known without asking one by one.
                var unknown = projects.Where(p => !knownLatest.ContainsKey(p)).ToHashSet();
                token = null;
                pages = 0;
                do
                {
                    var recent = await c.CodeBuild.ListBuildsAsync(new Cb.ListBuildsRequest { SortOrder = Amazon.CodeBuild.SortOrderType.DESCENDING, NextToken = token }, ct);
                    foreach (var id in recent.Ids ?? [])
                    {
                        var project = id.Split(':')[0];
                        if (!idsByProject.TryGetValue(project, out var list))
                            idsByProject[project] = list = [];
                        if (list.Count < 2)
                            list.Add(id);
                        unknown.Remove(project);
                    }
                    token = recent.NextToken;
                } while (!string.IsNullOrEmpty(token) && unknown.Count > 0 && ++pages < MaxRecentBuildPages);

                foreach (var p in projects.Where(p => !idsByProject.ContainsKey(p) && knownLatest.TryGetValue(p, out var k)))
                    idsByProject[p] = knownLatest[p] is { Length: > 0 } id ? [id] : [];

                // The rest one by one, slowly: CodeBuild's list calls have a low rate limit. Whatever is left is read
                // on the next polls.
                var lookups = 0;
                foreach (var p in projects.Where(p => !idsByProject.ContainsKey(p)))
                {
                    if (lookups++ >= MaxProjectLookupsPerPoll)
                        break;
                    try
                    {
                        var resp = await c.CodeBuild.ListBuildsForProjectAsync(new Cb.ListBuildsForProjectRequest
                        {
                            ProjectName = p,
                            SortOrder = Amazon.CodeBuild.SortOrderType.DESCENDING,
                        }, ct);
                        idsByProject[p] = (resp.Ids ?? []).Take(2).ToList();
                    }
                    catch (AmazonServiceException ex) when (ex.ErrorCode is "ThrottlingException" or "Throttling" || ex.Message.Contains("Rate exceeded", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }
            }

            var builds = await BatchGetBuildsAsync(c, idsByProject.Values.SelectMany(v => v).Distinct().ToList(), ct);
            return projects.Select(p =>
            {
                var runs = (idsByProject.GetValueOrDefault(p) ?? []).Select(id => builds.GetValueOrDefault(id)).Where(b => b is not null).ToList();
                return new CodeBuildProjectSnapshot
                {
                    Name = p,
                    LatestBuild = runs.FirstOrDefault(),
                    LastCompleted = runs.FirstOrDefault(r => r!.IsComplete),
                    NotReadYet = !idsByProject.ContainsKey(p),
                };
            }).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });

    public Task<IReadOnlyList<CodeBuildRun>> GetCodeBuildHistoryAsync(Target target, string project, int max, CancellationToken ct) =>
        Call<IReadOnlyList<CodeBuildRun>>(target, "ListBuildsForProject", async c =>
        {
            var resp = await c.CodeBuild.ListBuildsForProjectAsync(new Cb.ListBuildsForProjectRequest
            {
                ProjectName = project,
                SortOrder = Amazon.CodeBuild.SortOrderType.DESCENDING,
            }, ct);
            var ids = (resp.Ids ?? []).Take(max).ToList();
            var builds = await BatchGetBuildsAsync(c, ids, ct);
            return ids.Select(id => builds.GetValueOrDefault(id)).Where(b => b is not null).ToList()!;
        });

    private static async Task<Dictionary<string, CodeBuildRun>> BatchGetBuildsAsync(AwsClientSet c, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var result = new Dictionary<string, CodeBuildRun>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(100))
        {
            var resp = await c.CodeBuild.BatchGetBuildsAsync(new Cb.BatchGetBuildsRequest { Ids = chunk.ToList() }, ct);
            foreach (var b in resp.Builds ?? [])
                if (b.Id is not null)
                    result[b.Id] = MapBuild(b);
        }
        return result;
    }

    private static CodeBuildRun MapBuild(Cb.Build b) => new()
    {
        Id = b.Id ?? "",
        Arn = b.Arn,
        Number = b.BuildNumber,
        Status = b.BuildStatus?.Value ?? "",
        Started = b.StartTime,
        Ended = b.EndTime,
        Initiator = b.Initiator,
        SourceVersion = b.SourceVersion,
        ResolvedSourceVersion = b.ResolvedSourceVersion,
        CurrentPhase = b.CurrentPhase,
        LogGroup = b.Logs?.GroupName,
        LogStream = b.Logs?.StreamName,
        Phases = (b.Phases ?? []).Select(p => new CodeBuildPhase(
            p.PhaseType?.Value ?? "",
            p.PhaseStatus?.Value,
            p.DurationInSeconds,
            string.Join("; ", (p.Contexts ?? []).Select(x => x.Message).Where(m => !string.IsNullOrWhiteSpace(m))) is { Length: > 0 } m ? Shorten(m, 400) : null)).ToList(),
    };

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ---------------- CloudFormation ----------------

    public Task<IReadOnlyList<StackSnapshot>> GetStacksAsync(Target target, CancellationToken ct, string? onlyStack = null) =>
        Call<IReadOnlyList<StackSnapshot>>(target, "DescribeStacks", async c =>
        {
            var stacks = new List<Cfn.Stack>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.CloudFormation.DescribeStacksAsync(new Cfn.DescribeStacksRequest { StackName = onlyStack, NextToken = token }, ct);
                stacks.AddRange(resp.Stacks ?? []);
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);

            return stacks.Where(s => s.StackStatus?.Value != "DELETE_COMPLETE").Select(s => new StackSnapshot
            {
                Name = s.StackName ?? "",
                Id = s.StackId ?? "",
                Status = s.StackStatus?.Value ?? "",
                StatusReason = s.StackStatusReason,
                Description = s.Description,
                Created = s.CreationTime,
                LastUpdated = s.LastUpdatedTime,
                ParentId = s.ParentId,
                RootId = s.RootId,
                DriftStatus = s.DriftInformation?.StackDriftStatus?.Value,
                DriftChecked = s.DriftInformation?.LastCheckTimestamp,
                TerminationProtection = s.EnableTerminationProtection == true,
                Outputs = (s.Outputs ?? []).Select(o => new StackOutput(o.OutputKey ?? "", o.OutputValue, o.Description, o.ExportName)).ToList(),
                Tags = (s.Tags ?? []).Where(t => t.Key is not null).GroupBy(t => t.Key!).ToDictionary(g => g.Key, g => g.First().Value ?? ""),
            }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        });

    /// <summary>The newest events of a stack (newest first), up to <paramref name="max"/>.</summary>
    public Task<IReadOnlyList<StackEventInfo>> GetStackEventsAsync(Target target, string stack, int max, CancellationToken ct) =>
        Call<IReadOnlyList<StackEventInfo>>(target, "DescribeStackEvents", async c =>
        {
            var events = new List<StackEventInfo>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.CloudFormation.DescribeStackEventsAsync(new Cfn.DescribeStackEventsRequest { StackName = stack, NextToken = token }, ct);
                events.AddRange((resp.StackEvents ?? []).Select(e => new StackEventInfo(
                    e.Timestamp ?? DateTime.MinValue, e.LogicalResourceId ?? "", e.ResourceType, e.ResourceStatus?.Value ?? "",
                    e.ResourceStatusReason, e.PhysicalResourceId)));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && events.Count < max && ++pages < MaxPages);
            return events.Take(max).ToList();
        });

    // ---------------- Reachability Analyzer ----------------

    private static readonly TimeSpan InsightsTimeout = TimeSpan.FromMinutes(3);

    public Task<AwsReachResult> VerifyReachAsync(Target target, string sourceId, string? destinationId, string? destinationIp, string protocol, int? port, CancellationToken ct) =>
        Call(target, "ec2:StartNetworkInsightsAnalysis", async c =>
        {
            string? pathId = null;
            string? analysisId = null;
            try
            {
                var path = await c.Ec2.CreateNetworkInsightsPathAsync(new Ec2.CreateNetworkInsightsPathRequest
                {
                    Source = sourceId,
                    Destination = destinationId,
                    DestinationIp = destinationId is null ? destinationIp : null,
                    Protocol = protocol == "udp" ? Amazon.EC2.Protocol.Udp : Amazon.EC2.Protocol.Tcp,
                    DestinationPort = port,
                }, ct);
                pathId = path.NetworkInsightsPath?.NetworkInsightsPathId;
                var started = await c.Ec2.StartNetworkInsightsAnalysisAsync(new Ec2.StartNetworkInsightsAnalysisRequest { NetworkInsightsPathId = pathId }, ct);
                analysisId = started.NetworkInsightsAnalysis?.NetworkInsightsAnalysisId;

                var deadline = DateTime.UtcNow + InsightsTimeout;
                Ec2.NetworkInsightsAnalysis? analysis = started.NetworkInsightsAnalysis;
                while (analysis?.Status?.Value == "running" && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    var resp = await c.Ec2.DescribeNetworkInsightsAnalysesAsync(new Ec2.DescribeNetworkInsightsAnalysesRequest { NetworkInsightsAnalysisIds = [analysisId!] }, ct);
                    analysis = resp.NetworkInsightsAnalyses?.FirstOrDefault();
                }
                var explanations = (analysis?.Explanations ?? []).Select(e => string.Join(" ", new[]
                {
                    e.ExplanationCode,
                    e.Direction is { } d ? $"({d})" : null,
                    e.Component?.Id ?? e.SecurityGroup?.Id ?? e.Acl?.Id ?? e.RouteTable?.Id ?? e.Subnet?.Id,
                    e.Component?.Name,
                }.Where(x => !string.IsNullOrEmpty(x)))).Distinct().Take(8).ToList();
                var hops = (analysis?.ForwardPathComponents ?? []).OrderBy(p => p.SequenceNumber)
                    .Select(p => p.Component?.Id).Where(id => id is not null).Take(20).ToList()!;
                return new AwsReachResult(analysis?.NetworkPathFound, analysis?.Status?.Value ?? "unknown",
                    analysis?.Status?.Value == "running" ? ["timed out after 3 minutes; the analysis is deleted without a result"] : explanations, hops!);
            }
            finally
            {
                // Nothing is left behind, whatever happened.
                if (analysisId is not null)
                    try { await c.Ec2.DeleteNetworkInsightsAnalysisAsync(new Ec2.DeleteNetworkInsightsAnalysisRequest { NetworkInsightsAnalysisId = analysisId }, CancellationToken.None); }
                    catch (AmazonServiceException) { /* shown as left over in the console at worst */ }
                if (pathId is not null)
                    try { await c.Ec2.DeleteNetworkInsightsPathAsync(new Ec2.DeleteNetworkInsightsPathRequest { NetworkInsightsPathId = pathId }, CancellationToken.None); }
                    catch (AmazonServiceException) { }
            }
        }, elevated: true, $"{sourceId} → {destinationId ?? destinationIp} {protocol}/{port}",
        confirmation: $"Run AWS Reachability Analyzer once from {sourceId} to {destinationId ?? destinationIp} ({protocol} port {port}).\n\n"
                      + "AWS charges $0.10 for the analysis. Skypeek creates a network insights path and an analysis, waits for the answer "
                      + "(up to 3 minutes) and deletes both again; nothing else changes.");

    // ---------------- Redshift ----------------

    public Task<IReadOnlyList<RedshiftSnapshot>> GetRedshiftAsync(Target target, CancellationToken ct, RedshiftSnapshot? only = null) =>
        Call<IReadOnlyList<RedshiftSnapshot>>(target, "DescribeClusters", async c =>
        {
            var result = new List<RedshiftSnapshot>();
            if (only is null or { IsServerless: false })
            {
                string? marker = null;
                var pages = 0;
                do
                {
                    var resp = await c.Redshift.DescribeClustersAsync(new Rs.DescribeClustersRequest
                    {
                        ClusterIdentifier = only?.Id,
                        Marker = marker,
                        MaxRecords = only is null ? 100 : null,
                    }, ct);
                    result.AddRange((resp.Clusters ?? []).Select(k => new RedshiftSnapshot
                    {
                        Id = k.ClusterIdentifier ?? "",
                        Status = k.ClusterStatus ?? "",
                        AvailabilityStatus = k.ClusterAvailabilityStatus,
                        NodeType = k.NodeType,
                        NodeCount = k.NumberOfNodes,
                        DatabaseName = k.DBName,
                        Endpoint = k.Endpoint?.Address,
                        Port = k.Endpoint?.Port,
                        VpcId = k.VpcId,
                        SubnetGroup = k.ClusterSubnetGroupName,
                        SecurityGroups = (k.VpcSecurityGroups ?? []).Where(g => g.VpcSecurityGroupId is not null).Select(g => new SecurityGroupRef(g.VpcSecurityGroupId!, "")).ToList(),
                        PubliclyAccessible = k.PubliclyAccessible == true,
                        Encrypted = k.Encrypted == true,
                        MaintenanceWindow = k.PreferredMaintenanceWindow,
                        Created = k.ClusterCreateTime,
                    }));
                    marker = resp.Marker;
                } while (!string.IsNullOrEmpty(marker) && ++pages < MaxPages);
            }
            if (only is null or { IsServerless: true })
            {
                try
                {
                    var workgroups = new List<Rss.Workgroup>();
                    if (only is not null)
                    {
                        var one = await c.RedshiftServerless.GetWorkgroupAsync(new Rss.GetWorkgroupRequest { WorkgroupName = only.Id }, ct);
                        if (one.Workgroup is not null)
                            workgroups.Add(one.Workgroup);
                    }
                    else
                    {
                        string? token = null;
                        var pages = 0;
                        do
                        {
                            var resp = await c.RedshiftServerless.ListWorkgroupsAsync(new Rss.ListWorkgroupsRequest { NextToken = token }, ct);
                            workgroups.AddRange(resp.Workgroups ?? []);
                            token = resp.NextToken;
                        } while (!string.IsNullOrEmpty(token) && ++pages < MaxPages);
                    }
                    result.AddRange(workgroups.Select(w => new RedshiftSnapshot
                    {
                        Id = w.WorkgroupName ?? "",
                        IsServerless = true,
                        Status = w.Status?.Value?.ToLowerInvariant() ?? "",
                        BaseCapacityRpu = w.BaseCapacity,
                        Namespace = w.NamespaceName,
                        Endpoint = w.Endpoint?.Address,
                        Port = w.Endpoint?.Port,
                        VpcId = w.Endpoint?.VpcEndpoints?.FirstOrDefault()?.VpcId,
                        SubnetIds = w.SubnetIds ?? [],
                        SecurityGroups = (w.SecurityGroupIds ?? []).Select(id => new SecurityGroupRef(id, "")).ToList(),
                        PubliclyAccessible = w.PubliclyAccessible == true,
                        Created = w.CreationDate,
                    }));
                }
                catch (AmazonServiceException ex) when (only is null && !AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
                {
                    // Redshift Serverless is not in every region; provisioned clusters are still listed.
                }
            }
            return result.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToList();
        });
}
