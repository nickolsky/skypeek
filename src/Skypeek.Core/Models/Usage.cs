namespace Skypeek.Core.Models;

public enum Provisioning
{
    Unknown,
    Under,
    Right,
    Over,
}

/// <summary>Hourly statistics of one metric (CPU or memory, in %) of one node over the analysis window.</summary>
/// <param name="Scope">The node: instance id, cache node, or the service/database itself.</param>
/// <param name="Hours">Hours with data (a node started 3 days ago has ~72).</param>
/// <param name="HoursAbove90">Hours whose peak reached 90% or more.</param>
/// <param name="HoursAtFull">Hours whose peak reached ~100% (99% or more).</param>
public sealed record UsageSeries(string Scope, string Metric, double? Average, double? Minimum, double? Maximum, double? P95,
    int Hours, int HoursAbove90, int HoursAtFull, bool Estimated = false)
{
    public bool HasData => Hours > 0;
    public string Label => $"{Metric}{(Estimated ? " ≈" : "")}";
    public string StatsText => !HasData ? "no data"
        : $"avg {Average:0}% · min {Minimum:0}% · max {Maximum:0}% · p95 {P95:0}%";
    public string PeaksText => !HasData ? ""
        : HoursAtFull > 0 ? $"hit ~100% in {HoursAtFull} of {Hours} h ({Share(HoursAtFull)}) · ≥90% in {HoursAbove90} h"
        : HoursAbove90 > 0 ? $"≥90% in {HoursAbove90} of {Hours} h ({Share(HoursAbove90)}) · never ~100%"
        : $"never above 90% in {Hours} h";
    /// <summary>For the bar: typical (p95) usage colour-coded by how hot it runs.</summary>
    public HealthLevel Level => !HasData ? HealthLevel.Unknown
        : HoursAtFull * 100.0 / Hours >= 2 || P95 >= 80 ? HealthLevel.Critical
        : HoursAbove90 > 0 || P95 >= 60 ? HealthLevel.Warn
        : HealthLevel.Ok;

    private string Share(int hours) => $"{hours * 100.0 / Hours:0.#}%";
}

public sealed record UsageReport(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<UsageSeries> Series, Provisioning Verdict, IReadOnlyList<string> Reasons, DateTime CreatedUtc)
{
    public string VerdictText => Verdict switch
    {
        Provisioning.Under => "Under-provisioned",
        Provisioning.Over => "Over-provisioned",
        Provisioning.Right => "Right-sized",
        _ => "Not enough data",
    };
    public HealthLevel VerdictLevel => Verdict switch
    {
        Provisioning.Under => HealthLevel.Critical,
        Provisioning.Over => HealthLevel.Warn,
        Provisioning.Right => HealthLevel.Ok,
        _ => HealthLevel.Unknown,
    };
    public string RangeText => $"{FromUtc.ToLocalTime():d} – {ToUtc.ToLocalTime():d} · hourly CloudWatch data · analyzed {CreatedUtc.ToLocalTime():t}";
    public string ReasonText => string.Join(Environment.NewLine, Reasons);
}
