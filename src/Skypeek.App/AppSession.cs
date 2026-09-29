using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;
using Skypeek.Core.Services;
using Skypeek.Storage;

namespace Skypeek.App;

/// <summary>Everything that exists only after the vault is unlocked: storage, AWS access and background jobs.</summary>
public sealed class AppSession : IDisposable
{
    private readonly Timer _retentionTimer;

    private AppSession(Vault vault, RequestLogService log, INotifier notifier, IElevationApprover approver)
    {
        Vault = vault;
        Log = log;
        Repository = new VaultRepository(vault);
        log.AttachStore(Repository);

        Settings = new SettingsService(Repository);
        Monitor = new CredentialMonitor(ProfileReader.DefaultPath, Repository, new StsValidator(), Settings.RegionForProfile,
            ProfileReader.DefaultConfigPath, ProfileReader.DefaultSsoCacheDirectory);
        Clients = new AwsClientFactory();
        Gateway = new AwsGateway(Monitor, Clients, log, approver);
        Catalog = new CatalogService(Gateway, Repository, Settings, Monitor);
        Health = new HealthService(Gateway, Repository, Settings, notifier);
        Network = new NetworkService(Gateway, Repository, Settings);
        Costs = new CostService(Gateway, Repository, Settings, Health, Network);
        Scheduler = new TargetScheduler(Settings, Monitor, Repository, log, ExecuteJob);

        Monitor.ProfileHalted += p => notifier.Notify("AWS credentials rejected",
            $"{p.Profile} ({p.ErrorCode}). Refreshes for this profile are paused until the credentials file is updated.");
        Monitor.ProfileRecovered += p => notifier.Notify("AWS credentials updated", $"{p}: verified, refreshes resumed.");
        Settings.Changed += () => Catalog.ReloadIndex();

        _retentionTimer = new Timer(_ => PurgeLog(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(12));
    }

    public Vault Vault { get; }
    public RequestLogService Log { get; }
    public VaultRepository Repository { get; }
    public SettingsService Settings { get; }
    public CredentialMonitor Monitor { get; }
    public AwsClientFactory Clients { get; }
    public AwsGateway Gateway { get; }
    public CatalogService Catalog { get; }
    public HealthService Health { get; }
    public NetworkService Network { get; }
    public CostService Costs { get; }
    public TargetScheduler Scheduler { get; }

    public static async Task<AppSession> StartAsync(Vault vault, RequestLogService log, INotifier notifier, IElevationApprover approver)
    {
        var session = new AppSession(vault, log, notifier, approver);
        await session.Monitor.StartAsync();
        session.Catalog.ReloadIndex();
        session.Scheduler.Start();
        return session;
    }

    /// <summary>Visible profiles: read-only roles only unless the user opted in to see the rest.</summary>
    public IReadOnlyList<ProfileCredentials> VisibleProfiles() =>
        Monitor.Profiles.Values
            .Where(p => p.IsReadOnly || Settings.Settings.ShowNonReadOnlyProfiles)
            .OrderBy(p => p.Name)
            .ToList();

    public TrayStatus ComputeTrayStatus() =>
        TrayStatusCalculator.Compute(Settings.Targets, Health.Snapshot(), Monitor.GetStatuses(),
            (id, kind) => Scheduler.GetState(id, kind), Settings.Settings.WarningsTurnIconRed);

    public void DeleteTarget(long id)
    {
        Settings.DeleteTarget(id);
        Health.Forget(id);
        Network.Forget(id);
        Costs.Forget(id);
        Catalog.ReloadIndex();
    }

    private Task<int> ExecuteJob(Target target, JobKind kind, CancellationToken ct) => kind switch
    {
        JobKind.Catalog => Catalog.SyncAsync(target, ct),
        JobKind.Health => PollHealthThenPricesAsync(target, ct),
        JobKind.Costs => Costs.SyncAsync(target, forceActual: false, ct),
        JobKind.Metrics => Health.PollMetricsAsync(target, ct),
        JobKind.Network => Network.SyncAsync(target, ct),
        _ => Task.FromResult(0),
    };

    /// <summary>A new instance type (or first poll) needs prices: fetch them right away instead of the next day.</summary>
    private async Task<int> PollHealthThenPricesAsync(Target target, CancellationToken ct)
    {
        try
        {
            return await Health.PollHealthAsync(target, ct);
        }
        finally
        {
            if (Costs.NeedsSync(target, DateTime.UtcNow))
                Scheduler.RunNow(target.Id, JobKind.Costs);
        }
    }

    private void PurgeLog()
    {
        try
        {
            Repository.Purge(DateTime.UtcNow.AddDays(-Math.Max(1, Settings.Settings.RequestLogRetentionDays)));
        }
        catch
        {
            // best effort
        }
    }

    public void Dispose()
    {
        _retentionTimer.Dispose();
        Scheduler.Dispose();
        Monitor.Dispose();
        Clients.Dispose();
        Vault.Dispose();
    }
}
