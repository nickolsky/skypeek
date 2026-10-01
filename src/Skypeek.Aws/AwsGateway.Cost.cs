using System.Globalization;
using Amazon.CostExplorer;
using Amazon.Pricing;
using Amazon.Runtime;
using Skypeek.Core.Models;
using Ce = Amazon.CostExplorer.Model;
using Pricing = Amazon.Pricing.Model;

namespace Skypeek.Aws;

/// <summary>Prices (AWS Price List) and billed costs (Cost Explorer); both are reads, served from us-east-1.</summary>
public sealed partial class AwsGateway
{
    /// <summary>Services whose per-resource costs are matched to dashboard resources.</summary>
    private static readonly List<string> ResourceCostServices =
    [
        "Amazon Elastic Compute Cloud - Compute",
        "Amazon Relational Database Service",
        "Amazon ElastiCache",
        "Amazon Elastic Load Balancing",
    ];

    public Task<IReadOnlyList<PriceItem>> GetPricesAsync(Target target, PriceQuery query, CancellationToken ct) =>
        Call<IReadOnlyList<PriceItem>>(target, "pricing:GetProducts", async c =>
        {
            var items = new List<PriceItem>();
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.Pricing.GetProductsAsync(new Pricing.GetProductsRequest
                {
                    ServiceCode = query.ServiceCode,
                    FormatVersion = "aws_v1",
                    // "field~" matches part of the value (usage types carry a region prefix, e.g. USE1-VpcEndpoint-Hours).
                    Filters = query.Filters.Select(f => f.Key.EndsWith('~')
                        ? new Pricing.Filter { Type = FilterType.CONTAINS, Field = f.Key[..^1], Value = f.Value }
                        : new Pricing.Filter { Type = FilterType.TERM_MATCH, Field = f.Key, Value = f.Value }).ToList(),
                    MaxResults = 100,
                    NextToken = token,
                }, ct);
                foreach (var json in resp.PriceList ?? [])
                    items.AddRange(CostRules.ParseProduct(json));
                token = resp.NextToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 10);
            return items;
        });

    public Task<ActualCosts> GetActualCostsAsync(Target target, CancellationToken ct) =>
        Call(target, "ce:GetCostAndUsage", async c =>
        {
            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var lastMonthStart = monthStart.AddMonths(-1);
            // End is exclusive; on the first of the month there is no month-to-date yet.
            var end = today > monthStart ? today : monthStart;
            var region = new Ce.Expression { Dimensions = new Ce.DimensionValues { Key = Dimension.REGION, Values = [target.Region] } };

            var byService = new Dictionary<string, (double Mtd, double Last)>();
            double mtd = 0, last = 0;
            var estimated = false;
            string? token = null;
            var pages = 0;
            do
            {
                var resp = await c.CostExplorer.GetCostAndUsageAsync(new Ce.GetCostAndUsageRequest
                {
                    TimePeriod = new Ce.DateInterval { Start = Day(lastMonthStart), End = Day(end) },
                    Granularity = Granularity.MONTHLY,
                    Metrics = ["UnblendedCost"],
                    GroupBy = [new Ce.GroupDefinition { Type = GroupDefinitionType.DIMENSION, Key = "SERVICE" }],
                    Filter = region,
                    NextPageToken = token,
                }, ct);
                foreach (var period in resp.ResultsByTime ?? [])
                {
                    var isCurrent = period.TimePeriod?.Start == Day(monthStart);
                    estimated |= isCurrent && period.Estimated == true;
                    foreach (var group in period.Groups ?? [])
                    {
                        var service = group.Keys?.FirstOrDefault() ?? "?";
                        var amount = Amount(group.Metrics);
                        var (m, l) = byService.GetValueOrDefault(service);
                        byService[service] = isCurrent ? (m + amount, l) : (m, l + amount);
                        if (isCurrent) mtd += amount; else last += amount;
                    }
                }
                token = resp.NextPageToken;
            } while (!string.IsNullOrEmpty(token) && ++pages < 5);

            // Per-resource costs exist only when "resource-level data" is enabled in Billing, and only for 14 days.
            var resources = new Dictionary<string, double>();
            string? note = null;
            try
            {
                token = null;
                pages = 0;
                do
                {
                    var resp = await c.CostExplorer.GetCostAndUsageWithResourcesAsync(new Ce.GetCostAndUsageWithResourcesRequest
                    {
                        TimePeriod = new Ce.DateInterval { Start = Day(today.AddDays(-13)), End = Day(today) },
                        Granularity = Granularity.DAILY,
                        Metrics = ["UnblendedCost"],
                        GroupBy = [new Ce.GroupDefinition { Type = GroupDefinitionType.DIMENSION, Key = "RESOURCE_ID" }],
                        Filter = new Ce.Expression
                        {
                            And = [region, new Ce.Expression { Dimensions = new Ce.DimensionValues { Key = Dimension.SERVICE, Values = ResourceCostServices } }],
                        },
                        NextPageToken = token,
                    }, ct);
                    foreach (var group in (resp.ResultsByTime ?? []).SelectMany(p => p.Groups ?? []))
                        if (group.Keys?.FirstOrDefault() is { Length: > 0 } id)
                            resources[id] = resources.GetValueOrDefault(id) + Amount(group.Metrics);
                    token = resp.NextPageToken;
                } while (!string.IsNullOrEmpty(token) && ++pages < 10);
            }
            catch (AmazonServiceException ex) when (!AwsErrorClassifier.IsAuthFailure(ex.ErrorCode))
            {
                note = ex.ErrorCode is "DataUnavailableException" or "ValidationException"
                    ? "Per-resource billed costs need \"Resource-level data at daily granularity\" enabled in Billing → Cost Management preferences (payer account)."
                    : $"Per-resource billed costs are not available ({ex.ErrorCode}).";
            }

            return new ActualCosts
            {
                FetchedUtc = DateTime.UtcNow,
                MonthToDate = mtd,
                LastMonth = last,
                MonthToDateEstimated = estimated,
                Services = byService.Select(kv => new ServiceCost { Service = kv.Key, MonthToDate = kv.Value.Mtd, LastMonth = kv.Value.Last })
                    .Where(s => s.MonthToDate >= 0.005 || s.LastMonth >= 0.005)
                    .OrderByDescending(s => Math.Max(s.MonthToDate, s.LastMonth)).ToList(),
                ResourceLast14Days = resources,
                ResourceNote = note ?? (resources.Count == 0 ? "Cost Explorer returned no per-resource costs for this region." : null),
            };
        });

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static double Amount(Dictionary<string, Ce.MetricValue>? metrics) =>
        metrics?.GetValueOrDefault("UnblendedCost")?.Amount is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
