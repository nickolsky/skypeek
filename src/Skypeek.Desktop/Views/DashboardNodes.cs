using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Skypeek.Core.Credentials;
using Skypeek.Core.Health;
using Skypeek.Core.Models;
using Skypeek.Core.Services;

namespace Skypeek.Desktop.Views;

public enum NodeKind
{
    Target,
    Group,
    EbApplication,
    EbEnvironment,
    EcsCluster,
    EcsService,
    EcsTask,
    EcsContainer,
    Alarm,
    Message,
    RdsCluster,
    RdsInstance,
    Cache,
    CacheNode,
    Ec2Instance,
    LoadBalancer,
    TargetGroup,
    Vpn,
    CodeBuildProject,
    Stack,
    Redshift,
    // Network tab
    Vpc,
    Subnet,
    NetworkInterface,
    SecurityGroup,
    ElasticIp,
    Gateway,
}

/// <summary>One row of the dashboard tree. <see cref="Payload"/> drives the details panel via implicit DataTemplates.</summary>
public sealed partial class DashNode : ObservableObject
{
    public required string Key { get; init; }
    public required NodeKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? Right { get; set; }
    public HealthLevel Level { get; set; } = HealthLevel.Ok;
    public bool Dim { get; set; }
    /// <summary>A resource the user hid; only shown with "Show hidden".</summary>
    public bool IsHiddenResource { get; set; }
    /// <summary>A resource (or everything below a target) capped to "info only": shown, not counted.</summary>
    public bool IsMuted { get; set; }
    /// <summary>The alert cap badge ("info only", "max warning").</summary>
    public string? CapBadge { get; set; }
    /// <summary>The resource behind the row (hide/unhide from the context menu).</summary>
    public ResourceStatus? Resource => Payload as ResourceStatus;
    public bool CanHide => Resource is { IsHidden: false };
    public bool CanUnhide => Resource is { IsHidden: true };
    public int ProblemCount { get; set; }
    public object? Payload { get; init; }
    /// <summary>Small usage bars shown after the title (CPU, memory, connections, disk).</summary>
    public IReadOnlyList<Gauge> Gauges { get; init; } = [];
    /// <summary>Extra text the tree search matches besides the title and subtitle (ids, endpoints, images, IPs…).</summary>
    public string? SearchText { get; init; }
    public ObservableCollection<DashNode> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;
    /// <summary>"refreshing health…" while something for this row is being refreshed.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsBusy))] private string? _activity;
    public bool IsBusy => Activity is not null;

    /// <summary>Matched the tree search (shown highlighted).</summary>
    public bool IsMatch { get; set; }

    public string? Badge => ProblemCount > 0 ? ProblemCount.ToString() : null;
    public bool IsStrong => Kind is NodeKind.Target or NodeKind.Group;
    public double TitleOpacity => Dim ? 0.55 : 1.0;

    /// <summary>Accessible name of the tree item.</summary>
    public override string ToString() => Title;
}

/// <param name="Percent">Bar fill 0–100; null shows only the text.</param>
public sealed record Gauge(string Label, double? Percent, string Text, HealthLevel Level)
{
    public bool HasBar => Percent is not null;

    /// <summary>The last 5 minutes, or the average over the poll's period when metrics are read rarely ("avg").</summary>
    public static Gauge? For(string label, MetricEvaluation? metric, string? text = null) =>
        metric?.Headline is { } value ? new Gauge(label, value, text ?? (metric.ShowsAverage ? $"{value:0}% avg" : $"{value:0}%"), Of(metric)) : null;

    /// <summary>Only threshold breaches colour a bar; otherwise it is green.</summary>
    public static HealthLevel Of(MetricEvaluation? metric) => metric?.Level is HealthLevel.Warn or HealthLevel.Critical ? metric.Level : HealthLevel.Ok;

    public static IReadOnlyList<Gauge> List(params Gauge?[] gauges) => gauges.Where(g => g is not null).ToList()!;
}

/// <summary>Details panel content for a target node.</summary>
public sealed class TargetDetail
{
    public required Target Target { get; init; }
    public required string ReadOnlyProfile { get; init; }
    public required string ReadOnlyState { get; init; }
    public string? ElevatedProfile { get; init; }
    public string? ElevatedState { get; init; }
    public string? Account { get; init; }
    public required string Catalog { get; init; }
    public required string Network { get; init; }
    public required string Health { get; init; }
    public required string Metrics { get; init; }
    public string? Errors { get; init; }
    public string? Cost { get; init; }

    // ---- monthly cost (when enabled for the target) ----
    public bool ShowCosts { get; init; }
    public string Costs { get; init; } = "";
    public string? Estimated { get; init; }
    public string? Billed { get; init; }
    public IReadOnlyList<ServiceCost> Services { get; init; } = [];
    public string? CostNote { get; init; }
    public string CostExplorerUrl => "https://us-east-1.console.aws.amazon.com/costmanagement/home#/cost-explorer";
}

public sealed class ClusterDetail
{
    public required string Name { get; init; }
    public required string Target { get; init; }
    public int Services { get; init; }
    public int Problems { get; init; }
    public required IReadOnlyList<EcsServiceStatus> ProblemServices { get; init; }
}

/// <summary>An alarm in the tree, with the resource it belongs to (if any).</summary>
public sealed class AlarmDetail
{
    public required Target Target { get; init; }
    public required AlarmInfo Alarm { get; init; }
    public string? Resource { get; init; }

    public string ConsoleUrl =>
        $"https://{Target.Region}.console.aws.amazon.com/cloudwatch/home?region={Target.Region}#alarmsV2:alarm/{Uri.EscapeDataString(Alarm.Name)}";
    public string Metric => $"{Alarm.Namespace} · {Alarm.MetricName}";
    public string Dimensions => string.Join(", ", Alarm.Dimensions.Select(d => $"{d.Key}={d.Value}"));
    public bool CanUnsuppress => Alarm.Suppressed && Alarm.SuppressedReason?.StartsWith("suppressed by rule", StringComparison.Ordinal) == true;
    public bool CanSuppress => !Alarm.Suppressed;
}

public sealed record MessageDetail(string Text);

/// <summary>An SSO profile that needs <c>aws sso login</c>; the details offer to run it.</summary>
public sealed record SsoSignInDetail(string Profile, string Text)
{
    public string Command => $"aws sso login --profile {Profile}";
}

/// <summary>One version of an EB application and where it runs.</summary>
public sealed record EbVersionRow(EbApplicationVersion Version, string DeployedTo, bool IsDeployed, bool IsLatest)
{
    public string Badge => IsLatest ? (IsDeployed ? "latest · deployed" : "latest") : IsDeployed ? "deployed" : "";
    public bool HasBadge => Badge.Length > 0;
}

/// <summary>An EB application: its environments and (loaded on demand) its versions.</summary>
public sealed partial class EbApplicationDetail : ObservableObject
{
    public required Target Target { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<EbEnvironmentStatus> Environments { get; init; }

    [ObservableProperty] private IReadOnlyList<EbVersionRow> _versions = [];
    [ObservableProperty] private string _versionsStatus = "Loading versions…";

    public string Key => $"{Target.Id}:ebapp:{Name}";
    public string Summary => $"Elastic Beanstalk application · {Target.DisplayName} · {Environments.Count} environment(s)";

    public void SetVersions(IReadOnlyList<EbApplicationVersion> versions, DateTime loadedUtc)
    {
        var deployed = Environments.Where(e => e.Snapshot.VersionLabel is not null).ToLookup(e => e.Snapshot.VersionLabel!, e => e.Snapshot.EnvironmentName);
        var latest = versions.OrderByDescending(v => v.Created).FirstOrDefault()?.Label;
        Versions = versions
            .Select(v => new EbVersionRow(v, deployed[v.Label].Any() ? $"running on {string.Join(", ", deployed[v.Label])}" : "not deployed", deployed[v.Label].Any(), v.Label == latest))
            .ToList();
        VersionsStatus = $"{versions.Count} version(s) · loaded {loadedUtc.ToLocalTime():T}";
    }
}

/// <summary>An ECS task selected in the tree, with the service it belongs to.</summary>
public sealed record EcsTaskDetail(EcsServiceStatus Service, EcsTaskInfo Task)
{
    public string ConsoleUrl =>
        $"https://{Service.Region}.console.aws.amazon.com/ecs/v2/clusters/{Uri.EscapeDataString(Service.Snapshot.ClusterName)}/tasks/{Task.TaskId}/configuration?region={Service.Region}";
}

/// <summary>A container of an ECS task.</summary>
public sealed record EcsContainerDetail(EcsServiceStatus Service, EcsTaskInfo Task, EcsContainerInfo Container)
{
    public string ConsoleUrl => new EcsTaskDetail(Service, Task).ConsoleUrl;
}

/// <summary>A cache node selected in the tree (the cache itself is the owning resource).</summary>
public sealed record CacheNodeDetail(CacheStatus Cache, CacheNodeView View);

/// <summary>A target group of a load balancer, with its targets.</summary>
public sealed record TargetGroupDetail(LoadBalancerStatus LoadBalancer, TargetGroupInfo Group)
{
    public string ConsoleUrl => LoadBalancer.TargetGroupConsoleUrl(Group);
    public IReadOnlyList<LbTargetInfo> Targets => Group.Targets;
}

/// <summary>The EC2 group: which instances it lists.</summary>
public sealed class Ec2GroupDetail
{
    public required Target Target { get; init; }
    public int Count { get; init; }
    public int Running { get; init; }
    public bool IncludeEb { get; init; }
    public string Summary => $"{Count} instance(s), {Running} running. " + (IncludeEb
        ? "Instances managed by Elastic Beanstalk are listed here too (and under their environment)."
        : "Instances managed by Elastic Beanstalk are shown under their environment, not here.");
}

/// <summary>Row in the suppressions list; <see cref="Rule"/> is the AlarmSuppression or CauseSuppression to remove.</summary>
public sealed record SuppressionRow(object Rule, string Pattern, string Scope);

/// <summary>A hidden resource in the suppressions list; <see cref="Key"/> is removed from the hidden set to unhide it.</summary>
public sealed record HiddenRow(string Key, string Name, string Scope);

/// <summary>Everything currently suppressed, shown from the dashboard toolbar.</summary>
public sealed class SuppressionsDetail
{
    public required string TargetTracking { get; init; }
    public required IReadOnlyList<SuppressionRow> Alarms { get; init; }
    public required IReadOnlyList<SuppressionRow> Causes { get; init; }
    public IReadOnlyList<HiddenRow> Hidden { get; init; } = [];

    public static SuppressionsDetail From(AppSettings settings, IReadOnlyList<Target> targets, IReadOnlyList<TargetHealth>? health = null) => new()
    {
        Hidden = settings.HiddenResources.Select(key =>
        {
            var resource = health?.SelectMany(h => h.AllResources).FirstOrDefault(r => r.ResourceKey == key);
            var parts = key.Split(':', 3);
            var target = parts.Length == 3 && long.TryParse(parts[0], out var id) ? targets.FirstOrDefault(t => t.Id == id)?.DisplayName : null;
            return new HiddenRow(key, resource?.DisplayName ?? (parts.Length == 3 ? parts[2] : key),
                $"{(parts.Length == 3 ? KindName(parts[1]) : "resource")} · {target ?? "removed target"}{(resource is null ? " · not seen in the last poll" : "")}");
        }).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        TargetTracking = settings.IgnoreTargetTrackingAlarms
            ? "Auto-scaling target-tracking alarms (TargetTracking-*) are ignored (Settings → Suppressions & thresholds)."
            : "Auto-scaling target-tracking alarms are counted.",
        Alarms = settings.SuppressedAlarms.Select(s => new SuppressionRow(s, s.Pattern, s.Scope(targets))).ToList(),
        Causes = settings.SuppressedCauses.Select(s => new SuppressionRow(s, s.Pattern, s.Scope(targets))).ToList(),
    };

    private static string KindName(string kind) => kind switch
    {
        "eb" => "Elastic Beanstalk environment",
        "ecs" => "ECS service",
        "rds" => "RDS instance",
        "rdscluster" => "RDS cluster",
        "cache" => "ElastiCache",
        "ec2" => "EC2 instance",
        "elb" => "load balancer",
        "vpn" => "Site-to-Site VPN",
        "codebuild" => "CodeBuild project",
        "cfn" => "CloudFormation stack",
        "redshift" => "Redshift",
        _ => kind,
    };
}

public static class DashboardTreeBuilder
{
    public static List<DashNode> Build(AppSession session, bool problemsOnly, bool showHidden = false)
    {
        var healthById = session.Health.Snapshot().ToDictionary(h => h.TargetId);
        var nodes = new List<DashNode>();

        foreach (var target in session.Settings.Targets.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var health = healthById.GetValueOrDefault(target.Id);
            var cred = session.Monitor.GetStatus(target.ProfileName);
            var account = session.Catalog.AccountOf(target.ProfileName);
            var children = new List<DashNode>();

            if (cred.State is CredentialState.SignInRequired
                || (cred.State is CredentialState.Halted && session.Monitor.Profiles.GetValueOrDefault(target.ProfileName)?.IsSso == true))
            {
                var text = cred.State == CredentialState.SignInRequired
                    ? $"AWS SSO sign-in required for {target.ProfileName} ({cred.ErrorCode})"
                    : $"AWS SSO credentials rejected ({cred.ErrorCode}) — sign in again";
                children.Add(new DashNode
                {
                    Key = $"{target.Id}:cred", Kind = NodeKind.Message, Title = text, Level = HealthLevel.Critical, ProblemCount = 1,
                    Payload = new SsoSignInDetail(target.ProfileName, text),
                });
            }
            else if (cred.State is CredentialState.Halted or CredentialState.Validating or CredentialState.Missing)
            {
                var text = cred.State == CredentialState.Missing
                    ? $"Profile {target.ProfileName} is missing from the credentials and config files"
                    : $"Credentials halted ({cred.ErrorCode}) — refresh the credentials file";
                children.Add(new DashNode
                {
                    Key = $"{target.Id}:cred", Kind = NodeKind.Message, Title = text, Level = HealthLevel.Critical, ProblemCount = 1,
                    Payload = new MessageDetail($"{text}. Refreshes for this target are paused and resume automatically once the profile's credentials change."),
                });
            }

            // A failed poll is a problem in the tray; show it here too (credential problems above already explain theirs).
            if (cred.State is not (CredentialState.Halted or CredentialState.Validating or CredentialState.Missing or CredentialState.SignInRequired))
                foreach (var kind in Enum.GetValues<JobKind>())
                    if (session.Scheduler.GetState(target.Id, kind) is { LastFailed: true } job)
                    {
                        var what = kind switch { JobKind.Catalog => "Secrets/parameters list", JobKind.Health => "Health poll", JobKind.Network => "Network download", _ => "Metrics poll" };
                        children.Add(new DashNode
                        {
                            Key = $"{target.Id}:job:{kind}", Kind = NodeKind.Message, Title = $"{what} failed: {job.LastError}", Level = HealthLevel.Warn, ProblemCount = 1,
                            Payload = new MessageDetail($"{what} failed at {job.LastAttempt?.ToLocalTime():g}: {job.LastError}. " +
                                "It is retried on the next scheduled run; use Refresh everything on the target to retry now."),
                        });
                    }

            if (health is null)
            {
                children.Add(new DashNode { Key = $"{target.Id}:nodata", Kind = NodeKind.Message, Title = "No health data yet", Level = HealthLevel.Unknown, Payload = new MessageDetail("Waiting for the first health poll.") });
            }
            else
            {
                if (target.EbEnabled)
                    children.Add(EbGroup(target, health, problemsOnly, session.Settings.Settings.EbGroupByApplication));
                if (target.EcsEnabled)
                    children.Add(EcsGroup(target, health, problemsOnly));
                if (target.RdsEnabled)
                    children.Add(RdsGroup(target, health, problemsOnly));
                if (target.CacheEnabled)
                    children.Add(CacheGroup(target, health, problemsOnly));
                if (target.Ec2Enabled)
                    children.Add(Ec2Group(target, health, problemsOnly, session.Settings.Settings.Ec2IncludeEbInstances));
                if (target.ElbEnabled)
                    children.Add(LoadBalancerGroup(target, health, problemsOnly));
                if (target.RedshiftEnabled && health.Redshift.Count > 0)
                    children.Add(RedshiftGroup(target, health, problemsOnly));
                if (target.VpnEnabled && health.Vpns.Count > 0)
                    children.Add(VpnGroup(target, health, problemsOnly));
                if (target.CodeBuildEnabled && health.Builds.Count > 0)
                    children.Add(CodeBuildGroup(target, health, problemsOnly));
                if (target.StacksEnabled && health.Stacks.Count > 0)
                    children.Add(StackGroup(target, health, problemsOnly));
                foreach (var note in health.AccessNotes)
                    children.Add(new DashNode
                    {
                        Key = $"{target.Id}:access:{note.Split(':')[0]}", Kind = NodeKind.Message, Title = $"No permission — {note}", Level = HealthLevel.Unknown,
                        Payload = new MessageDetail($"{note}\n\nThe read-only role cannot read this part, so it is left out; everything else is still monitored. " +
                            "Grant the read permission (see README → Permissions), or turn the feature off for this target in Settings → Accounts & regions."),
                    });
                children.Add(AlarmGroup(target, health, problemsOnly));
                if (health.HealthError is { } he)
                    children.Add(new DashNode { Key = $"{target.Id}:herr", Kind = NodeKind.Message, Title = $"Health poll error: {he}", Level = HealthLevel.Warn, ProblemCount = 1, Payload = new MessageDetail(he) });
                if (health.MetricsError is { } me)
                    children.Add(new DashNode { Key = $"{target.Id}:merr", Kind = NodeKind.Message, Title = $"Metrics poll error: {me}", Level = HealthLevel.Warn, ProblemCount = 1, Payload = new MessageDetail(me) });
            }

            foreach (var group in children)
                ApplyHidden(group, showHidden);
            var kept = problemsOnly ? children.Where(c => c.ProblemCount > 0).ToList() : children;
            if (problemsOnly && kept.Count == 0)
                continue;

            var costs = target.CostEnabled || target.CostExplorerEnabled ? AddCosts(kept, session) : 0;
            // NAT gateways, endpoints and public IPs (Network tab), and the secrets and Advanced parameters.
            var nat = Combine(session.Costs.NetworkEstimate(target), session.Costs.CatalogEstimate(target));
            var actual = target.CostExplorerEnabled ? session.Costs.Get(target.Id)?.Actual : null;
            var costText = string.Join(" · ", new[]
            {
                costs + (nat?.MonthlyUsd ?? 0) is var total and > 0 ? $"~{CostRules.Money(total)}/mo est." : null,
                actual is { } a ? $"billed {CostRules.Money(a.MonthToDate)} this month" : null,
            }.Where(s => s is not null));
            var node = new DashNode
            {
                Key = $"{target.Id}",
                Kind = NodeKind.Target,
                Title = target.DisplayName,
                Subtitle = $"{account ?? target.ProfileName} · {target.Region}{(target.Enabled ? "" : " · disabled")}{(costText.Length > 0 ? $" · {costText}" : "")}",
                SearchText = $"{target.ProfileName} {target.ElevatedProfileName} {account}",
                Payload = BuildTargetDetail(session, target, health, cred, account, costs, nat),
            };
            node.CapBadge = target.AlertCap switch { AlertCap.Info => "info only", AlertCap.Warning => "max warning", _ => null };
            foreach (var c in kept)
            {
                // "Info only" on the target: everything below is shown but counts as nothing.
                if (target.AlertCap == AlertCap.Info)
                    MarkMuted(c);
                node.Children.Add(c);
            }
            Summarize(node);
            nodes.Add(node);
        }
        return nodes;
    }

    /// <summary>
    /// Adds monthly cost to resource rows (right column) and group totals; returns the estimated total. Hidden
    /// resources are not counted.
    /// </summary>
    private static double AddCosts(IEnumerable<DashNode> nodes, AppSession session)
    {
        double total = 0;
        foreach (var node in nodes)
        {
            double own = 0;
            ResourceCost? cost = null;
            if (node.Payload is ResourceStatus { IsHidden: false } r && (cost = session.Costs.For(r)) is not null)
            {
                own = cost.Estimate?.MonthlyUsd ?? 0;
                node.Right ??= cost.Short;
            }
            var below = AddCosts(node.Children, session);
            if (below > 0 && node.Payload is not ResourceStatus)
                node.Right = node.Right is { Length: > 0 } right ? $"{right} · ~{CostRules.Money(below)}/mo" : $"~{CostRules.Money(below)}/mo";
            else if (below > 0 && own > 0 && node.Right == cost?.Short)
                node.Right = $"{node.Right} · ~{CostRules.Money(own + below)} total"; // e.g. a primary database with its read replicas
            else if (below > 0 && own == 0)
                // e.g. an Aurora cluster: its instances carry the cost
                node.Right = node.Right is { Length: > 0 } other && other != cost?.Short ? $"{other} · ~{CostRules.Money(below)}/mo" : $"~{CostRules.Money(below)}/mo";
            total += own + below;
        }
        return total;
    }

    /// <summary>
    /// Removes hidden resources (with their subtree) from a group, or with <paramref name="showHidden"/> keeps them
    /// dimmed and not counted. Group totals are recounted afterwards.
    /// </summary>
    private static void ApplyHidden(DashNode group, bool showHidden)
    {
        var changed = false;
        foreach (var child in group.Children.ToList())
        {
            if (child.Payload is ResourceStatus { CapBadge: { } badge })
                child.CapBadge = badge;
            if (child.Payload is ResourceStatus { IsHidden: false, IsMuted: true })
            {
                changed = true;
                MarkMuted(child);
                continue;
            }
            if (child.Payload is ResourceStatus { IsHidden: true })
            {
                changed = true;
                if (!showHidden)
                {
                    group.Children.Remove(child);
                    continue;
                }
                MarkHidden(child);
                continue;
            }
            var before = child.ProblemCount;
            ApplyHidden(child, showHidden);
            changed |= child.ProblemCount != before;
        }
        if (!changed)
            return;
        // Recount from the remaining children: a hidden problem no longer counts.
        group.ProblemCount = (group.Payload is ResourceStatus { IsProblem: true } ? 1 : 0) + group.Children.Sum(c => c.ProblemCount);
        group.Level = group.Payload is ResourceStatus own ? own.Level
            : group.Children.Where(c => !c.IsHiddenResource && !c.IsMuted).Select(c => c.Level).DefaultIfEmpty(HealthLevel.Ok).Max() is var max && max >= HealthLevel.Warn ? max : HealthLevel.Ok;
        if (group.Right is { } right && right.IndexOf('/') is var slash and > 0 && int.TryParse(right[..slash], out _))
            group.Right = $"{group.ProblemCount}{right[slash..]}";
    }

    /// <summary>Info-only resources keep their colour but count as nothing (and do not colour their group).</summary>
    private static CostEstimate? Combine(CostEstimate? a, CostEstimate? b) =>
        a is null ? b : b is null ? a : new CostEstimate(a.MonthlyUsd + b.MonthlyUsd, $"{a.Basis}; {b.Basis}", a.Complete && b.Complete);

    private static void MarkMuted(DashNode node)
    {
        node.IsMuted = true;
        node.ProblemCount = 0;
        foreach (var child in node.Children)
            MarkMuted(child);
    }

    private static void MarkHidden(DashNode node)
    {
        node.IsHiddenResource = true;
        node.Dim = true;
        node.ProblemCount = 0;
        foreach (var child in node.Children)
            MarkHidden(child);
    }

    private static DashNode EbGroup(Target target, TargetHealth health, bool problemsOnly, bool byApplication)
    {
        var apps = health.Eb.Select(e => e.Snapshot.ApplicationName).Distinct().Count();
        var group = new DashNode { Key = $"{target.Id}:eb", Kind = NodeKind.Group, Title = "Elastic Beanstalk", Payload = new MessageDetail($"{health.Eb.Count} environment(s) in {apps} application(s).") };

        DashNode EnvNode(EbEnvironmentStatus env) => new()
        {
            Key = env.ResourceKey,
            Kind = NodeKind.EbEnvironment,
            Title = env.Snapshot.EnvironmentName,
            Subtitle = (byApplication ? "" : $"{env.Snapshot.ApplicationName} · ")
                       + (env.Level >= HealthLevel.Warn ? env.ReasonText : $"{env.Snapshot.Health} · {env.Snapshot.Status} · {env.Snapshot.VersionLabel}")
                       + (env.Snapshot.IsLatestVersion == false ? " · not latest version" : ""),
            Gauges = Gauge.List(Gauge.For("CPU", env.Cpu), Gauge.For("Mem", env.Memory)),
            SearchText = string.Join(' ', new[] { env.Snapshot.ApplicationName, env.Snapshot.EnvironmentId, env.Snapshot.Cname, env.Snapshot.VersionLabel }
                .Concat(env.Snapshot.InstanceIds).Concat(env.Snapshot.InstanceHealth.Select(i => i.PrivateIp))),
            Level = env.Level,
            ProblemCount = env.Level >= HealthLevel.Warn ? 1 : 0,
            Payload = env,
        };

        if (!byApplication)
        {
            foreach (var env in health.Eb.OrderByDescending(e => e.Level).ThenBy(e => e.DisplayName))
                if (!problemsOnly || env.Level >= HealthLevel.Warn)
                    group.Children.Add(EnvNode(env));
            Summarize(group, health.Eb.Count);
            return group;
        }

        foreach (var app in health.Eb.GroupBy(e => e.Snapshot.ApplicationName).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var envs = app.OrderByDescending(e => e.Level).ThenBy(e => e.DisplayName).ToList();
            var detail = ApplicationDetail(target, app.Key, envs);
            var appNode = new DashNode
            {
                Key = detail.Key,
                Kind = NodeKind.EbApplication,
                Title = app.Key,
                Subtitle = VersionSpread(envs),
                Payload = detail,
            };
            foreach (var env in envs)
                if (!problemsOnly || env.Level >= HealthLevel.Warn)
                    appNode.Children.Add(EnvNode(env));
            if (problemsOnly && appNode.Children.Count == 0)
                continue;
            Summarize(appNode, envs.Count);
            group.Children.Add(appNode);
        }
        Summarize(group, health.Eb.Count);
        return group;
    }

    /// <summary>The dashboard node of an EC2 instance: its own row, or the EB environment it belongs to.</summary>
    public static string? InstanceKey(TargetHealth health, string instanceId) =>
        health.Ec2.FirstOrDefault(i => i.Snapshot.InstanceId == instanceId && !i.IsHidden)?.ResourceKey
        ?? health.Eb.FirstOrDefault(env => env.Snapshot.InstanceIds.Contains(instanceId) && !env.IsHidden)?.ResourceKey;

    public static EbApplicationDetail ApplicationDetail(Target target, string application, IReadOnlyList<EbEnvironmentStatus> environments) =>
        new() { Target = target, Name = application, Environments = environments };

    public static EbApplicationDetail? ApplicationDetail(AppSession session, long targetId, string application) =>
        session.Settings.FindTarget(targetId) is { } target && session.Health.Get(targetId) is { } health
            ? ApplicationDetail(target, application, health.Eb.Where(e => e.Snapshot.ApplicationName == application).OrderBy(e => e.DisplayName).ToList())
            : null;

    /// <summary>E.g. "all on v42" or "3 versions deployed".</summary>
    private static string VersionSpread(IReadOnlyList<EbEnvironmentStatus> envs)
    {
        var versions = envs.Select(e => e.Snapshot.VersionLabel).Where(v => v is not null).Distinct().ToList();
        var behind = envs.Count(e => e.Snapshot.IsLatestVersion == false);
        var text = versions.Count switch
        {
            0 => "no version",
            1 => envs.Count == 1 ? $"{versions[0]}" : $"all on {versions[0]}",
            _ => $"{versions.Count} versions deployed",
        };
        return behind > 0 ? $"{text} · {behind} not on latest" : text;
    }

    private static DashNode RdsGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:rds",
            Kind = NodeKind.Group,
            Title = "RDS",
            Payload = new MessageDetail($"{health.Rds.Count} database instance(s), {health.RdsClusters.Count} cluster(s). Clusters list their writer and readers; read replicas sit under their source."),
        };
        var byId = health.Rds.ToDictionary(r => r.Snapshot.Identifier);
        var placed = new HashSet<string>();

        DashNode InstanceNode(RdsInstanceStatus db, int depth)
        {
            placed.Add(db.Snapshot.Identifier);
            var node = new DashNode
            {
                Key = db.ResourceKey,
                Kind = NodeKind.RdsInstance,
                Title = db.Snapshot.Identifier,
                Subtitle = db.Level >= HealthLevel.Warn ? db.ReasonText
                    : $"{db.Snapshot.Role} · {db.Snapshot.EngineText} · {db.Snapshot.InstanceClass} · {db.Snapshot.Status}"
                      + (db.Snapshot.ReplicaSource is { } src && !byId.ContainsKey(src) ? $" · replica of {src}" : ""),
                Gauges = RdsGauges(db),
                SearchText = $"{db.Snapshot.Address} {db.Snapshot.Arn} {db.Snapshot.ClusterIdentifier} {db.Snapshot.ParameterGroup} {db.Snapshot.ReplicaSource}",
                Level = db.Level,
                ProblemCount = db.Level >= HealthLevel.Warn ? 1 : 0,
                Payload = db,
            };
            // Read replicas in this region nest under their source (depth guard against odd loops).
            foreach (var replicaId in db.Snapshot.Replicas.Where(byId.ContainsKey).Where(id => !placed.Contains(id)))
                if (depth < 5)
                    node.Children.Add(InstanceNode(byId[replicaId], depth + 1));
            foreach (var external in db.Snapshot.Replicas.Where(id => !byId.ContainsKey(id)))
                node.Children.Add(new DashNode
                {
                    Key = $"{db.ResourceKey}:replica:{external}",
                    Kind = NodeKind.Message,
                    Title = external,
                    Subtitle = "replica in another region or cluster",
                    Level = HealthLevel.Unknown,
                    Payload = new MessageDetail($"{external} replicates from {db.Snapshot.Identifier}. It is outside this target (another region, or an Aurora cluster); add that region as a target to monitor it."),
                });
            // The dot shows this database's own health; the badge also counts its replicas' problems.
            node.ProblemCount += node.Children.Sum(c => c.ProblemCount);
            return node;
        }

        var nodes = new List<DashNode>();
        foreach (var cluster in health.RdsClusters)
        {
            var node = new DashNode
            {
                Key = cluster.ResourceKey,
                Kind = NodeKind.RdsCluster,
                Title = cluster.Snapshot.Identifier,
                SearchText = $"{cluster.Snapshot.WriterEndpoint} {cluster.Snapshot.ReaderEndpoint} {string.Join(' ', cluster.Snapshot.CustomEndpoints)} {cluster.Snapshot.Arn}",
                Subtitle = cluster.Level >= HealthLevel.Warn ? cluster.ReasonText
                    : $"cluster · {cluster.Snapshot.EngineText} · {cluster.Snapshot.Members.Count} instance(s) · {cluster.Snapshot.Status}"
                      + (cluster.Snapshot.ReplicationSource is not null ? " · replica cluster" : ""),
                Level = cluster.Level,
                ProblemCount = cluster.Level >= HealthLevel.Warn ? 1 : 0,
                Payload = cluster,
            };
            foreach (var member in cluster.Snapshot.Members.OrderByDescending(m => m.IsWriter).ThenBy(m => m.InstanceId))
                if (byId.TryGetValue(member.InstanceId, out var db))
                    node.Children.Add(InstanceNode(db, 1));
            node.ProblemCount += node.Children.Sum(c => c.ProblemCount);
            nodes.Add(node);
        }
        // Standalone instances and replica roots whose source is not here.
        foreach (var db in health.Rds.Where(d => !placed.Contains(d.Snapshot.Identifier) && (d.Snapshot.ReplicaSource is null || !byId.ContainsKey(d.Snapshot.ReplicaSource))))
            nodes.Add(InstanceNode(db, 0));
        // Anything left (e.g. a replica cycle) still shows up.
        foreach (var db in health.Rds.Where(d => !placed.Contains(d.Snapshot.Identifier)).ToList())
            nodes.Add(InstanceNode(db, 0));

        foreach (var node in nodes.OrderByDescending(n => n.Level).ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase))
            if (!problemsOnly || node.ProblemCount > 0)
                group.Children.Add(node);
        Summarize(group, health.Rds.Count + health.RdsClusters.Count);
        return group;
    }

    private static IReadOnlyList<Gauge> RdsGauges(RdsInstanceStatus db)
    {
        // Connections fill against max_connections when it is known; otherwise just the count.
        var connections = db.Connections?.Current is not { } count ? null
            : db.Memory?.Current is { } percent ? new Gauge("Conn", percent, $"{count:0}", Gauge.Of(db.Memory))
            : new Gauge("Conn", null, $"{count:0}", HealthLevel.Ok);
        var disk = db.StorageUsed?.Current is { } used
            ? new Gauge("Disk", used, $"{RdsInstanceStatus.FormatBytes(db.FreeStorageBytes)} free / {db.SizeText}", Gauge.Of(db.StorageUsed))
            : null;
        return Gauge.List(Gauge.For("CPU", db.Cpu), connections, disk);
    }

    private static DashNode CacheGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:cache",
            Kind = NodeKind.Group,
            Title = "ElastiCache",
            Payload = new MessageDetail($"{health.Caches.Count} cache(s): Redis/Valkey replication groups, standalone clusters and serverless caches."),
        };
        foreach (var cache in health.Caches.OrderByDescending(c => c.Level).ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (problemsOnly && cache.Level < HealthLevel.Warn)
                continue;
            var snapshot = cache.Snapshot;
            var node = new DashNode
            {
                Key = cache.ResourceKey,
                Kind = NodeKind.Cache,
                Title = snapshot.Id,
                Subtitle = cache.Level >= HealthLevel.Warn ? cache.ReasonText
                    : $"{snapshot.EngineText} · {snapshot.NodeType} · {snapshot.Status}"
                      + (snapshot.Kind == CacheKind.Serverless ? " · serverless" : snapshot.ClusterMode ? $" · {snapshot.Shards.Count} shard(s)" : $" · {snapshot.Nodes.Count()} node(s)"),
                Gauges = Gauge.List(Gauge.For("CPU", cache.Cpu), Gauge.For("Mem", cache.Memory)),
                SearchText = $"{snapshot.PrimaryEndpoint} {snapshot.ReaderEndpoint} {snapshot.ConfigurationEndpoint} {snapshot.Description} {snapshot.Arn} {string.Join(' ', snapshot.Nodes.Select(n => n.ClusterId))}",
                Level = cache.Level,
                ProblemCount = cache.Level >= HealthLevel.Warn ? 1 : 0,
                Payload = cache,
            };

            DashNode NodeNode(CacheNodeView view) => new()
            {
                Key = $"{cache.ResourceKey}:{view.Node.ClusterId}:{view.Node.NodeId}",
                Kind = NodeKind.CacheNode,
                Title = view.Node.ClusterId,
                Subtitle = $"{view.Node.Role ?? "node"} · {view.Node.Status}{(view.Node.AvailabilityZone is null ? "" : $" · {view.Node.AvailabilityZone}")}",
                Gauges = Gauge.List(Gauge.For("CPU", view.Metric?.Cpu), Gauge.For("Mem", view.Metric?.Memory)),
                SearchText = view.Node.Endpoint,
                Level = view.Level,
                Payload = new CacheNodeDetail(cache, view),
            };

            var views = cache.NodeViews;
            if (snapshot.ClusterMode && snapshot.Shards.Count > 1)
            {
                foreach (var shard in snapshot.Shards)
                {
                    var shardNode = new DashNode
                    {
                        Key = $"{cache.ResourceKey}:shard:{shard.Id}",
                        Kind = NodeKind.Group,
                        Title = $"Shard {shard.Id}",
                        Subtitle = $"{shard.Status}{(shard.Slots is null ? "" : $" · slots {shard.Slots}")}",
                        Payload = cache,
                    };
                    foreach (var view in views.Where(v => v.Shard == shard.Id))
                        shardNode.Children.Add(NodeNode(view));
                    Summarize(shardNode);
                    node.Children.Add(shardNode);
                }
            }
            else if (views.Count > 1)
            {
                foreach (var view in views.OrderBy(v => v.Node.Role != "primary").ThenBy(v => v.Node.ClusterId))
                    node.Children.Add(NodeNode(view));
            }
            // Node levels are shown, but the cache counts once.
            node.Level = HealthRules.Max(cache.Level, node.Children.Count == 0 ? HealthLevel.Ok : node.Children.Max(c => c.Level));
            group.Children.Add(node);
        }
        Summarize(group, health.Caches.Count);
        return group;
    }

    private static DashNode Ec2Group(Target target, TargetHealth health, bool problemsOnly, bool includeEb)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:ec2",
            Kind = NodeKind.Group,
            Title = "EC2 instances",
            Payload = new Ec2GroupDetail { Target = target, Count = health.Ec2.Count, Running = health.Ec2.Count(e => e.Snapshot.IsRunning), IncludeEb = includeEb },
        };
        foreach (var ec2 in health.Ec2.OrderByDescending(e => e.Level).ThenBy(e => e.Snapshot.IsStopped).ThenBy(e => e.Snapshot.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (problemsOnly && ec2.Level < HealthLevel.Warn)
                continue;
            var s = ec2.Snapshot;
            group.Children.Add(new DashNode
            {
                Key = ec2.ResourceKey,
                Kind = NodeKind.Ec2Instance,
                Title = s.Title,
                Subtitle = ec2.Level >= HealthLevel.Warn ? ec2.ReasonText
                    : string.Join(" · ", new[] { s.State, s.InstanceType, s.PrivateIp, s.OwnerText }.Where(x => !string.IsNullOrEmpty(x))),
                Gauges = Gauge.List(Gauge.For("CPU", ec2.Cpu), Gauge.For("Mem", ec2.Memory)),
                SearchText = string.Join(' ', new[] { s.InstanceId, s.PublicIp, s.PrivateDns, s.PublicDns, s.VpcId, s.SubnetId, s.ImageId, s.EbEnvironment, s.AutoScalingGroup }
                    .Concat(s.Interfaces.SelectMany(i => i.PrivateIps.Append(i.PublicIp ?? "").Concat(i.Ipv6)))
                    .Concat(s.SecurityGroups.SelectMany(g => new[] { g.Id, g.Name }))),
                Level = s.IsStopped ? HealthLevel.Unknown : ec2.Level,
                Dim = s.IsStopped,
                ProblemCount = ec2.Level >= HealthLevel.Warn ? 1 : 0,
                Payload = ec2,
            });
        }
        Summarize(group, health.Ec2.Count);
        return group;
    }

    private static DashNode LoadBalancerGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:elb",
            Kind = NodeKind.Group,
            Title = "Load balancers",
            Payload = new MessageDetail($"{health.LoadBalancers.Count} load balancer(s) with {health.LoadBalancers.Sum(l => l.Snapshot.TargetGroups.Count)} target group(s). "
                                        + "A target group with unhealthy targets is a warning; one with no healthy target is critical."),
        };
        foreach (var lb in health.LoadBalancers.OrderByDescending(l => l.Level).ThenBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (problemsOnly && lb.Level < HealthLevel.Warn)
                continue;
            var s = lb.Snapshot;
            var node = new DashNode
            {
                Key = lb.ResourceKey,
                Kind = NodeKind.LoadBalancer,
                Title = s.Name,
                Subtitle = lb.Level >= HealthLevel.Warn ? lb.ReasonText : $"{s.TypeText} · {s.Scheme} · {s.State} · {s.TargetGroups.Count} target group(s)",
                Gauges = s.Counted == 0 ? [] : [new Gauge("Healthy", s.Healthy * 100.0 / s.Counted, $"{s.Healthy}/{s.Counted}",
                    s.Healthy == s.Counted ? HealthLevel.Ok : s.Healthy == 0 ? HealthLevel.Critical : HealthLevel.Warn)],
                SearchText = $"{s.DnsName} {s.Arn} {s.VpcId} {string.Join(' ', s.SecurityGroups)} {string.Join(' ', s.TargetGroups.SelectMany(g => g.Targets.Select(t => t.Id)))}",
                Level = lb.Level,
                ProblemCount = lb.Level >= HealthLevel.Warn ? 1 : 0,
                Payload = lb,
            };
            foreach (var tg in s.TargetGroups)
                node.Children.Add(new DashNode
                {
                    Key = $"{lb.ResourceKey}:tg:{tg.Name}",
                    Kind = NodeKind.TargetGroup,
                    Title = tg.Name,
                    Subtitle = $"{tg.ProtocolText} · {tg.TargetType} · {tg.HealthText}{(tg.Error is null ? "" : " · health unknown")}",
                    Gauges = tg.Counted == 0 ? [] : [new Gauge("Healthy", tg.Healthy * 100.0 / tg.Counted, $"{tg.Healthy}/{tg.Counted}", tg.Level == HealthLevel.Unknown ? HealthLevel.Ok : tg.Level)],
                    SearchText = $"{tg.Arn} {string.Join(' ', tg.Targets.Select(t => t.Id))}",
                    Level = tg.Level,
                    Payload = new TargetGroupDetail(lb, tg),
                });
            group.Children.Add(node);
        }
        Summarize(group, health.LoadBalancers.Count);
        return group;
    }

    private static DashNode VpnGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:vpn", Kind = NodeKind.Group, Title = "Site-to-Site VPN",
            Payload = new MessageDetail($"{health.Vpns.Count} VPN connection(s). One tunnel down is a warning (suppress it for sites with a single tunnel); all tunnels down is critical."),
        };
        foreach (var vpn in health.Vpns.OrderByDescending(v => v.Level).ThenBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (problemsOnly && vpn.Level < HealthLevel.Warn)
                continue;
            var s = vpn.Snapshot;
            group.Children.Add(new DashNode
            {
                Key = vpn.ResourceKey, Kind = NodeKind.Vpn, Title = s.Name ?? s.Id,
                Subtitle = vpn.Level >= HealthLevel.Warn ? vpn.ReasonText : $"{s.State} · {s.CustomerText} · {s.GatewayText}",
                Gauges = s.Tunnels.Count == 0 ? [] : [new Gauge("Tunnels", s.UpCount * 100.0 / s.Tunnels.Count, $"{s.UpCount}/{s.Tunnels.Count} up",
                    s.UpCount == s.Tunnels.Count ? HealthLevel.Ok : s.UpCount == 0 ? HealthLevel.Critical : HealthLevel.Warn)],
                SearchText = $"{s.Id} {s.CustomerGatewayIp} {s.CustomerGatewayId} {s.VpnGatewayId} {s.TransitGatewayId} {string.Join(' ', s.Tunnels.Select(t => t.OutsideIp))} {string.Join(' ', s.StaticRoutes)}",
                Level = vpn.Level, ProblemCount = vpn.Level >= HealthLevel.Warn ? 1 : 0, Payload = vpn,
            });
        }
        Summarize(group, health.Vpns.Count);
        return group;
    }

    private static DashNode CodeBuildGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:codebuild", Kind = NodeKind.Group, Title = "CodeBuild",
            Payload = new MessageDetail($"{health.Builds.Count} project(s). A failed latest build is critical (no CloudWatch alarm needed); suppress a known failure from the project's details."),
        };
        foreach (var b in health.Builds.OrderByDescending(b => b.Level).ThenByDescending(b => b.Snapshot.LatestBuild?.Started).ThenBy(b => b.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (problemsOnly && b.Level < HealthLevel.Warn)
                continue;
            var latest = b.Snapshot.LatestBuild;
            group.Children.Add(new DashNode
            {
                Key = b.ResourceKey, Kind = NodeKind.CodeBuildProject, Title = b.DisplayName,
                Subtitle = b.Level >= HealthLevel.Warn ? b.ReasonText : latest?.Summary ?? "no builds",
                Right = b.IsBuilding ? $"building {latest!.NumberText}" : null,
                SearchText = $"{latest?.Id} {latest?.ResolvedSourceVersion} {latest?.Initiator}",
                Level = b.Level, ProblemCount = b.Level >= HealthLevel.Warn ? 1 : 0, Payload = b,
                Dim = latest is null || latest.Started < DateTime.UtcNow.AddDays(-90),
            });
        }
        Summarize(group, health.Builds.Count);
        return group;
    }

    /// <summary>Stacks, with nested stacks under their root stack.</summary>
    private static DashNode StackGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:cfn", Kind = NodeKind.Group, Title = "CloudFormation",
            Payload = new MessageDetail($"{health.Stacks.Count} stack(s). Failed stacks (and ROLLBACK_COMPLETE) are critical; an update that failed and rolled back is a warning. "
                                        + "Select a stack for its history."),
        };
        DashNode Node(StackStatus st) => new()
        {
            Key = st.ResourceKey, Kind = NodeKind.Stack, Title = st.DisplayName,
            Subtitle = st.Level >= HealthLevel.Warn ? st.ReasonText
                : $"{st.Snapshot.Status}{((st.Snapshot.LastUpdated ?? st.Snapshot.Created) is { } t ? $" · {t.ToLocalTime():g}" : "")}",
            SearchText = $"{st.Snapshot.Id} {st.Snapshot.Status} {st.Snapshot.Description}",
            Level = st.Level, ProblemCount = st.Level >= HealthLevel.Warn ? 1 : 0, Payload = st,
        };
        var byId = health.Stacks.ToDictionary(s => s.Snapshot.Id);
        var roots = health.Stacks.Where(s => !s.Snapshot.IsNested || !byId.ContainsKey(s.Snapshot.RootId!)).ToList();
        foreach (var root in roots.OrderByDescending(s => s.Level).ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var nested = health.Stacks.Where(s => s.Snapshot.RootId == root.Snapshot.Id).OrderByDescending(s => s.Level).ThenBy(s => s.DisplayName).ToList();
            var worst = nested.Select(n => n.Level).Append(root.Level).Max();
            if (problemsOnly && worst < HealthLevel.Warn)
                continue;
            var node = Node(root);
            foreach (var child in nested.Where(n => !problemsOnly || n.Level >= HealthLevel.Warn))
                node.Children.Add(Node(child));
            group.Children.Add(node);
        }
        Summarize(group, health.Stacks.Count);
        return group;
    }

    private static DashNode RedshiftGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode
        {
            Key = $"{target.Id}:redshift", Kind = NodeKind.Group, Title = "Redshift",
            Payload = new MessageDetail($"{health.Redshift.Count} cluster(s) and serverless workgroup(s). Read-only: manage them in the console."),
        };
        foreach (var r in health.Redshift.OrderByDescending(r => r.Level).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (problemsOnly && r.Level < HealthLevel.Warn)
                continue;
            var s = r.Snapshot;
            group.Children.Add(new DashNode
            {
                Key = r.ResourceKey, Kind = NodeKind.Redshift, Title = s.Id,
                Subtitle = r.Level >= HealthLevel.Warn ? r.ReasonText : $"{s.Status} · {s.SizeText}",
                Gauges = new[] { Gauge.For("CPU", r.Cpu), r.DiskUsedPercent is { } d ? new Gauge("Disk", d, $"{d:0}%", d >= 90 ? HealthLevel.Warn : HealthLevel.Ok) : null }
                    .Where(g => g is not null).ToList()!,
                SearchText = $"{s.Endpoint} {s.VpcId} {s.Namespace} {string.Join(' ', s.SecurityGroups.Select(g => g.Id))}",
                Level = r.Level, ProblemCount = r.Level >= HealthLevel.Warn ? 1 : 0, Payload = r, Dim = s.IsPaused,
            });
        }
        Summarize(group, health.Redshift.Count);
        return group;
    }

    private static DashNode EcsGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var group = new DashNode { Key = $"{target.Id}:ecs", Kind = NodeKind.Group, Title = "ECS", Payload = new MessageDetail($"{health.Ecs.Count} service(s).") };
        foreach (var cluster in health.Ecs.GroupBy(s => s.Snapshot.ClusterName).OrderBy(g => g.Key))
        {
            var services = cluster.OrderByDescending(s => s.Level).ThenBy(s => s.Snapshot.ServiceName).ToList();
            var problems = services.Where(s => s.Level >= HealthLevel.Warn).ToList();
            var clusterNode = new DashNode
            {
                Key = $"{target.Id}:ecs:{cluster.Key}",
                Kind = NodeKind.EcsCluster,
                Title = cluster.Key,
                Payload = new ClusterDetail { Name = cluster.Key, Target = target.DisplayName, Services = services.Count, Problems = problems.Count, ProblemServices = problems },
            };
            foreach (var svc in services)
            {
                if (problemsOnly && svc.Level < HealthLevel.Warn)
                    continue;
                var serviceNode = new DashNode
                {
                    Key = svc.ResourceKey,
                    Kind = NodeKind.EcsService,
                    Title = svc.Snapshot.ServiceName,
                    Subtitle = svc.Level >= HealthLevel.Warn ? svc.ReasonText : $"{svc.Snapshot.Running}/{svc.Snapshot.Desired} tasks",
                    Gauges = Gauge.List(Gauge.For("CPU", svc.Cpu), Gauge.For("Mem", svc.Memory)),
                    SearchText = $"{cluster.Key} {string.Join(' ', svc.Snapshot.Deployments.Select(d => d.TaskDefinition))}",
                    Level = svc.Level,
                    ProblemCount = svc.Level >= HealthLevel.Warn ? 1 : 0,
                    Payload = svc,
                };
                // Tasks and their containers are shown for context; the service carries the health problem.
                foreach (var task in svc.Snapshot.Tasks)
                {
                    var taskNode = new DashNode
                    {
                        Key = $"{svc.ResourceKey}:task:{task.TaskId}",
                        Kind = NodeKind.EcsTask,
                        Title = task.TaskId,
                        Subtitle = task.Summary,
                        SearchText = $"{task.TaskArn} {task.LaunchType} {task.CapacityProvider} {task.StartedBy}",
                        Level = task.Level,
                        Payload = new EcsTaskDetail(svc, task),
                    };
                    foreach (var container in task.Containers)
                        taskNode.Children.Add(new DashNode
                        {
                            Key = $"{taskNode.Key}:{container.Name}",
                            Kind = NodeKind.EcsContainer,
                            Title = container.Name,
                            Subtitle = $"{container.Summary} · {container.ImageShort}",
                            SearchText = $"{container.Image} {container.ImageDigest} {container.RuntimeId}",
                            Level = container.Level,
                            Payload = new EcsContainerDetail(svc, task, container),
                        });
                    serviceNode.Children.Add(taskNode);
                }
                clusterNode.Children.Add(serviceNode);
            }
            if (problemsOnly && clusterNode.Children.Count == 0)
                continue;
            Summarize(clusterNode, services.Count);
            group.Children.Add(clusterNode);
        }
        Summarize(group, health.Ecs.Count);
        return group;
    }

    private static DashNode AlarmGroup(Target target, TargetHealth health, bool problemsOnly)
    {
        var owners = new Dictionary<AlarmInfo, string>(ReferenceEqualityComparer.Instance);
        // Alarms of hidden resources are hidden with them.
        foreach (var r in health.AllResources.Where(r => !r.IsHidden)) foreach (var a in r.Alarms) owners[a] = r.DisplayName;

        var alarms = owners.Keys.Concat(health.OtherAlarms)
            .Where(a => a.IsActive || a.IsRecent)
            .OrderByDescending(a => a.CountsAsProblem).ThenBy(a => a.Suppressed).ThenBy(a => a.Name)
            .ToList();
        var suppressed = alarms.Count(a => a.IsActive && a.Suppressed);

        var group = new DashNode
        {
            Key = $"{target.Id}:alarms",
            Kind = NodeKind.Group,
            Title = "CloudWatch alarms",
            Payload = new MessageDetail($"CPU/memory alarms, EC2 status-check alarms, and every RDS, ElastiCache and load balancer alarm, that are in ALARM or fired recently. {suppressed} active alarm(s) are suppressed."),
        };
        foreach (var alarm in alarms)
        {
            if (problemsOnly && !alarm.CountsAsProblem)
                continue;
            owners.TryGetValue(alarm, out var owner);
            group.Children.Add(new DashNode
            {
                Key = $"{target.Id}:alarm:{alarm.Name}",
                Kind = NodeKind.Alarm,
                Title = alarm.Name,
                SearchText = $"{alarm.Namespace} {alarm.MetricName} {string.Join(' ', alarm.Dimensions.Values)}",
                Subtitle = alarm.Suppressed ? $"{alarm.Badge} — {alarm.SuppressedReason}" : alarm.Badge,
                Right = owner,
                Level = alarm.CountsAsProblem ? HealthLevel.Critical : alarm.Suppressed ? HealthLevel.Unknown : HealthLevel.Ok,
                // An alarm on a service/environment is already counted there; count only unattached ones here.
                ProblemCount = alarm.CountsAsProblem && owner is null ? 1 : 0,
                Dim = alarm.Suppressed,
                Payload = new AlarmDetail { Target = target, Alarm = alarm, Resource = owner },
            });
        }
        Summarize(group, alarms.Count, suppressed > 0 ? $"{suppressed} suppressed" : null);
        return group;
    }

    private static void Summarize(DashNode node, int? total = null, string? extra = null)
    {
        if (node.Children.Count > 0)
        {
            node.ProblemCount = Math.Max(node.ProblemCount, node.Children.Sum(c => c.ProblemCount));
            node.Level = node.Children.Where(c => !c.IsMuted).Select(c => c.Level).DefaultIfEmpty(HealthLevel.Ok).Max() is var max && max >= HealthLevel.Warn ? max : HealthLevel.Ok;
        }
        if (total is not null && node.Right is null)
        {
            // Right column shows "problems/total".
            node.Right = $"{node.ProblemCount}/{total}{(extra is null ? "" : $" · {extra}")}";
        }
    }

    /// <summary>
    /// Keeps nodes whose title, subtitle or search text contains every word of the query, plus the path to them.
    /// A matching node keeps its whole subtree (collapsed); the path to deeper matches is expanded.
    /// </summary>
    public static List<DashNode> Filter(IEnumerable<DashNode> nodes, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<DashNode>();
        if (words.Length == 0)
            return nodes.ToList();
        foreach (var node in nodes)
            if (Prune(node, words))
                kept.Add(node);
        return kept;
    }

    public static bool Matches(DashNode node, IReadOnlyList<string> words)
    {
        var text = $"{node.Title} {node.Subtitle} {node.SearchText}";
        return words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the node stays; removes non-matching children in place and marks the path open.</summary>
    private static bool Prune(DashNode node, IReadOnlyList<string> words)
    {
        if (Matches(node, words))
        {
            node.IsMatch = true;
            return true;
        }
        foreach (var child in node.Children.ToList())
            if (!Prune(child, words))
                node.Children.Remove(child);
        if (node.Children.Count == 0)
            return false;
        node.IsExpanded = true;
        return true;
    }

    private static TargetDetail BuildTargetDetail(AppSession session, Target target, TargetHealth? health, ProfileStatus cred, string? account,
        double estimated = 0, CostEstimate? nat = null)
    {
        string Job(JobKind kind)
        {
            if (!TargetScheduler.IsFeatureEnabled(target, kind))
                return "off";
            var s = session.Scheduler.GetState(target.Id, kind);
            var text = s.Running ? "running…" : s.LastSuccess is { } ok ? $"ok {ok.ToLocalTime():g} ({s.LastCount})" : "never";
            if (s.LastFailed)
                text += $" — failed: {s.LastError}";
            var interval = TargetScheduler.IntervalMinutes(target, kind);
            text += interval <= 0 ? " · periodic off" : session.Scheduler.NextDue(target, kind) is { } next ? $" · next {next.ToLocalTime():t}" : "";
            return text;
        }

        var metricCount = session.Health.MetricCount(target.Id);
        var elevated = target.ElevatedProfileName is { Length: > 0 } ep ? session.Monitor.GetStatus(ep) : null;
        var costSnapshot = session.Costs.Get(target.Id);
        var actual = target.CostExplorerEnabled ? costSnapshot?.Actual : null;
        var pricesPending = target.CostEnabled ? session.Costs.MissingPrices(target, DateTime.UtcNow).Count : 0;
        return new TargetDetail
        {
            ShowCosts = target.CostEnabled || target.CostExplorerEnabled,
            Costs = Job(JobKind.Costs),
            Estimated = !target.CostEnabled ? null
                : $"~{CostRules.Money(estimated + (nat?.MonthlyUsd ?? 0))}/month on-demand list price for the resources on the dashboard"
                  + (nat is not null ? $" (including {nat.Text}: {nat.Basis})" : "")
                  + ". Excludes EBS, data transfer, load balancer capacity units, backups and discounts (savings plans, reserved instances)."
                  + (pricesPending > 0 ? $" {pricesPending} price(s) not read yet." : ""),
            Billed = actual is null ? (target.CostExplorerEnabled ? "Not read yet." : null)
                : $"{CostRules.Money(actual.MonthToDate)} this month so far{(actual.MonthToDateEstimated ? " (estimated)" : "")} · {CostRules.Money(actual.LastMonth)} last month · region {target.Region}, read {actual.FetchedUtc.ToLocalTime():g}",
            Services = actual?.Services.Take(12).ToList() ?? [],
            CostNote = string.Join(" ", new[] { actual?.ResourceNote, costSnapshot?.Error }.Where(s => s is not null)) is { Length: > 0 } n ? n : null,
            Target = target,
            Account = account,
            ReadOnlyProfile = target.ProfileName,
            ReadOnlyState = Describe(cred),
            ElevatedProfile = target.ElevatedProfileName,
            ElevatedState = elevated is null ? null
                : target.UsesSameKey ? $"⚠ same key as read-only (confirmed exception) · {Describe(elevated)}"
                : Describe(elevated),
            Catalog = Job(JobKind.Catalog),
            Network = Job(JobKind.Network),
            Health = Job(JobKind.Health),
            Metrics = Job(JobKind.Metrics),
            Errors = string.Join("\n", new[] { health?.HealthError, health?.MetricsError }.Where(e => e is not null)) is { Length: > 0 } err ? err : null,
            Cost = metricCount == 0 || target.MetricsIntervalMinutes <= 0 ? null
                : $"~{metricCount} metrics per poll ≈ ${Core.Health.HealthRules.EstimateMonthlyMetricsCostUsd(metricCount, target.MetricsIntervalMinutes):0.00}/month",
        };
    }

    private static string Describe(ProfileStatus p) => p.State switch
    {
        CredentialState.Halted => $"halted ({p.ErrorCode}) since {p.HaltedAtUtc?.ToLocalTime():g} — waiting for the credentials file to change",
        CredentialState.Validating => "credentials changed — verifying…",
        CredentialState.Missing => "not in the credentials or config file",
        CredentialState.SignInRequired => $"AWS SSO sign-in required ({p.ErrorCode}{(p.HaltedAtUtc is { } at ? $" at {at.ToLocalTime():g}" : "")}) — run aws sso login",
        CredentialState.Valid => $"ok (last success {p.LastSuccessUtc?.ToLocalTime():g})",
        _ => "not used yet",
    };
}
