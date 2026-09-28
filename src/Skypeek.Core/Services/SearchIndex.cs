using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

public sealed record SearchFilter(CatalogKind? Kind = null, string? AccountId = null, string? Region = null, long? TargetId = null);

public sealed class SearchResult
{
    public required CatalogItem Item { get; init; }
    public required Target Target { get; init; }
    public string? AccountId { get; init; }
    public int Score { get; init; }
    public bool IsNew { get; init; }

    public string KindLabel => Item.Kind == CatalogKind.Secret ? "Secret" : Item.IsSecureParameter ? "Param (secure)" : "Param";
    public string Location => $"{Target.DisplayName} · {Target.Region}";
    public string Modified => Item.LastModified is { } m ? m.ToLocalTime().ToString("g") : "";
}

/// <summary>In-memory, case-insensitive token search over the catalog. Snapshots are swapped atomically.</summary>
public sealed class SearchIndex
{
    private static readonly TimeSpan NewBadgeWindow = TimeSpan.FromDays(3);

    private sealed record Entry(CatalogItem Item, string Haystack, string NameLower);

    private IReadOnlyList<Entry> _entries = [];
    private IReadOnlyDictionary<long, Target> _targets = new Dictionary<long, Target>();
    private Func<string, string?> _accountOf = _ => null;

    public int Count => _entries.Count;

    public void Load(IEnumerable<CatalogItem> items, IEnumerable<Target> targets, Func<string, string?> accountOfProfile)
    {
        var targetMap = targets.ToDictionary(t => t.Id);
        var entries = new List<Entry>();
        foreach (var item in items)
        {
            if (!targetMap.TryGetValue(item.TargetId, out var target))
                continue;
            var haystack = string.Join('\n',
                item.Name, item.Description, item.Arn, target.DisplayName, target.Region, target.ProfileName,
                string.Join(' ', item.Tags.Select(kv => $"{kv.Key}={kv.Value}"))).ToLowerInvariant();
            entries.Add(new Entry(item, haystack, item.Name.ToLowerInvariant()));
        }

        _targets = targetMap;
        _accountOf = accountOfProfile;
        _entries = entries;
    }

    public IReadOnlyList<SearchResult> Search(string query, SearchFilter filter, int limit = 300)
    {
        var tokens = query.ToLowerInvariant().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var targets = _targets;
        var now = DateTime.UtcNow;
        var results = new List<SearchResult>();

        foreach (var entry in _entries)
        {
            var item = entry.Item;
            if (filter.Kind is { } kind && item.Kind != kind)
                continue;
            if (filter.TargetId is { } tid && item.TargetId != tid)
                continue;
            var target = targets[item.TargetId];
            if (filter.Region is { } region && target.Region != region)
                continue;
            var account = _accountOf(target.ProfileName);
            if (filter.AccountId is { } acc && account != acc)
                continue;

            var score = 0;
            var matched = true;
            foreach (var token in tokens)
            {
                if (!entry.Haystack.Contains(token, StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
                if (entry.NameLower == token) score += 100;
                else if (entry.NameLower.StartsWith(token, StringComparison.Ordinal) || entry.NameLower.Contains("/" + token, StringComparison.Ordinal)) score += 30;
                else if (entry.NameLower.Contains(token, StringComparison.Ordinal)) score += 10;
                else score += 1;
            }
            if (!matched)
                continue;

            results.Add(new SearchResult
            {
                Item = item,
                Target = target,
                AccountId = account,
                Score = score,
                // FirstSeen is MinValue for items present in the very first sync (baseline, not "new").
                IsNew = item.FirstSeen != DateTime.MinValue && now - item.FirstSeen < NewBadgeWindow,
            });
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }
}
