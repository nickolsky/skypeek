using Skypeek.Core.Health;
using Skypeek.Core.Models;

namespace Skypeek.Tests;

public class UsageAnalysisTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<MetricPoint> Hours(int count, Func<int, double> value) =>
        Enumerable.Range(0, count).Select(i => new MetricPoint(Start.AddHours(i), value(i))).ToList();

    private static UsageSeries Series(string metric, double average, double peak, int hours = 720, int fullHours = 0) =>
        UsageAnalysis.Summarize("node", metric, Hours(hours, _ => average), Hours(hours, _ => average / 2),
            Hours(hours, i => i < fullHours ? 100 : peak));

    [Fact]
    public void Summary_statistics()
    {
        var s = UsageAnalysis.Summarize("i-1", "CPU", Hours(100, i => i), Hours(100, i => i / 2.0), Hours(100, i => i == 99 ? 100 : i + 1));
        Assert.Equal(49.5, s.Average);
        Assert.Equal(0, s.Minimum);
        Assert.Equal(100, s.Maximum);
        Assert.Equal(94.05, s.P95!.Value, precision: 2);
        Assert.Equal(100, s.Hours);
        Assert.Equal(2, s.HoursAtFull); // peaks of 99 and 100 (≥ 99% counts as full)
        Assert.Equal(11, s.HoursAbove90); // peaks 90..100
    }

    [Fact]
    public void Frequent_full_peaks_mean_under_provisioned()
    {
        // ~100% in 36 of 720 hours (5%) although the typical load is modest.
        var (verdict, reasons) = UsageAnalysis.Recommend([Series("CPU", 40, 70, fullHours: 36), Series("Memory", 30, 40)], "ec2");
        Assert.Equal(Provisioning.Under, verdict);
        Assert.Contains(reasons, r => r.Contains("hit ~100% in 36 of 720 h"));
    }

    [Fact]
    public void High_typical_load_means_under_provisioned() =>
        Assert.Equal(Provisioning.Under, UsageAnalysis.Recommend([Series("CPU", 85, 95)], "ecs").Verdict);

    [Fact]
    public void Low_cpu_and_memory_mean_over_provisioned() =>
        Assert.Equal(Provisioning.Over, UsageAnalysis.Recommend([Series("CPU", 10, 45), Series("Memory", 20, 35)], "rds").Verdict);

    [Fact]
    public void Memory_near_half_is_not_called_over_provisioned() =>
        Assert.Equal(Provisioning.Right, UsageAnalysis.Recommend([Series("CPU", 10, 45), Series("Memory", 25, 55)], "ec2").Verdict);

    [Fact]
    public void Short_history_gives_no_verdict() =>
        Assert.Equal(Provisioning.Unknown, UsageAnalysis.Recommend([Series("CPU", 95, 100, hours: 10)], "ec2").Verdict);
}
