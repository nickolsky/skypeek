namespace Skypeek.Core.Models;

/// <summary>
/// A billing meter Skypeek's own AWS calls count toward. Prices are AWS's public list prices (us-east-1); free tiers
/// are per account and month and shared with everything else in the account.
/// </summary>
public sealed record ApiMeter(string Id, string Name, double UsdPerUnit, string UnitName, long FreePerMonth = 0, string? Note = null);

/// <summary>Calls and billed units per month, profile, region and meter (kept apart from the request log's retention).</summary>
public sealed record ApiUsageRow(string Month, string? Profile, string? Region, string Meter, long Calls, long Units);

public static class PaidApi
{
    public static readonly ApiMeter Metrics = new("cw-metrics", "CloudWatch metrics (GetMetricData)", 0.01 / 1000, "metric");
    public static readonly ApiMeter CloudWatchRequests = new("cw-requests", "CloudWatch requests (alarms, metric lists, history)", 0.01 / 1000, "request", 1_000_000,
        "free up to 1 million requests a month per account");
    public static readonly ApiMeter CostExplorer = new("ce", "Cost Explorer", 0.01, "request");
    public static readonly ApiMeter SecretsManager = new("secrets", "Secrets Manager API", 0.05 / 10_000, "request");
    public static readonly ApiMeter Kms = new("kms", "KMS (encrypting or decrypting values)", 0.03 / 10_000, "request", 20_000,
        "free up to 20,000 requests a month per account");
    public static readonly ApiMeter Reachability = new("reach", "Reachability Analyzer", 0.10, "analysis");

    public static readonly IReadOnlyList<ApiMeter> All = [Metrics, CloudWatchRequests, CostExplorer, SecretsManager, Kms, Reachability];

    public static ApiMeter? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// The meters a call counts toward, with its units (metrics for GetMetricData, else 1). Calls to free services
    /// (EC2, ECS, RDS, … Describe/List calls, the Price List, standard Parameter Store) count toward nothing.
    /// </summary>
    public static IEnumerable<(ApiMeter Meter, long Units)> Classify(string service, string operation, string parameters, int units)
    {
        switch (service)
        {
            case "cloudwatch" when operation == "GetMetricData":
                yield return (Metrics, Math.Max(units, 0));
                break;
            case "cloudwatch":
                yield return (CloudWatchRequests, 1);
                break;
            case "ce":
                yield return (CostExplorer, 1);
                break;
            case "secretsmanager":
                yield return (SecretsManager, 1);
                // Reading or writing a value uses the secret's KMS key.
                if (operation is "GetSecretValue" or "PutSecretValue" or "CreateSecret")
                    yield return (Kms, 1);
                break;
            case "ssm" when operation == "GetParameter" && parameters.Contains("WithDecryption=True", StringComparison.Ordinal):
            case "ssm" when operation == "PutParameter" && parameters.Contains("Type=SecureString", StringComparison.Ordinal):
                yield return (Kms, 1);
                break;
            case "ec2" when operation == "StartNetworkInsightsAnalysis":
                yield return (Reachability, 1);
                break;
        }
    }

    /// <summary>Cost of a month's usage of one meter in one account, after its free tier.</summary>
    public static double Cost(ApiMeter meter, long units) => Math.Max(0, units - meter.FreePerMonth) * meter.UsdPerUnit;

    public static string MonthOf(DateTime utc) => utc.ToString("yyyy-MM");
}

/// <summary>One line of the "cost of running Skypeek" estimate for a target.</summary>
public sealed record CostEstimateLine(string Item, string Usage, double MonthlyUsd, string? Note = null)
{
    public bool IsFree => MonthlyUsd < 0.005;
    public string CostText => IsFree ? "free" : $"${MonthlyUsd:0.00}";
}

public sealed record TargetRunningCost(Target Target, IReadOnlyList<CostEstimateLine> Lines)
{
    public double MonthlyUsd => Lines.Sum(l => l.MonthlyUsd);
}

/// <summary>What the configured refreshes cost per month (list prices), before anything is used on demand.</summary>
public static class RunningCost
{
    private const double MinutesPerMonth = 30 * 24 * 60;

    private static double PollsPerMonth(int intervalMinutes) => intervalMinutes <= 0 ? 0 : MinutesPerMonth / intervalMinutes;

    /// <param name="metricsPerPoll">Metrics one metrics poll requests (from the last poll; 0 before the first).</param>
    /// <param name="alarms">CloudWatch alarms seen in the target (pages of 100 per poll).</param>
    public static TargetRunningCost Estimate(Target t, int metricsPerPoll, int secrets, int alarms)
    {
        var lines = new List<CostEstimateLine>();
        if (!t.Enabled)
            return new TargetRunningCost(t, [new CostEstimateLine("Target disabled", "no refreshes", 0)]);

        var metricsPolls = t.HealthEnabled ? PollsPerMonth(t.MetricsIntervalMinutes) : 0;
        var metrics = metricsPerPoll * metricsPolls;
        lines.Add(new CostEstimateLine(PaidApi.Metrics.Name,
            metricsPolls == 0 ? "metrics poll off" : $"{metricsPerPoll} metrics every {t.MetricsIntervalMinutes} min ≈ {metrics:N0} a month",
            metrics * PaidApi.Metrics.UsdPerUnit,
            metricsPerPoll == 0 && metricsPolls > 0 ? "known after the first metrics poll" : "hidden resources are not queried"));

        var healthPolls = t.HealthEnabled ? PollsPerMonth(t.HealthIntervalMinutes) : 0;
        // Per health poll: the alarms (100 per page) and their recent history; the CloudWatch agent's metric list hourly.
        var requests = healthPolls * (1 + Math.Ceiling(Math.Max(alarms, 1) / 100.0)) + (t.Ec2Enabled || t.EbEnabled ? 720 : 0);
        lines.Add(new CostEstimateLine(PaidApi.CloudWatchRequests.Name, $"≈ {requests:N0} requests a month", 0,
            $"free within 1 million a month per account (beyond: ${requests * PaidApi.CloudWatchRequests.UsdPerUnit:0.00})"));

        if (t.CostExplorerEnabled)
            lines.Add(new CostEstimateLine(PaidApi.CostExplorer.Name, "≈ 2 requests twice a day", 2 * 2 * 30 * PaidApi.CostExplorer.UsdPerUnit,
                "turn off in Settings → Accounts & regions → Costs"));

        if (t.SecretsEnabled && t.CatalogIntervalMinutes > 0)
        {
            var calls = Math.Max(1, Math.Ceiling(secrets / 100.0)) * PollsPerMonth(t.CatalogIntervalMinutes);
            lines.Add(new CostEstimateLine(PaidApi.SecretsManager.Name, $"list of {secrets} secret(s), {calls:N0} requests a month", calls * PaidApi.SecretsManager.UsdPerUnit,
                "revealing a value adds $0.05 per 10,000 plus KMS"));
        }

        lines.Add(new CostEstimateLine("Everything else", "EC2, VPC, ECS, EB, RDS, ElastiCache, load balancers, VPN, CodeBuild, CloudFormation, Redshift, Parameter Store, Price List", 0,
            "these reads are free"));
        return new TargetRunningCost(t, lines);
    }
}

public sealed record MeterUsage(ApiMeter Meter, long Calls, long Units, double Usd)
{
    public string UsageText => Meter == PaidApi.Metrics ? $"{Units:N0} metrics in {Calls:N0} calls" : $"{Units:N0} {Meter.UnitName}(s)";
    public string CostText => Usd < 0.005 ? (Meter.FreePerMonth > 0 ? "free tier" : "< $0.01") : $"${Usd:0.00}";
}

public sealed record MonthCost(string Month, double Usd)
{
    public string Text => $"{DateTime.ParseExact(Month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture):MMMM yyyy}: ${Usd:0.00}";
}

/// <summary>What Skypeek's own AWS calls cost: counted this month, projected to month end, and estimated from the settings.</summary>
public sealed class RunningCostReport
{
    public double MonthToDateUsd { get; init; }
    public double ProjectedUsd { get; init; }
    public double EstimatedMonthlyUsd { get; init; }
    public DateTime CountingSinceUtc { get; init; }
    public IReadOnlyList<MeterUsage> ThisMonth { get; init; } = [];
    public IReadOnlyList<TargetRunningCost> Targets { get; init; } = [];
    public IReadOnlyList<MonthCost> PreviousMonths { get; init; } = [];

    /// <param name="accountOf">The account of a profile (free tiers apply per account); unknown profiles count as their own.</param>
    /// <param name="countingStartedUtc">The first paid call ever counted: in the month counting began, the counts cover only the time since.</param>
    public static RunningCostReport Build(IReadOnlyList<ApiUsageRow> usage, IReadOnlyList<TargetRunningCost> estimates,
        Func<string?, string?> accountOf, DateTime nowUtc, DateTime? countingStartedUtc)
    {
        var month = PaidApi.MonthOf(nowUtc);
        double CostOf(IEnumerable<ApiUsageRow> rows) => rows
            .GroupBy(r => (Account: accountOf(r.Profile) ?? r.Profile ?? "", r.Meter))
            .Sum(g => PaidApi.Find(g.Key.Meter) is { } meter ? PaidApi.Cost(meter, g.Sum(r => r.Units)) : 0);

        var current = usage.Where(r => r.Month == month).ToList();
        var mtd = CostOf(current);
        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var since = countingStartedUtc is { } first && first > monthStart ? first : monthStart;
        var elapsed = Math.Max((nowUtc - since).TotalDays, 1.0 / 24);
        var remaining = (monthStart.AddMonths(1) - nowUtc).TotalDays;
        return new RunningCostReport
        {
            MonthToDateUsd = mtd,
            // What was spent, plus the rest of the month at the rate seen so far.
            ProjectedUsd = mtd + mtd / elapsed * remaining,
            EstimatedMonthlyUsd = estimates.Sum(e => e.MonthlyUsd),
            CountingSinceUtc = since,
            ThisMonth = PaidApi.All.Select(m =>
            {
                var rows = current.Where(r => r.Meter == m.Id).ToList();
                return new MeterUsage(m, rows.Sum(r => r.Calls), rows.Sum(r => r.Units), CostOf(rows));
            }).Where(m => m.Calls > 0).ToList(),
            Targets = estimates,
            PreviousMonths = usage.Where(r => string.CompareOrdinal(r.Month, month) < 0).GroupBy(r => r.Month)
                .OrderByDescending(g => g.Key).Select(g => new MonthCost(g.Key, CostOf(g))).ToList(),
        };
    }
}
