namespace Skypeek.Core.Credentials;

public sealed record CredentialHalt(string Profile, string Fingerprint, DateTime HaltedAtUtc, string ErrorCode);

public enum CredentialState
{
    Unknown,
    Valid,
    Halted,
    Validating,
    Missing,
}

public sealed record ProfileStatus(string Profile, CredentialState State, DateTime? HaltedAtUtc, string? ErrorCode, DateTime? LastSuccessUtc);

public sealed class CredentialsUnavailableException(string profile, string reason) : Exception($"Profile '{profile}': {reason}")
{
    public string Profile { get; } = profile;
    public string Reason { get; } = reason;
}

/// <summary>
/// Tracks credential health per profile. When AWS rejects a profile's credentials the profile is halted and the
/// hash of the rejected credentials is persisted. No further calls are made for that profile until the credentials
/// file contains different credentials for it (hash change), which are then verified with sts:GetCallerIdentity.
/// </summary>
public sealed class CredentialMonitor : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly string _path;
    private readonly ICredentialHaltStore _store;
    private readonly ICredentialValidator _validator;
    private readonly Func<string, string?> _regionResolver;
    private readonly object _gate = new();
    private readonly Dictionary<string, CredentialHalt> _halts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastSuccess = new(StringComparer.Ordinal);
    private readonly HashSet<string> _validating = new(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, ProfileCredentials> _profiles = new Dictionary<string, ProfileCredentials>();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _poll;

    public CredentialMonitor(string path, ICredentialHaltStore store, ICredentialValidator validator, Func<string, string?> regionResolver)
    {
        _path = path;
        _store = store;
        _validator = validator;
        _regionResolver = regionResolver;
    }

    public event Action<ProfileStatus>? ProfileHalted;
    public event Action<string>? ProfileRecovered;
    public event Action? StatusChanged;

    public string CredentialsPath => _path;

    public IReadOnlyDictionary<string, ProfileCredentials> Profiles
    {
        get { lock (_gate) return _profiles; }
    }

    /// <summary>Loads persisted halts, reads the file and starts watching it. Returns once initial validations finish.</summary>
    public Task StartAsync(bool watch = true)
    {
        lock (_gate)
        {
            foreach (var halt in _store.LoadHalts())
                _halts[halt.Profile] = halt;
        }

        if (watch)
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir is not null && Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, _) => ScheduleReload();
                _watcher.Created += (_, _) => ScheduleReload();
                _watcher.Renamed += (_, _) => ScheduleReload();
            }
            _debounce = new Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
            // Fallback in case the watcher misses an event (network drives, atomic replace by some tools).
            _poll = new Timer(_ => _ = ReloadAsync(), null, PollInterval, PollInterval);
        }

        return ReloadAsync();
    }

    private void ScheduleReload() => _debounce?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);

    /// <summary>Re-reads the credentials file and validates halted profiles whose credentials hash changed.</summary>
    public Task ReloadAsync()
    {
        IReadOnlyDictionary<string, ProfileCredentials> profiles;
        try
        {
            profiles = ProfileReader.ReadFile(_path);
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }

        var toValidate = new List<string>();
        lock (_gate)
        {
            _profiles = profiles;
            foreach (var halt in _halts.Values)
                if (profiles.TryGetValue(halt.Profile, out var creds) && creds.Fingerprint != halt.Fingerprint)
                    toValidate.Add(halt.Profile);
        }

        StatusChanged?.Invoke();
        return Task.WhenAll(toValidate.Select(ValidateAsync));
    }

    /// <summary>Returns the current credentials or throws when the profile is missing or halted.</summary>
    public ProfileCredentials Acquire(string profile)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profile, out var creds))
                throw new CredentialsUnavailableException(profile, "not found in the credentials file");

            if (_halts.TryGetValue(profile, out var halt))
            {
                if (halt.Fingerprint == creds.Fingerprint)
                    throw new CredentialsUnavailableException(profile, $"credentials rejected ({halt.ErrorCode}) at {halt.HaltedAtUtc.ToLocalTime():g}; waiting for the credentials file to be updated");
                throw new CredentialsUnavailableException(profile, "validating updated credentials");
            }

            return creds;
        }
    }

    public bool IsHalted(string profile)
    {
        lock (_gate) return _halts.ContainsKey(profile);
    }

    public void ReportSuccess(string profile)
    {
        lock (_gate) _lastSuccess[profile] = DateTime.UtcNow;
    }

    /// <summary>Called when AWS rejects credentials. <paramref name="usedFingerprint"/> is the hash of the credentials that failed.</summary>
    public void ReportAuthFailure(string profile, string usedFingerprint, string errorCode)
    {
        CredentialHalt halt;
        bool fileAlreadyChanged;
        lock (_gate)
        {
            if (_halts.TryGetValue(profile, out var existing) && existing.Fingerprint == usedFingerprint)
                return;
            halt = new CredentialHalt(profile, usedFingerprint, DateTime.UtcNow, errorCode);
            _halts[profile] = halt;
            fileAlreadyChanged = _profiles.TryGetValue(profile, out var current) && current.Fingerprint != usedFingerprint;
        }

        _store.SaveHalt(halt);
        ProfileHalted?.Invoke(GetStatus(profile));
        StatusChanged?.Invoke();

        if (fileAlreadyChanged)
            _ = ValidateAsync(profile);
    }

    public async Task ValidateAsync(string profile)
    {
        ProfileCredentials creds;
        lock (_gate)
        {
            if (!_halts.ContainsKey(profile) || !_profiles.TryGetValue(profile, out creds!) || !_validating.Add(profile))
                return;
        }
        StatusChanged?.Invoke();

        try
        {
            var region = _regionResolver(profile) ?? creds.DefaultRegion ?? "us-east-1";
            var result = await _validator.ValidateAsync(creds, region, CancellationToken.None).ConfigureAwait(false);

            var accountMatches = creds.AccountId is null || result.AccountId == creds.AccountId;
            if (result.Success && accountMatches)
            {
                lock (_gate)
                {
                    _halts.Remove(profile);
                    _lastSuccess[profile] = DateTime.UtcNow;
                }
                _store.DeleteHalt(profile);
                ProfileRecovered?.Invoke(profile);
            }
            else if (result.IsAuthFailure || (result.Success && !accountMatches))
            {
                // New credentials are also bad: remember their hash and keep waiting for another change.
                var halt = new CredentialHalt(profile, creds.Fingerprint, DateTime.UtcNow, result.Success ? "AccountMismatch" : result.ErrorCode ?? "AuthFailure");
                lock (_gate) _halts[profile] = halt;
                _store.SaveHalt(halt);
            }
            // Other failures (network etc.) leave the old hash so the next reload retries validation.
        }
        finally
        {
            lock (_gate) _validating.Remove(profile);
            StatusChanged?.Invoke();
        }
    }

    public ProfileStatus GetStatus(string profile)
    {
        lock (_gate)
        {
            _lastSuccess.TryGetValue(profile, out var ok);
            DateTime? lastOk = ok == default ? null : ok;
            if (!_profiles.ContainsKey(profile))
                return new ProfileStatus(profile, CredentialState.Missing, null, null, lastOk);
            if (_validating.Contains(profile))
                return new ProfileStatus(profile, CredentialState.Validating, _halts.GetValueOrDefault(profile)?.HaltedAtUtc, null, lastOk);
            if (_halts.TryGetValue(profile, out var halt))
                return new ProfileStatus(profile, CredentialState.Halted, halt.HaltedAtUtc, halt.ErrorCode, lastOk);
            return new ProfileStatus(profile, lastOk is null ? CredentialState.Unknown : CredentialState.Valid, null, null, lastOk);
        }
    }

    public IReadOnlyList<ProfileStatus> GetStatuses()
    {
        List<string> names;
        lock (_gate) names = _profiles.Keys.Union(_halts.Keys).OrderBy(n => n).ToList();
        return names.Select(GetStatus).ToList();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _poll?.Dispose();
    }
}
