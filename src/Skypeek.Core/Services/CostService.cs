using System.Collections.Concurrent;
using Skypeek.Core.Credentials;
using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

/// <summary>
/// Monthly cost per resource: list-price estimates (AWS Price List, prices cached for a week) and, when enabled, billed
/// cost from Cost Explorer (refreshed at most every 12 hours, because every Cost Explorer request is charged).
/// </summary>
public sealed class CostService
{
    private static readonly TimeSpan PriceMaxAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan MissingPriceRetry = TimeSpan.FromDays(1);
    private static readonly TimeSpan ActualMaxAge = TimeSpan.FromHours(12);

    private readonly IAwsGateway _gateway;
    private readonly ICostStore _store;
    private readonly SettingsService _settings;
    private readonly HealthService _health;
    private readonly NetworkService _network;
    private readonly ConcurrentDictionary<long, CostSnapshot> _state = new();

    public CostService(IAwsGateway gateway, ICostStore store, SettingsService settings, HealthService health, NetworkService network)
    {
        _gateway = gateway;
        _store = store;
        _settings = settings;
        _health = health;
        _network = network;
        foreach (var snapshot in store.LoadAll())
            _state[snapshot.TargetId] = snapshot;
    }

    public event Action? Changed;

    public CostSnapshot? Get(long targetId) => _state.TryGetValue(targetId, out var s) ? s : null;

    public void Forget(long targetId) => _state.TryRemove(targetId, out _);

    private PriceEntry? Price(long targetId, string key) =>
        _state.TryGetValue(targetId, out var s) && s.Prices.TryGetValue(key, out var p) ? p : null;

    /// <summary>Estimate and billed cost of one resource, or null when cost features are off for its target.</summary>
    public ResourceCost? For(ResourceStatus r)
    {
        if (_settings.FindTarget(r.TargetId) is not { } target || !(target.CostEnabled || target.CostExplorerEnabled))
            return null;
        var estimate = target.CostEnabled ? CostRules.Estimate(r, key => Price(r.TargetId, key)) : null;
        if (r is RdsInstanceStatus db && Get(r.TargetId) is { } costs)
            estimate = CostRules.WithEngineSupport(estimate, db.Snapshot, costs.ExtendedSupportStarts, DateTime.UtcNow);
        var actual = target.CostExplorerEnabled ? CostRules.Actual14Days(r, Get(r.TargetId)?.Actual) : null;
        return estimate is null && actual is null ? null : new ResourceCost(estimate, actual);
    }

    /// <summary>A cached list price (per hour, per GB-month…) when estimates are on for the target.</summary>
    public double? UnitPrice(Target target, string key) => target.CostEnabled ? Price(target.Id, key)?.Usd : null;

    /// <summary>Secrets and Advanced parameters per target (set by the session from the catalog).</summary>
    public Func<long, (int Secrets, int AdvancedParameters)> CatalogCounts { get; set; } = _ => (0, 0);

    private Func<string, PriceEntry?>? Prices(Target target) => target.CostEnabled ? key => Price(target.Id, key) : null;

    /// <summary>NAT gateways, interface endpoints and public IPv4 addresses of all the target's VPCs.</summary>
    public CostEstimate? NetworkEstimate(Target target) =>
        Prices(target) is { } price && _network.Get(target.Id) is { } network ? CostRules.NetworkEstimate(network, price) : null;

    public CostEstimate? VpcEstimate(Target target, string vpcId) =>
        Prices(target) is { } price && _network.Get(target.Id) is { } network ? CostRules.VpcEstimate(network, vpcId, price) : null;

    public CostEstimate? IdleAddressEstimate(Target target) =>
        Prices(target) is { } price && _network.Get(target.Id) is { } network ? CostRules.IdleAddressEstimate(network, price) : null;

    public CostEstimate? CatalogEstimate(Target target)
    {
        if (Prices(target) is not { } price)
            return null;
        var (secrets, advanced) = CatalogCounts(target.Id);
        return CostRules.CatalogEstimate(secrets, advanced, price);
    }

    /// <summary>NAT gateways of the target (they are not dashboard resources).</summary>
    public CostEstimate? NatEstimate(Target target) =>
        target.CostEnabled && _network.Get(target.Id) is { } network ? CostRules.NatEstimate(network, key => Price(target.Id, key)) : null;

    /// <summary>Price keys the target's current resources need but that are not cached (or are stale).</summary>
    public IReadOnlyList<string> MissingPrices(Target target, DateTime nowUtc)
    {
        if (!target.CostEnabled || _health.Get(target.Id) is not { } health)
            return [];
        var cached = Get(target.Id)?.Prices ?? new();
        var (secrets, advanced) = CatalogCounts(target.Id);
        IEnumerable<string> catalogKeys = [.. secrets > 0 ? [CostRules.SecretKey] : Array.Empty<string>(),
            .. advanced > 0 ? [CostRules.AdvancedParameterKey] : Array.Empty<string>()];
        return CostRules.KeysFor(health, _network.Get(target.Id)).Concat(catalogKeys).Distinct()
            .Where(key => !cached.TryGetValue(key, out var p) || nowUtc - p.FetchedUtc > (p.Usd is null ? MissingPriceRetry : PriceMaxAge))
            .Where(key => CostRules.QueryFor(key, target.Region) is not null)
            .ToList();
    }

    /// <summary>RDS engines of the target whose support dates are unknown or older than a week.</summary>
    private IReadOnlyList<string> MissingEngineSupport(Target target, CostSnapshot? snapshot, DateTime nowUtc) =>
        !target.CostEnabled || _health.Get(target.Id) is not { } health ? []
            : health.Rds.Select(d => d.Snapshot.Engine).Where(e => e.Length > 0).Distinct()
                .Where(e => snapshot is null || !snapshot.EngineSupportFetchedUtc.TryGetValue(e, out var at) || nowUtc - at > PriceMaxAge)
                .ToList();

    /// <summary>True when estimates are on and something they need (prices, RDS support dates) is not cached.</summary>
    public bool NeedsSync(Target target, DateTime nowUtc) =>
        target.CostEnabled && (MissingPrices(target, nowUtc).Count > 0 || MissingEngineSupport(target, Get(target.Id), nowUtc).Count > 0);

    /// <summary>
    /// Fetches missing prices and, if enabled and older than 12 hours (or <paramref name="forceActual"/>), the billed
    /// costs. Parts that fail are reported; what was read is kept.
    /// </summary>
    public async Task<int> SyncAsync(Target target, bool forceActual, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var previous = Get(target.Id);
        var snapshot = new CostSnapshot
        {
            TargetId = target.Id,
            Prices = new Dictionary<string, PriceEntry>(previous?.Prices ?? new()),
            ExtendedSupportStarts = new Dictionary<string, DateTime>(previous?.ExtendedSupportStarts ?? new()),
            EngineSupportFetchedUtc = new Dictionary<string, DateTime>(previous?.EngineSupportFetchedUtc ?? new()),
            Actual = previous?.Actual,
            UpdatedUtc = now,
        };
        var errors = new List<string>();
        var fetched = 0;

        foreach (var key in MissingPrices(target, now))
        {
            if (CostRules.QueryFor(key, target.Region) is not { } query)
                continue;
            try
            {
                var items = await _gateway.GetPricesAsync(target, query, ct);
                snapshot.Prices[key] = CostRules.Pick(query, items, now);
                fetched++;
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                var code = ex.GetType().GetProperty("ErrorCode")?.GetValue(ex) as string ?? "";
                if (code.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"price list: {ex.Message}");
                    break; // A permission problem: the rest would fail the same way.
                }
                // One price that fails (throttling, an odd product) must not keep the others from loading: try it
                // again tomorrow, and slow down a little in case the Price List is throttling.
                snapshot.Prices[key] = new PriceEntry { FetchedUtc = now, Description = $"not available: {ex.Message}" };
                errors.Add($"price of {key}: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        // RDS support dates (Extended Support is billed per vCPU-hour once standard support ends).
        foreach (var engine in MissingEngineSupport(target, snapshot, now))
        {
            try
            {
                var starts = await _gateway.GetRdsExtendedSupportStartsAsync(target, engine, ct);
                foreach (var key in snapshot.ExtendedSupportStarts.Keys.Where(k => k.StartsWith(engine + "|", StringComparison.Ordinal)).ToList())
                    snapshot.ExtendedSupportStarts.Remove(key);
                foreach (var (major, start) in starts)
                    snapshot.ExtendedSupportStarts[$"{engine}|{major}"] = start;
                snapshot.EngineSupportFetchedUtc[engine] = now;
                fetched++;
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"RDS engine versions: {ex.Message}");
                break;
            }
        }

        if (target.CostExplorerEnabled && (forceActual || snapshot.Actual is null || now - snapshot.Actual.FetchedUtc > ActualMaxAge))
        {
            try
            {
                snapshot.Actual = await _gateway.GetActualCostsAsync(target, ct);
                fetched++;
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"Cost Explorer: {ex.Message}");
            }
        }

        snapshot.Error = errors.Count > 0 ? string.Join("; ", errors) : null;
        _state[target.Id] = snapshot;
        try
        {
            _store.Save(target.Id, snapshot);
        }
        catch
        {
            // Caching is best effort.
        }
        Changed?.Invoke();
        if (snapshot.Error is { } error)
            throw new InvalidOperationException(error);
        return fetched;
    }
}
