using System.Collections.Concurrent;
using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

/// <summary>
/// Network tab data per target: downloaded by the Network job (or on demand), cached in the vault so the tab opens
/// instantly, and patched in place after a security group edit.
/// </summary>
public sealed class NetworkService
{
    private readonly IAwsGateway _gateway;
    private readonly INetworkStore _store;
    private readonly SettingsService _settings;
    private readonly ConcurrentDictionary<long, NetworkSnapshot> _state = new();

    public NetworkService(IAwsGateway gateway, INetworkStore store, SettingsService settings)
    {
        _gateway = gateway;
        _store = store;
        _settings = settings;
        foreach (var snapshot in store.LoadAll())
            _state[snapshot.TargetId] = snapshot;
    }

    public event Action? Changed;

    public NetworkSnapshot? Get(long targetId) => _state.TryGetValue(targetId, out var s) ? s : null;

    public IReadOnlyList<NetworkSnapshot> Snapshot()
    {
        var ids = _settings.Targets.Select(t => t.Id).ToHashSet();
        return _state.Values.Where(s => ids.Contains(s.TargetId)).ToList();
    }

    public void Forget(long targetId) => _state.TryRemove(targetId, out _);

    /// <summary>Downloads everything for the target. Parts that fail are reported but the rest is kept.</summary>
    public async Task<int> SyncAsync(Target target, CancellationToken ct)
    {
        var snapshot = await _gateway.GetNetworkAsync(target, ct);
        Commit(target.Id, snapshot);
        if (snapshot.Error is { } error)
            throw new InvalidOperationException(error);
        return snapshot.Interfaces.Count + snapshot.SecurityGroups.Count + snapshot.Subnets.Count;
    }

    /// <summary>Re-reads the given security groups (e.g. after adding or deleting a rule) and updates the cache.</summary>
    public async Task RefreshSecurityGroupsAsync(Target target, IReadOnlyList<string> groupIds, CancellationToken ct)
    {
        var fresh = await _gateway.GetSecurityGroupsAsync(target, groupIds, ct);
        if (Get(target.Id) is not { } current)
            return;
        var groups = current.SecurityGroups.Where(g => !groupIds.Contains(g.Id)).Concat(fresh).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Commit(target.Id, new NetworkSnapshot
        {
            TargetId = current.TargetId,
            DownloadedUtc = current.DownloadedUtc,
            Vpcs = current.Vpcs,
            Subnets = current.Subnets,
            Interfaces = current.Interfaces,
            ElasticIps = current.ElasticIps,
            SecurityGroups = groups,
            Error = current.Error,
        });
    }

    private void Commit(long targetId, NetworkSnapshot snapshot)
    {
        _state[targetId] = snapshot;
        try
        {
            _store.Save(targetId, snapshot);
        }
        catch
        {
            // Caching is best effort; the data is still shown.
        }
        Changed?.Invoke();
    }
}
