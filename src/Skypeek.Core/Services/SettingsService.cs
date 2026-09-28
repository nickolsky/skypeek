using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

/// <summary>In-memory copy of settings and targets backed by the vault.</summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly object _gate = new();
    private AppSettings _settings;
    private List<Target> _targets;

    public SettingsService(ISettingsStore store)
    {
        _store = store;
        _settings = store.LoadSettings();
        _targets = store.LoadTargets().ToList();
    }

    public event Action? Changed;

    public AppSettings Settings
    {
        get { lock (_gate) return _settings; }
    }

    public IReadOnlyList<Target> Targets
    {
        get { lock (_gate) return _targets.ToList(); }
    }

    public Target? FindTarget(long id)
    {
        lock (_gate) return _targets.FirstOrDefault(t => t.Id == id);
    }

    public void SaveSettings(AppSettings settings)
    {
        _store.SaveSettings(settings);
        lock (_gate) _settings = settings;
        Changed?.Invoke();
    }

    public Target SaveTarget(Target target)
    {
        var saved = _store.SaveTarget(target);
        lock (_gate)
        {
            _targets.RemoveAll(t => t.Id == saved.Id);
            _targets.Add(saved);
            _targets = _targets.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        Changed?.Invoke();
        return saved;
    }

    public void DeleteTarget(long id)
    {
        _store.DeleteTarget(id);
        lock (_gate) _targets.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    /// <summary>Region used to validate a profile: first target region using it.</summary>
    public string? RegionForProfile(string profile)
    {
        lock (_gate) return _targets.FirstOrDefault(t => t.ProfileName == profile || t.ElevatedProfileName == profile)?.Region;
    }
}
