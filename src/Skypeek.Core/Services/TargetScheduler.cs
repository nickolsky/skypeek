using System.Collections.Concurrent;
using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;

namespace Skypeek.Core.Services;

public sealed class JobState
{
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastAttempt { get; set; }
    public bool LastFailed { get; set; }
    public string? LastError { get; set; }
    public int LastCount { get; set; }
    public bool Running { get; set; }
    public bool ForceDue { get; set; }
    public DateTime? SkippedUntil { get; set; }
}

/// <summary>
/// Runs catalog, health and metrics jobs per target on their own intervals. Jobs for halted profiles are skipped
/// without calling AWS; they resume as soon as the credential monitor reports the profile recovered.
/// </summary>
public sealed class TargetScheduler : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Stagger = TimeSpan.FromSeconds(5);

    private readonly SettingsService _settings;
    private readonly CredentialMonitor _monitor;
    private readonly ISyncRunStore _runStore;
    private readonly IRequestLogSink _log;
    private readonly Func<Target, JobKind, CancellationToken, Task<int>> _executor;
    private readonly ActivityTracker? _activity;
    private readonly ConcurrentDictionary<(long TargetId, JobKind Kind), JobState> _jobs = new();
    private readonly SemaphoreSlim _concurrency = new(3, 3);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _tickGate = new();
    private Timer? _timer;

    public TargetScheduler(SettingsService settings, CredentialMonitor monitor, ISyncRunStore runStore, IRequestLogSink log,
        Func<Target, JobKind, CancellationToken, Task<int>> executor, ActivityTracker? activity = null)
    {
        _activity = activity;
        _settings = settings;
        _monitor = monitor;
        _runStore = runStore;
        _log = log;
        _executor = executor;
        _monitor.ProfileRecovered += OnProfileRecovered;
        _monitor.SignInRestored += OnSignInRestored;
    }

    /// <summary>After signing in again, jobs skipped while signed out run now instead of after their full interval.</summary>
    private void OnSignInRestored(IReadOnlyList<string> profiles)
    {
        foreach (var profile in profiles)
            OnProfileRecovered(profile);
    }

    public event Action<Target, JobKind, JobState>? JobCompleted;

    public void Start()
    {
        foreach (var run in _runStore.LatestRuns())
        {
            if (!Enum.TryParse<JobKind>(run.Feature, out var kind))
                continue;
            var state = GetState(run.TargetId, kind);
            state.LastAttempt = run.FinishedAt;
            state.LastFailed = !run.Success;
            state.LastError = run.Error;
            state.LastCount = run.ItemCount;
            if (run.Success)
                state.LastSuccess = run.FinishedAt;
        }

        // Health and metrics are cheap and should be fresh after a restart; lists and network data are cached.
        foreach (var ((_, kind), state) in _jobs)
            if (kind is JobKind.Health or JobKind.Metrics)
                state.ForceDue = true;

        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(2), TickInterval);
    }

    public JobState GetState(long targetId, JobKind kind) => _jobs.GetOrAdd((targetId, kind), _ => new JobState());

    public static bool IsFeatureEnabled(Target t, JobKind kind) => kind switch
    {
        JobKind.Catalog => t.SecretsEnabled || t.ParamsEnabled,
        JobKind.Health or JobKind.Metrics => t.HealthEnabled,
        JobKind.Network => t.NetworkEnabled,
        JobKind.Costs => t.CostEnabled || t.CostExplorerEnabled,
        _ => false,
    };

    public static int IntervalMinutes(Target t, JobKind kind) => kind switch
    {
        JobKind.Catalog => t.CatalogIntervalMinutes,
        JobKind.Health => t.HealthIntervalMinutes,
        JobKind.Metrics => t.MetricsIntervalMinutes,
        JobKind.Network => t.NetworkIntervalMinutes,
        JobKind.Costs => 1440,
        _ => 0,
    };

    /// <summary>Manual refresh. Runs even when the periodic interval is Off.</summary>
    public void RunNow(long? targetId = null, JobKind? kind = null)
    {
        foreach (var target in _settings.Targets.Where(t => t.Enabled && (targetId is null || t.Id == targetId)))
            foreach (var k in Enum.GetValues<JobKind>().Where(k => (kind is null || k == kind) && IsFeatureEnabled(target, k)))
            {
                var state = GetState(target.Id, k);
                state.ForceDue = true;
                state.SkippedUntil = null;
            }
        Tick();
    }

    public DateTime? NextDue(Target target, JobKind kind)
    {
        var interval = IntervalMinutes(target, kind);
        if (interval <= 0 || !target.Enabled || !IsFeatureEnabled(target, kind))
            return null;
        var state = GetState(target.Id, kind);
        var next = (state.LastSuccess ?? DateTime.MinValue).AddMinutes(interval);
        if (state.LastFailed && state.LastAttempt is { } attempt)
        {
            var retry = attempt + TimeSpan.FromMinutes(Math.Min(interval, MaxRetryDelay.TotalMinutes));
            if (retry > next) next = retry;
        }
        if (state.SkippedUntil is { } skipped && skipped > next)
            next = skipped;
        return next;
    }

    private void OnProfileRecovered(string profile)
    {
        var now = DateTime.UtcNow;
        foreach (var target in _settings.Targets.Where(t => t.ProfileName == profile))
            foreach (var kind in Enum.GetValues<JobKind>())
            {
                var state = GetState(target.Id, kind);
                state.SkippedUntil = null;
                // Run everything that became overdue while the profile was halted.
                if (NextDue(target, kind) is { } due && due <= now)
                    state.ForceDue = true;
            }
        Tick();
    }

    private void Tick()
    {
        if (_cts.IsCancellationRequested)
            return;

        var now = DateTime.UtcNow;
        var toStart = new List<(Target Target, JobKind Kind)>();

        lock (_tickGate)
        {
            foreach (var target in _settings.Targets.Where(t => t.Enabled))
            {
                foreach (var kind in Enum.GetValues<JobKind>())
                {
                    if (!IsFeatureEnabled(target, kind))
                        continue;
                    var state = GetState(target.Id, kind);
                    if (state.Running)
                        continue;

                    var due = state.ForceDue || (NextDue(target, kind) is { } next && next <= now);
                    if (!due)
                        continue;

                    if (_monitor.IsHalted(target.ProfileName))
                    {
                        state.ForceDue = false;
                        state.SkippedUntil = now.AddMinutes(Math.Max(1, IntervalMinutes(target, kind)));
                        _log.Write(new RequestLogEntry
                        {
                            TimestampUtc = now,
                            Profile = target.ProfileName,
                            Region = target.Region,
                            Service = "scheduler",
                            Operation = $"{kind} refresh",
                            Outcome = RequestOutcome.Skipped,
                            Message = "skipped: credentials unchanged since halt",
                        });
                        continue;
                    }

                    state.Running = true;
                    state.ForceDue = false;
                    toStart.Add((target, kind));
                }
            }
        }

        for (var i = 0; i < toStart.Count; i++)
        {
            var (target, kind) = toStart[i];
            var delay = toStart.Count > 3 ? Stagger * i : TimeSpan.Zero;
            _ = RunJobAsync(target, kind, delay);
        }
    }

    private async Task RunJobAsync(Target target, JobKind kind, TimeSpan delay)
    {
        using var activity = _activity?.Begin(target.Id, target.DisplayName, ActivityTracker.JobText(kind), waiting: true);
        var state = GetState(target.Id, kind);
        var ct = _cts.Token;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            await _concurrency.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            state.Running = false;
            return;
        }
        activity?.Started();

        var started = DateTime.UtcNow;
        try
        {
            var count = await _executor(target, kind, ct);
            state.LastSuccess = DateTime.UtcNow;
            state.LastFailed = false;
            state.LastError = null;
            state.LastCount = count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (CredentialsUnavailableException ex)
        {
            state.LastFailed = true;
            state.LastError = ex.Reason;
            state.SkippedUntil = DateTime.UtcNow.AddMinutes(Math.Max(1, IntervalMinutes(target, kind)));
        }
        catch (Exception ex)
        {
            state.LastFailed = true;
            state.LastError = ex.Message;
        }
        finally
        {
            _concurrency.Release();
            state.LastAttempt = DateTime.UtcNow;
            state.Running = false;
        }

        try
        {
            _runStore.Record(new SyncRun(target.Id, kind.ToString(), started, state.LastAttempt ?? DateTime.UtcNow, !state.LastFailed, state.LastCount, state.LastError));
        }
        catch
        {
            // best effort
        }
        JobCompleted?.Invoke(target, kind, state);
    }

    public void Dispose()
    {
        _monitor.ProfileRecovered -= OnProfileRecovered;
        _monitor.SignInRestored -= OnSignInRestored;
        _cts.Cancel();
        _timer?.Dispose();
    }
}
