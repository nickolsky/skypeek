using System.Collections.Concurrent;

namespace Skypeek.Core.Logging;

public enum RequestOutcome
{
    Success,
    Error,
    Blocked,
    Skipped,
}

/// <summary>One logged AWS call. Must never contain values, tokens, headers or response bodies.</summary>
public sealed class RequestLogEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; init; }
    public string? Profile { get; init; }
    public string? AccountId { get; init; }
    public string? Region { get; init; }
    public string Service { get; init; } = "";
    public string Operation { get; init; } = "";
    public string Parameters { get; init; } = "";
    public RequestOutcome Outcome { get; init; }
    public int? HttpStatus { get; init; }
    public long DurationMs { get; init; }
    public string? RequestId { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }

    /// <summary>The call used the target's elevated profile (after the user approved it).</summary>
    public bool Elevated { get; init; }

    public DateTime TimestampLocal => TimestampUtc.ToLocalTime();
    public string KeyLabel => Elevated ? "ELEVATED" : "read-only";
}

public interface IRequestLogSink
{
    void Write(RequestLogEntry entry);
}

/// <summary>
/// Ambient information about who is calling AWS, flowed through async calls so the SDK pipeline handler
/// can attribute each request to a profile/region without the request object carrying it.
/// </summary>
/// <param name="Approved">The user confirmed this call; required for the few allowlisted non-read actions.</param>
public sealed record RequestScopeInfo(string Profile, string? AccountId, string Region, bool Elevated = false, bool Approved = false);

public static class RequestScope
{
    private static readonly AsyncLocal<RequestScopeInfo?> CurrentScope = new();

    public static RequestScopeInfo? Current => CurrentScope.Value;

    public static IDisposable Begin(RequestScopeInfo info)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = info;
        return new Restore(previous);
    }

    private sealed class Restore(RequestScopeInfo? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }
}

/// <summary>Fan-out sink: keeps a live in-memory tail and persists when a store is attached.</summary>
public sealed class RequestLogService : IRequestLogSink
{
    private const int MemoryCapacity = 5000;
    private readonly ConcurrentQueue<RequestLogEntry> _recent = new();
    private IRequestLogStore? _store;

    public event Action<RequestLogEntry>? EntryAdded;

    public void AttachStore(IRequestLogStore store)
    {
        _store = store;
        // Flush entries logged before the vault was unlocked.
        foreach (var entry in _recent)
            if (entry.Id == 0)
                TryPersist(entry);
    }

    public void Write(RequestLogEntry entry)
    {
        _recent.Enqueue(entry);
        while (_recent.Count > MemoryCapacity && _recent.TryDequeue(out _)) { }
        TryPersist(entry);
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<RequestLogEntry> Snapshot(int limit = 2000)
    {
        if (_store is not null)
        {
            try
            {
                return _store.Recent(limit);
            }
            catch
            {
                // fall back to memory
            }
        }
        return _recent.Reverse().Take(limit).ToList();
    }

    private void TryPersist(RequestLogEntry entry)
    {
        try
        {
            _store?.Append(entry);
        }
        catch
        {
            // Logging must never break an AWS call.
        }
    }
}
