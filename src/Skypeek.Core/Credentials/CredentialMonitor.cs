namespace Skypeek.Core.Credentials;

public sealed record CredentialHalt(string Profile, string Fingerprint, DateTime HaltedAtUtc, string ErrorCode);

public enum CredentialState
{
    Unknown,
    Valid,
    Halted,
    Validating,
    Missing,
    /// <summary>SSO profile without a valid cached token: run <c>aws sso login</c>.</summary>
    SignInRequired,
}

public sealed record ProfileStatus(string Profile, CredentialState State, DateTime? HaltedAtUtc, string? ErrorCode, DateTime? LastSuccessUtc);

public sealed class CredentialsUnavailableException(string profile, string reason) : Exception($"Profile '{profile}': {reason}")
{
    public string Profile { get; } = profile;
    public string Reason { get; } = reason;
}

/// <summary>
/// Tracks credential health per profile (credentials file profiles and SSO profiles from the config file). When AWS rejects a profile's credentials the profile is halted and the
/// hash of the rejected credentials is persisted. No further calls are made for that profile until the credentials
/// file contains different credentials for it (hash change), which are then verified with sts:GetCallerIdentity.
/// </summary>
public sealed class CredentialMonitor : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    /// <summary>Renew a renewable SSO sign-in this long before its token runs out (the CLI renews within 15 minutes).</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(10);
    /// <summary>At most one renewal attempt per sign-in this often.</summary>
    public static readonly TimeSpan RenewRetry = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan RenewAfterExpiryRetry = TimeSpan.FromMinutes(30);

    private readonly string _path;
    private readonly string? _configPath;
    private readonly string? _ssoCacheDirectory;
    private readonly ICredentialHaltStore _store;
    private readonly ICredentialValidator _validator;
    private readonly Func<string, string?> _regionResolver;
    private readonly object _gate = new();
    private readonly Dictionary<string, CredentialHalt> _halts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastSuccess = new(StringComparer.Ordinal);
    private readonly HashSet<string> _validating = new(StringComparer.Ordinal);
    /// <summary>SSO profiles by whether they were signed in at the last reload (sign-in ended / restored events).</summary>
    private readonly Dictionary<string, bool> _signedIn = new(StringComparer.Ordinal);
    /// <summary>Last renewal attempt per token cache file (one sign-in can serve many profiles).</summary>
    private readonly Dictionary<string, DateTime> _renewAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _renewing = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _expiry;

    private IReadOnlyDictionary<string, ProfileCredentials> _profiles = new Dictionary<string, ProfileCredentials>();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounce;
    private Timer? _poll;

    /// <param name="configPath">~/.aws/config for SSO profiles; null reads only the credentials file.</param>
    /// <param name="ssoCacheDirectory">Where <c>aws sso login</c> caches tokens (~/.aws/sso/cache).</param>
    public CredentialMonitor(string path, ICredentialHaltStore store, ICredentialValidator validator, Func<string, string?> regionResolver,
        string? configPath = null, string? ssoCacheDirectory = null)
    {
        _path = path;
        _configPath = configPath;
        _ssoCacheDirectory = ssoCacheDirectory;
        _store = store;
        _validator = validator;
        _regionResolver = regionResolver;
    }

    public event Action<ProfileStatus>? ProfileHalted;
    public event Action<string>? ProfileRecovered;
    public event Action? StatusChanged;
    /// <summary>SSO profiles whose sign-in ended (token expired and could not be renewed, or signed out).</summary>
    public event Action<IReadOnlyList<string>>? SignInEnded;
    /// <summary>SSO profiles signed in again (after <see cref="SignInEnded"/>).</summary>
    public event Action<IReadOnlyList<string>>? SignInRestored;

    /// <summary>
    /// Renews a renewable SSO sign-in (profile name → task that finishes once the CLI is done); null or a false
    /// <see cref="RenewEnabled"/> turns renewal off. The monitor re-reads the token cache afterwards.
    /// </summary>
    public Func<string, SsoProfile, Task>? Renewer { get; set; }
    public Func<bool> RenewEnabled { get; set; } = () => true;
    /// <summary>For tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public string CredentialsPath => _path;
    public string? ConfigPath => _configPath;

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
            Watch(Path.GetDirectoryName(_path), Path.GetFileName(_path));
            if (_configPath is not null)
                Watch(Path.GetDirectoryName(_configPath), Path.GetFileName(_configPath));
            // A new `aws sso login` rewrites the token file.
            if (_ssoCacheDirectory is not null)
                Watch(_ssoCacheDirectory, "*.json");
            _debounce = new Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
            // Sign-ins end at a known time: re-check then (and shortly before, to renew) instead of waiting for the poll.
            _expiry = new Timer(_ => _ = ReloadAsync(), null, Timeout.Infinite, Timeout.Infinite);
            // Fallback in case the watcher misses an event (network drives, atomic replace by some tools).
            _poll = new Timer(_ => _ = ReloadAsync(), null, PollInterval, PollInterval);
        }

        return ReloadAsync();
    }

    private void Watch(string? directory, string filter)
    {
        if (directory is null || !Directory.Exists(directory))
            return;
        var watcher = new FileSystemWatcher(directory, filter)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        watcher.Changed += (_, _) => ScheduleReload();
        watcher.Created += (_, _) => ScheduleReload();
        watcher.Renamed += (_, _) => ScheduleReload();
        _watchers.Add(watcher);
    }

    private void ScheduleReload() => _debounce?.Change(DebounceDelay, Timeout.InfiniteTimeSpan);

    /// <summary>Re-reads the credentials file and validates halted profiles whose credentials hash changed.</summary>
    public Task ReloadAsync()
    {
        IReadOnlyDictionary<string, ProfileCredentials> profiles;
        try
        {
            profiles = ProfileReader.ReadAll(_path, _configPath, _ssoCacheDirectory);
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }

        var toValidate = new List<string>();
        var ended = new List<string>();
        var restored = new List<string>();
        var toRenew = new List<(string Profile, SsoProfile Sso)>();
        var now = Clock();
        DateTime? nextCheck = null;
        lock (_gate)
        {
            _profiles = profiles;
            foreach (var halt in _halts.Values)
                if (profiles.TryGetValue(halt.Profile, out var creds) && creds.Fingerprint != halt.Fingerprint)
                    toValidate.Add(halt.Profile);

            var renewing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, creds) in profiles)
            {
                if (creds.Sso is not { } sso)
                {
                    _signedIn.Remove(name);
                    continue;
                }
                var signedIn = sso.IsSignedIn(now);
                // Renew shortly before the end, and keep trying (throttled) after it while the CLI could still renew.
                var dueForRenewal = sso.CanRenew && sso.TokenExpiresUtc is { } expires && expires - now < RenewBefore;
                var mayRenew = dueForRenewal && Renewer is not null && RenewEnabled()
                    && (!_renewAttempts.TryGetValue(sso.TokenCacheFile, out var last)
                        // Every few minutes before the end; once it has passed, now and then (e.g. after the PC slept).
                        || now - last >= (sso.TokenExpiresUtc > now ? RenewRetry : RenewAfterExpiryRetry));
                if (mayRenew && !_renewing.Contains(sso.TokenCacheFile) && renewing.Add(sso.TokenCacheFile))
                    toRenew.Add((name, sso));

                var was = _signedIn.TryGetValue(name, out var previous) ? previous : (bool?)null;
                if (!signedIn && (mayRenew || _renewing.Contains(sso.TokenCacheFile)))
                    continue; // decided after the renewal attempt
                _signedIn[name] = signedIn;
                if (was != false && !signedIn)
                    ended.Add(name);
                else if (was == false && signedIn)
                    restored.Add(name);

                if (sso.TokenExpiresUtc is { } end && signedIn)
                {
                    var check = sso.CanRenew && end - RenewBefore > now ? end - RenewBefore : end.AddMinutes(-1);
                    if (check > now && (nextCheck is null || check < nextCheck))
                        nextCheck = check;
                }
            }
            foreach (var file in renewing)
            {
                _renewAttempts[file] = now;
                _renewing.Add(file);
            }
        }
        if (nextCheck is { } at)
            _expiry?.Change(at - now + TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        else if (toRenew.Count == 0)
            _expiry?.Change(Timeout.Infinite, Timeout.Infinite);

        StatusChanged?.Invoke();
        if (ended.Count > 0)
            SignInEnded?.Invoke(ended);
        if (restored.Count > 0)
            SignInRestored?.Invoke(restored);
        return Task.WhenAll(toValidate.Select(ValidateAsync).Concat(toRenew.Select(r => RenewAsync(r.Profile, r.Sso))));
    }

    private async Task RenewAsync(string profile, SsoProfile sso)
    {
        try
        {
            await Renewer!(profile, sso).ConfigureAwait(false);
        }
        catch
        {
            // The CLI missing or failing is the same as a sign-in that cannot be renewed: the user signs in again.
        }
        finally
        {
            lock (_gate)
                _renewing.Remove(sso.TokenCacheFile);
        }
        // Either the CLI wrote a fresh token, or the sign-in has really ended (reported now, as the attempt is recent).
        await ReloadAsync().ConfigureAwait(false);
    }

    /// <summary>Returns the current credentials or throws when the profile is missing or halted.</summary>
    public ProfileCredentials Acquire(string profile)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(profile, out var creds))
                throw new CredentialsUnavailableException(profile, "not found in the credentials or config file");

            // Nothing to send to AWS without a token; no call is made until `aws sso login` writes one.
            if (creds.Sso is { } sso && !sso.IsSignedIn(DateTime.UtcNow))
                throw new CredentialsUnavailableException(profile, SignInMessage(profile, sso));

            if (_halts.TryGetValue(profile, out var halt))
            {
                if (halt.Fingerprint == creds.Fingerprint)
                    throw new CredentialsUnavailableException(profile, $"credentials rejected ({halt.ErrorCode}) at {halt.HaltedAtUtc.ToLocalTime():g}; waiting for the credentials file to be updated");
                throw new CredentialsUnavailableException(profile, "validating updated credentials");
            }

            return creds;
        }
    }

    public static string SignInMessage(string profile, SsoProfile sso) =>
        (sso.AccessToken is null ? "not signed in to AWS SSO" : $"AWS SSO sign-in expired at {sso.TokenExpiresUtc?.ToLocalTime():g}")
        + $"; run `aws sso login --profile {profile}`";

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
            if (!_profiles.TryGetValue(profile, out var creds))
                return new ProfileStatus(profile, CredentialState.Missing, null, null, lastOk);
            if (creds.Sso is { } sso && !sso.IsSignedIn(DateTime.UtcNow))
                return new ProfileStatus(profile, CredentialState.SignInRequired, sso.TokenExpiresUtc, sso.AccessToken is null ? "not signed in" : "SSO session expired", lastOk);
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
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _debounce?.Dispose();
        _poll?.Dispose();
        _expiry?.Dispose();
    }
}
