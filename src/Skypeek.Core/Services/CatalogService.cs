using Skypeek.Core.Credentials;
using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

/// <summary>Downloads secret/parameter lists (metadata only) and keeps the search index current.</summary>
public sealed class CatalogService
{
    private readonly IAwsGateway _gateway;
    private readonly ICatalogStore _store;
    private readonly SettingsService _settings;
    private readonly CredentialMonitor _monitor;

    public CatalogService(IAwsGateway gateway, ICatalogStore store, SettingsService settings, CredentialMonitor monitor)
    {
        _gateway = gateway;
        _store = store;
        _settings = settings;
        _monitor = monitor;
        Index = new SearchIndex();
    }

    public SearchIndex Index { get; }

    public event Action? CatalogChanged;

    public void ReloadIndex()
    {
        Index.Load(_store.LoadAll(), _settings.Targets, AccountOf);
        CatalogChanged?.Invoke();
    }

    public string? AccountOf(string profile) =>
        _monitor.Profiles.TryGetValue(profile, out var p) ? p.AccountId : null;

    public async Task<int> SyncAsync(Target target, CancellationToken ct)
    {
        var total = 0;
        var errors = new List<string>();
        var now = DateTime.UtcNow;

        if (target.SecretsEnabled)
        {
            try
            {
                var secrets = await _gateway.ListSecretsAsync(target, ct);
                _store.ReplaceSnapshot(target.Id, CatalogKind.Secret, secrets, now);
                total += secrets.Count;
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"Secrets: {ex.Message}");
            }
        }

        if (target.ParamsEnabled)
        {
            try
            {
                var parameters = await _gateway.ListParametersAsync(target, ct);
                _store.ReplaceSnapshot(target.Id, CatalogKind.Parameter, parameters, now);
                total += parameters.Count;
            }
            catch (Exception ex) when (ex is not CredentialsUnavailableException and not OperationCanceledException)
            {
                errors.Add($"Parameters: {ex.Message}");
            }
        }

        ReloadIndex();

        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("; ", errors));
        return total;
    }
}
