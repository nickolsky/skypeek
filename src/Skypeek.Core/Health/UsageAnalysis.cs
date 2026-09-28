using Skypeek.Core.Models;

namespace Skypeek.Core.Health;

/// <summary>
/// Turns hourly CPU/memory statistics into usage figures and a sizing recommendation. Pure functions, so the
/// thresholds below are easy to test and change.
/// </summary>
public static class UsageAnalysis
{
    /// <summary>A peak of 99% or more counts as "hit 100%".</summary>
    public const double FullAt = 99;
    public const double HotAt = 90;
    /// <summary>Under-provisioned when peaks reach ~100% in at least this share of hours…</summary>
    public const double UnderFullShare = 2;
    /// <summary>…or when typical load (p95 of hourly averages) is at least this.</summary>
    public const double UnderP95 = 80;
    /// <summary>Over-provisioned when typical load stays below this…</summary>
    public const double OverP95 = 30;
    /// <summary>…and the highest peak stays below this (CPU) / <see cref="OverMaxMemory"/> (memory).</summary>
    public const double OverMaxCpu = 60;
    public const double OverMaxMemory = 50;
    /// <summary>Less history than this and no verdict is given.</summary>
    public const int MinHours = 24;

    /// <param name="averages">Hourly Average values.</param>
    /// <param name="minimums">Hourly Minimum values.</param>
    /// <param name="maximums">Hourly Maximum values.</param>
    public static UsageSeries Summarize(string scope, string metric, IReadOnlyList<MetricPoint> averages, IReadOnlyList<MetricPoint> minimums,
        IReadOnlyList<MetricPoint> maximums, bool estimated = false)
    {
        if (averages.Count == 0)
            return new UsageSeries(scope, metric, null, null, null, null, 0, 0, 0, estimated);
        var peaks = maximums.Count > 0 ? maximums : averages;
        return new UsageSeries(scope, metric,
            Average: averages.Average(p => p.Value),
            Minimum: (minimums.Count > 0 ? minimums : averages).Min(p => p.Value),
            Maximum: peaks.Max(p => p.Value),
            P95: Percentile(averages.Select(p => p.Value), 95),
            Hours: averages.Count,
            HoursAbove90: peaks.Count(p => p.Value >= HotAt),
            HoursAtFull: peaks.Count(p => p.Value >= FullAt),
            Estimated: estimated);
    }

    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
            return double.NaN;
        var rank = percentile / 100 * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    /// <summary>
    /// Under-provisioned if any node's CPU or memory runs hot; over-provisioned if every metric with data stays low;
    /// otherwise right-sized. Memory is judged more cautiously than CPU (running out of memory kills the process).
    /// </summary>
    public static (Provisioning Verdict, List<string> Reasons) Recommend(IReadOnlyList<UsageSeries> series, string resourceKind)
    {
        var withData = series.Where(s => s.Hours >= MinHours).ToList();
        if (withData.Count == 0)
            return (Provisioning.Unknown, [$"Less than {MinHours} hours of CloudWatch data, so no recommendation yet."]);

        var hot = withData
            .Where(s => s.HoursAtFull * 100.0 / s.Hours >= UnderFullShare || s.P95 >= UnderP95)
            .ToList();
        if (hot.Count > 0)
        {
            var reasons = hot.Select(s => $"{s.Scope} {s.Metric}: p95 {s.P95:0}%, {s.PeaksText}.").ToList();
            reasons.Add(resourceKind switch
            {
                "ecs" => "Give the tasks more CPU/memory, or run more tasks (raise desired count or the autoscaling target).",
                "rds" => "Consider a larger instance class, or move read traffic to a replica.",
                "cache" => "Consider a larger node type, or more shards/replicas.",
                _ => "Consider a larger instance type, or more instances (autoscaling).",
            });
            return (Provisioning.Under, reasons);
        }

        bool Low(UsageSeries s) => s.P95 < OverP95 && s.Maximum < (IsMemory(s) ? OverMaxMemory : OverMaxCpu);
        if (withData.All(Low))
        {
            var reasons = withData.Select(s => $"{s.Scope} {s.Metric}: p95 {s.P95:0}%, max {s.Maximum:0}%.").ToList();
            reasons.Add(resourceKind switch
            {
                "ecs" => "The tasks could run with less CPU/memory, or with fewer tasks.",
                "rds" => "A smaller instance class would likely be enough (check IOPS and connections first).",
                "cache" => "A smaller node type would likely be enough (keep memory headroom for growth).",
                _ => "A smaller instance type, or fewer instances, would likely be enough.",
            });
            return (Provisioning.Over, reasons);
        }

        var busiest = withData.OrderByDescending(s => s.P95).First();
        return (Provisioning.Right, [$"Busiest: {busiest.Scope} {busiest.Metric} p95 {busiest.P95:0}%, max {busiest.Maximum:0}%. Peaks rarely reach 100% and typical load leaves headroom."]);
    }

    private static bool IsMemory(UsageSeries s) => s.Metric.StartsWith("Mem", StringComparison.OrdinalIgnoreCase);
}
