using Skypeek.Core.Models;

namespace Skypeek.Core.Health;

/// <summary>Site-to-Site VPN, CodeBuild, CloudFormation and Redshift health (pure; no I/O).</summary>
public static partial class HealthRules
{
    /// <summary>A problem of a resource as a stable text (suppressible like EB causes), its level, detail and occurrence.</summary>
    public sealed record Cause(string Text, HealthLevel Level, string Detail, string? Instance = null, string? InstanceLabel = null);

    // ---------------- VPN ----------------

    /// <summary>
    /// One tunnel down is a warning (many sites run one tunnel by design, so it can be suppressed); every tunnel down
    /// means the site is cut off: critical.
    /// </summary>
    public static IReadOnlyList<Cause> VpnCauses(VpnConnectionSnapshot vpn)
    {
        if (vpn.State != "available" || vpn.Tunnels.Count == 0)
            return [];
        var down = vpn.Tunnels.Where(t => !t.IsUp).ToList();
        if (down.Count == vpn.Tunnels.Count)
            return [new Cause("All VPN tunnels are DOWN", HealthLevel.Critical, string.Join("; ", down.Select(t => t.Text)))];
        return down.Select(t => new Cause($"VPN tunnel {t.OutsideIp} is DOWN", HealthLevel.Warn, t.Text)).ToList();
    }

    public static (HealthLevel Level, List<string> Reasons) EvaluateVpn(VpnConnectionSnapshot vpn, Func<string, bool>? isSuppressed = null)
    {
        if (vpn.State == "pending")
            return (HealthLevel.Unknown, ["Being created"]);
        if (vpn.State is "deleting" or "deleted")
            return (HealthLevel.Ok, [$"Connection {vpn.State}"]);
        return FromCauses(VpnCauses(vpn), isSuppressed);
    }

    public static void ApplyCauseSuppression(VpnConnectionStatus vpn, long targetId, AppSettings settings)
    {
        vpn.CauseItems = Items(VpnCauses(vpn.Snapshot), targetId, vpn.Snapshot.Id, settings);
        var suppressed = vpn.CauseItems.Where(i => i.Suppressed).Select(i => i.Text).ToHashSet();
        (vpn.BaseLevel, vpn.BaseReasons) = EvaluateVpn(vpn.Snapshot, suppressed.Contains);
    }

    public static bool AlarmMatchesVpn(AlarmInfo alarm, VpnConnectionSnapshot vpn) =>
        alarm.Namespace == "AWS/VPN" && alarm.Dimensions.TryGetValue("VpnId", out var id) && id == vpn.Id;

    // ---------------- CodeBuild ----------------

    public const string BuildFailedCause = "Latest build failed";

    /// <summary>A failed (or timed out) latest finished build is critical, with or without a CloudWatch alarm.</summary>
    public static IReadOnlyList<Cause> CodeBuildCauses(CodeBuildProjectSnapshot project) =>
        project.LastCompleted is { IsFailure: true } run
            ? [new Cause(BuildFailedCause, HealthLevel.Critical, run.Summary, run.Id, $"build {run.NumberText}")]
            : [];

    public static (HealthLevel Level, List<string> Reasons) EvaluateCodeBuild(CodeBuildProjectSnapshot project, Func<string, bool>? isSuppressed = null)
    {
        var (level, reasons) = FromCauses(CodeBuildCauses(project), isSuppressed);
        if (project.NotReadYet)
            return (HealthLevel.Unknown, ["builds not read yet (read a few projects per poll)"]);
        if (project.LastCompleted is null)
            return (project.LatestBuild is null ? HealthLevel.Ok : HealthLevel.Unknown, reasons);
        return (level, reasons);
    }

    public static void ApplyCauseSuppression(CodeBuildStatus build, long targetId, AppSettings settings)
    {
        build.CauseItems = Items(CodeBuildCauses(build.Snapshot), targetId, build.Snapshot.Name, settings);
        var suppressed = build.CauseItems.Where(i => i.Suppressed).Select(i => i.Text).ToHashSet();
        (build.BaseLevel, build.BaseReasons) = EvaluateCodeBuild(build.Snapshot, suppressed.Contains);
    }

    public static bool AlarmMatchesCodeBuild(AlarmInfo alarm, CodeBuildProjectSnapshot project) =>
        alarm.Namespace == "AWS/CodeBuild" && alarm.Dimensions.TryGetValue("ProjectName", out var name) && name == project.Name;

    // ---------------- CloudFormation ----------------

    public static IReadOnlyList<Cause> StackCauses(StackSnapshot stack)
    {
        var level = StackRules.StatusLevel(stack.Status);
        if (level < HealthLevel.Warn)
            return [];
        var text = stack.Status switch
        {
            "ROLLBACK_COMPLETE" => "Stack creation failed (ROLLBACK_COMPLETE)",
            "UPDATE_ROLLBACK_COMPLETE" or "IMPORT_ROLLBACK_COMPLETE" => $"Last update failed and was rolled back ({stack.Status})",
            _ => $"Stack {stack.Status}",
        };
        var detail = string.Join(" — ", new[] { stack.FailedResource, stack.StatusReason }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        var when = stack.LastUpdated ?? stack.Created;
        return [new Cause(text, level, detail, stack.FailureInstance, when is { } w ? $"the failure of {w.ToLocalTime():g}" : "this failure")];
    }

    public static (HealthLevel Level, List<string> Reasons) EvaluateStack(StackSnapshot stack, Func<string, bool>? isSuppressed = null)
    {
        if (StackRules.StatusLevel(stack.Status) == HealthLevel.Unknown)
            return (HealthLevel.Unknown, [$"{stack.Status.ToLowerInvariant().Replace('_', ' ')}"]);
        return FromCauses(StackCauses(stack), isSuppressed);
    }

    public static void ApplyCauseSuppression(StackStatus stack, long targetId, AppSettings settings)
    {
        stack.CauseItems = Items(StackCauses(stack.Snapshot), targetId, stack.Snapshot.Name, settings);
        var suppressed = stack.CauseItems.Where(i => i.Suppressed).Select(i => i.Text).ToHashSet();
        (stack.BaseLevel, stack.BaseReasons) = EvaluateStack(stack.Snapshot, suppressed.Contains);
    }

    // ---------------- Redshift ----------------

    public static (HealthLevel Level, List<string> Reasons) EvaluateRedshift(RedshiftSnapshot r)
    {
        var status = r.Status.ToLowerInvariant();
        if (r.AvailabilityStatus is "Failed" || status is "hardware-failure" or "storage-full" or "incompatible-hsm" or "incompatible-network"
            or "incompatible-parameters" or "incompatible-restore" or "failed")
            return (HealthLevel.Critical, [$"Status {r.Status}{(r.AvailabilityStatus is { } a ? $" ({a})" : "")}"]);
        if (r.AvailabilityStatus is "Unavailable" && !r.IsPaused)
            return (HealthLevel.Warn, [$"Unavailable ({r.Status})"]);
        if (status is "available" or "paused")
            return (HealthLevel.Ok, []);
        return (HealthLevel.Unknown, [r.Status]);
    }

    public static bool AlarmMatchesRedshift(AlarmInfo alarm, RedshiftSnapshot r) =>
        alarm.Namespace == "AWS/Redshift" &&
        ((alarm.Dimensions.TryGetValue("ClusterIdentifier", out var id) && id == r.Id) ||
         (alarm.Dimensions.TryGetValue("Workgroup", out var wg) && wg == r.Id));

    // ---------------- shared ----------------

    private static (HealthLevel Level, List<string> Reasons) FromCauses(IReadOnlyList<Cause> causes, Func<string, bool>? isSuppressed)
    {
        var level = HealthLevel.Ok;
        var reasons = new List<string>();
        foreach (var cause in causes)
        {
            if (isSuppressed?.Invoke(cause.Text) == true)
                continue;
            level = Max(level, cause.Level);
            reasons.Add(cause.Detail.Length > 0 ? $"{cause.Text}: {cause.Detail}" : cause.Text);
        }
        return (level, reasons);
    }

    private static List<CauseItem> Items(IReadOnlyList<Cause> causes, long targetId, string name, AppSettings settings) =>
        causes.Select(c => MatchCause(c.Text, targetId, name, settings, c.Instance) is { } rule
                ? new CauseItem(c.Text, true, rule.Pattern, c.Instance, c.InstanceLabel)
                : new CauseItem(c.Text, false, null, c.Instance, c.InstanceLabel))
            .ToList();
}
