using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

/// <summary>Something being refreshed right now (a scheduled job or a manual refresh), for the "refreshing…" indicators.</summary>
/// <param name="ResourceKey">Set when one resource is refreshed on its own.</param>
/// <param name="Waiting">Queued behind other refreshes (at most three run at once).</param>
public sealed record ActivityItem(long Id, long? TargetId, string TargetName, string What, string? ResourceKey, DateTime StartedUtc, bool Waiting)
{
    public string Text => $"{TargetName}: {What}{(Waiting ? " (queued)" : "")}";
}

/// <summary>What is being refreshed right now. Thread-safe; <see cref="Changed"/> fires on any thread.</summary>
public sealed class ActivityTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<long, ActivityItem> _items = new();
    private long _nextId;

    public event Action? Changed;

    public IReadOnlyList<ActivityItem> Items
    {
        get
        {
            lock (_gate)
                return _items.Values.OrderBy(i => i.Waiting).ThenBy(i => i.StartedUtc).ToList();
        }
    }

    /// <summary>Registers an activity; dispose the handle when it is done.</summary>
    public Handle Begin(long? targetId, string targetName, string what, string? resourceKey = null, bool waiting = false)
    {
        long id;
        lock (_gate)
        {
            id = ++_nextId;
            _items[id] = new ActivityItem(id, targetId, targetName, what, resourceKey, DateTime.UtcNow, waiting);
        }
        Changed?.Invoke();
        return new Handle(this, id);
    }

    private void Started(long id)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var item) || !item.Waiting)
                return;
            _items[id] = item with { Waiting = false, StartedUtc = DateTime.UtcNow };
        }
        Changed?.Invoke();
    }

    private void End(long id)
    {
        bool removed;
        lock (_gate)
            removed = _items.Remove(id);
        if (removed)
            Changed?.Invoke();
    }

    public sealed class Handle(ActivityTracker owner, long id) : IDisposable
    {
        /// <summary>A queued activity started running.</summary>
        public void Started() => owner.Started(id);
        public void Dispose() => owner.End(id);
    }

    /// <summary>"health", "metrics and alarms", … as shown in the indicators.</summary>
    public static string JobText(JobKind kind) => kind switch
    {
        JobKind.Catalog => "secrets and parameters list",
        JobKind.Health => "health",
        JobKind.Metrics => "metrics and alarms",
        JobKind.Network => "network download",
        JobKind.Costs => "costs",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
