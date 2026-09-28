using System.Globalization;
using System.Text;
using System.Text.Json;
using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

/// <summary>What the exported lines are and where they came from.</summary>
public sealed record LogExportContext(
    Target Target,
    string? AccountId,
    ResourceStatus? Resource,
    string Source,
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? Filter);

/// <summary>
/// Formats shown log lines for AI analysis: a short instruction, the resource's current health state (causes, events,
/// deployments) and the lines oldest-first, so a model has everything it needs in one paste or file.
/// </summary>
public static class LogExport
{
    private const string Instruction =
        "Analyze these AWS logs. Using the current state below as context, identify the most likely root cause of the problem, " +
        "quote the log lines that support it, and suggest how to fix it. Say if the logs are not enough to tell.";

    public static string ToMarkdown(LogExportContext ctx, IReadOnlyList<LogEvent> lines, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Skypeek log export").AppendLine();
        sb.AppendLine($"> {Instruction}").AppendLine();

        sb.AppendLine("## Context");
        foreach (var (key, value) in ContextFields(ctx, lines, nowUtc))
            sb.AppendLine($"- **{key}:** {value}");
        sb.AppendLine();

        var state = StateLines(ctx.Resource);
        if (state.Count > 0)
        {
            sb.AppendLine("## Current state");
            foreach (var line in state)
                sb.AppendLine(line);
            sb.AppendLine();
        }

        sb.AppendLine($"## Log lines ({lines.Count}, oldest first, times in UTC)");
        sb.AppendLine("```");
        foreach (var l in lines.OrderBy(l => l.Timestamp))
            sb.Append(l.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
              .Append(l.Stream).Append(' ').AppendLine(l.Message);
        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>First line is a context object, then one JSON object per log line.</summary>
    public static string ToJsonLines(LogExportContext ctx, IReadOnlyList<LogEvent> lines, DateTime nowUtc)
    {
        var sb = new StringBuilder();
        var context = new Dictionary<string, object?>
        {
            ["type"] = "context",
            ["instruction"] = Instruction,
        };
        foreach (var (key, value) in ContextFields(ctx, lines, nowUtc))
            context[key.ToLowerInvariant().Replace(' ', '_')] = value;
        context["current_state"] = StateLines(ctx.Resource).Select(s => s.TrimStart('-', ' ', '#')).ToList();
        sb.AppendLine(JsonSerializer.Serialize(context));
        foreach (var l in lines.OrderBy(l => l.Timestamp))
            sb.AppendLine(JsonSerializer.Serialize(new { type = "log", ts = l.Timestamp.ToString("O", CultureInfo.InvariantCulture), stream = l.Stream, message = l.Message }));
        return sb.ToString();
    }

    public static string SuggestFileName(LogExportContext ctx, DateTime nowUtc, string extension)
    {
        var name = ctx.Resource switch
        {
            EcsServiceStatus s => s.Snapshot.ServiceName,
            EbEnvironmentStatus e => e.Snapshot.EnvironmentName,
            RdsInstanceStatus db => db.Snapshot.Identifier,
            _ => "logs",
        };
        var safe = new string($"{ctx.Target.DisplayName}-{name}".Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return $"{safe}-{nowUtc.ToLocalTime():yyyyMMdd-HHmm}.{extension}";
    }

    private static IEnumerable<(string Key, string Value)> ContextFields(LogExportContext ctx, IReadOnlyList<LogEvent> lines, DateTime nowUtc)
    {
        yield return ("Resource", ctx.Resource switch
        {
            EcsServiceStatus s => $"ECS service {s.Snapshot.ServiceName} (cluster {s.Snapshot.ClusterName})",
            EbEnvironmentStatus e => $"Elastic Beanstalk environment {e.Snapshot.EnvironmentName} (application {e.Snapshot.ApplicationName})",
            RdsInstanceStatus db => $"RDS {db.Snapshot.Role} {db.Snapshot.Identifier} ({db.Snapshot.EngineText}, {db.Snapshot.InstanceClass})",
            _ => "unknown",
        });
        yield return ("Target", $"{ctx.Target.DisplayName} · region {ctx.Target.Region}{(ctx.AccountId is null ? "" : $" · account {ctx.AccountId}")}");
        yield return ("Source", ctx.Source);
        if (ctx.FromUtc is { } from && ctx.ToUtc is { } to)
            yield return ("Time range", $"{from:yyyy-MM-dd HH:mm} – {to:yyyy-MM-dd HH:mm} UTC");
        else if (lines.Count > 0)
            yield return ("Time range", $"{lines.Min(l => l.Timestamp):yyyy-MM-dd HH:mm} – {lines.Max(l => l.Timestamp):yyyy-MM-dd HH:mm} UTC");
        if (!string.IsNullOrWhiteSpace(ctx.Filter))
            yield return ("Filter pattern", ctx.Filter);
        yield return ("Exported", $"{nowUtc:yyyy-MM-dd HH:mm} UTC");
    }

    private static List<string> StateLines(ResourceStatus? resource)
    {
        var lines = new List<string>();
        if (resource is null)
            return lines;

        lines.Add($"- **Health level:** {resource.Level}{(resource.Reasons.Count > 0 ? $" — {resource.ReasonText}" : "")}");
        if (resource.Cpu?.Current is not null)
            lines.Add($"- **CPU:** {resource.Cpu!.Display}");
        if (resource.Memory?.Current is not null && resource is not RdsInstanceStatus)
            lines.Add($"- **Memory:** {resource.Memory!.Display}");
        foreach (var alarm in resource.Alarms.Where(a => a.IsActive || a.IsRecent))
            lines.Add($"- **Alarm {alarm.Name}:** {alarm.Badge} — {alarm.StateReason}");

        switch (resource)
        {
            case EbEnvironmentStatus eb:
                var env = eb.Snapshot;
                lines.Add($"- **Status / health:** {env.Status} · {env.Health} ({env.HealthStatus}) · version {env.VersionLabel} · updated {env.DateUpdated:u}");
                foreach (var cause in env.Causes)
                    lines.Add($"- **Cause:** {cause}");
                if (env.RequestSummary is { } req)
                    lines.Add($"- **Requests:** {req}");
                foreach (var inst in env.UnhealthyInstances)
                    lines.Add($"- **Instance {inst.InstanceId}:** {inst.HealthStatus} · deployment {inst.DeploymentStatus} {inst.VersionLabel} — {inst.CauseText}");
                if (eb.RecentEvents.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Recent Elastic Beanstalk events (newest first)");
                    foreach (var ev in eb.RecentEvents.Take(20))
                        lines.Add($"- {ev.Date:u} {ev.Severity}: {ev.Message}");
                }
                break;

            case RdsInstanceStatus db:
                var snap = db.Snapshot;
                lines.Add($"- **Status:** {snap.Status}{(snap.MultiAz ? " · Multi-AZ" : "")} · parameter group {snap.ParameterGroup} ({snap.ParameterApplyStatus})");
                lines.Add($"- **Connections:** {db.ConnectionsText}");
                lines.Add($"- **Storage:** {db.StorageText} · memory {db.FreeableMemoryText}");
                if (snap.ReplicaSource is { } source)
                    lines.Add($"- **Replica of:** {source}{(db.ReplicaLagText is { } lag ? $" · lag {lag}" : "")}{(snap.ReplicationState is { } state ? $" · {state}" : "")}");
                if (snap.PendingChanges.Count > 0)
                    lines.Add($"- **Pending changes:** {string.Join(", ", snap.PendingChanges)}");
                if (db.RecentEvents.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Recent RDS events (newest first)");
                    foreach (var ev in db.RecentEvents.Take(20))
                        lines.Add($"- {ev.Date:u} {ev.Category}: {ev.Message}");
                }
                break;

            case EcsServiceStatus ecs:
                var svc = ecs.Snapshot;
                lines.Add($"- **Tasks:** {svc.Running} running / {svc.Desired} desired / {svc.Pending} pending");
                foreach (var d in svc.Deployments)
                    lines.Add($"- **Deployment {d.Status}:** {d.RolloutState} · {d.TaskDefinition} · running {d.Running}, failed {d.Failed} · started {d.CreatedAt:u}{(d.RolloutStateReason is null ? "" : $" — {d.RolloutStateReason}")}");
                if (ecs.StoppedReasons.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Recently stopped tasks");
                    foreach (var reason in ecs.StoppedReasons)
                        lines.Add($"- {reason}");
                }
                if (svc.Events.Count > 0)
                {
                    lines.Add("");
                    lines.Add("### Recent ECS service events");
                    foreach (var ev in svc.Events)
                        lines.Add($"- {ev}");
                }
                break;
        }
        return lines;
    }
}
