using Skypeek.Core.Credentials;
using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

public sealed record TrayStatus(bool IsRed, IReadOnlyList<Problem> Problems)
{
    public static readonly TrayStatus Empty = new(false, []);

    public int Count => Problems.Count;
}

/// <summary>Aggregates everything "not good" into one status that drives the tray icon.</summary>
public static class TrayStatusCalculator
{
    public static TrayStatus Compute(
        IReadOnlyList<Target> targets,
        IReadOnlyList<TargetHealth> health,
        IReadOnlyList<ProfileStatus> profiles,
        Func<long, JobKind, JobState?> jobState,
        bool warningsTurnIconRed)
    {
        var problems = new List<Problem>();
        var byId = targets.Where(t => t.Enabled).ToDictionary(t => t.Id);

        foreach (var h in health)
        {
            if (!byId.TryGetValue(h.TargetId, out var target))
                continue;
            foreach (var r in h.AllResources.Where(r => r.Level >= HealthLevel.Warn))
                problems.Add(new Problem(target.Id, target.DisplayName, r.DisplayName, r.ReasonText, r.Level, r.ConsoleUrl));
            foreach (var alarm in h.OtherAlarms.Where(a => a.CountsAsProblem))
                problems.Add(new Problem(target.Id, target.DisplayName, $"alarm {alarm.Name}", alarm.StateReason ?? "in ALARM", HealthLevel.Critical,
                    $"https://{target.Region}.console.aws.amazon.com/cloudwatch/home?region={target.Region}#alarmsV2:alarm/{Uri.EscapeDataString(alarm.Name)}"));
        }

        var usedProfiles = byId.Values.Select(t => t.ProfileName).ToHashSet();
        foreach (var p in profiles.Where(p => usedProfiles.Contains(p.Profile)))
        {
            if (p.State is CredentialState.Halted or CredentialState.Validating)
                problems.Add(new Problem(0, p.Profile, "credentials", $"halted ({p.ErrorCode}) — refresh the credentials file", HealthLevel.Critical));
            else if (p.State == CredentialState.Missing)
                problems.Add(new Problem(0, p.Profile, "credentials", "profile missing from the credentials file", HealthLevel.Critical));
        }

        var halted = profiles.Where(p => p.State is CredentialState.Halted or CredentialState.Validating or CredentialState.Missing)
            .Select(p => p.Profile).ToHashSet();
        foreach (var target in byId.Values.Where(t => !halted.Contains(t.ProfileName)))
            foreach (var kind in Enum.GetValues<JobKind>())
                if (jobState(target.Id, kind) is { LastFailed: true } s)
                    problems.Add(new Problem(target.Id, target.DisplayName, $"{kind.ToString().ToLowerInvariant()} sync", s.LastError ?? "failed", HealthLevel.Warn));

        var ordered = problems.OrderByDescending(p => p.Level).ThenBy(p => p.TargetName).ThenBy(p => p.Resource).ToList();
        var red = ordered.Any(p => p.Level == HealthLevel.Critical || (warningsTurnIconRed && p.Level == HealthLevel.Warn));
        return new TrayStatus(red, ordered);
    }
}
