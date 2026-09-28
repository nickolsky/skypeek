using Skypeek.Aws;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;

namespace Skypeek.Tests;

public class CredentialMonitorTests
{
    private static async Task<(CredentialMonitor Monitor, MemoryHaltStore Store, FakeValidator Validator, string Path)> Create(TempDir dir, string token, MemoryHaltStore? store = null)
    {
        var path = dir.File("credentials");
        await File.WriteAllTextAsync(path, CredentialsFile.Content(token));
        store ??= new MemoryHaltStore();
        var validator = new FakeValidator();
        var monitor = new CredentialMonitor(path, store, validator, _ => "us-east-1");
        await monitor.StartAsync(watch: false);
        return (monitor, store, validator, path);
    }

    [Fact]
    public void ProfileReader_parses_account_role_and_readonly_flag()
    {
        var profiles = ProfileReader.Parse(CredentialsFile.Content("t1"));

        var ro = profiles[CredentialsFile.Profile];
        Assert.Equal("123456789012", ro.AccountId);
        Assert.Equal("AWSReadOnlyAccess", ro.RoleName);
        Assert.True(ro.IsReadOnly);
        Assert.False(profiles["123456789012_AWSAdministratorAccess"].IsReadOnly);
    }

    [Fact]
    public void Fingerprint_changes_when_any_credential_part_changes()
    {
        var a = ProfileReader.ComputeFingerprint("id", "secret", "token");
        Assert.NotEqual(a, ProfileReader.ComputeFingerprint("id", "secret", "token2"));
        Assert.NotEqual(a, ProfileReader.ComputeFingerprint("id", "secret2", "token"));
        Assert.NotEqual(a, ProfileReader.ComputeFingerprint("id2", "secret", "token"));
        Assert.Equal(a, ProfileReader.ComputeFingerprint("id", "secret", "token"));
    }

    [Fact]
    public async Task Auth_failure_halts_profile_and_persists_hash()
    {
        using var dir = new TempDir();
        var (monitor, store, _, _) = await Create(dir, "expired");
        var fingerprint = monitor.Acquire(CredentialsFile.Profile).Fingerprint;

        monitor.ReportAuthFailure(CredentialsFile.Profile, fingerprint, "ExpiredTokenException");

        Assert.True(monitor.IsHalted(CredentialsFile.Profile));
        Assert.Equal(fingerprint, store.Halts[CredentialsFile.Profile].Fingerprint);
        Assert.Throws<CredentialsUnavailableException>(() => monitor.Acquire(CredentialsFile.Profile));
    }

    [Fact]
    public async Task Same_hash_after_reload_stays_halted_without_validation()
    {
        using var dir = new TempDir();
        var (monitor, _, validator, _) = await Create(dir, "expired");
        monitor.ReportAuthFailure(CredentialsFile.Profile, monitor.Acquire(CredentialsFile.Profile).Fingerprint, "ExpiredToken");

        await monitor.ReloadAsync();

        Assert.True(monitor.IsHalted(CredentialsFile.Profile));
        Assert.Empty(validator.ValidatedFingerprints);
    }

    [Fact]
    public async Task Restart_with_same_hash_stays_halted_without_any_aws_call()
    {
        using var dir = new TempDir();
        var (monitor, store, _, _) = await Create(dir, "expired");
        monitor.ReportAuthFailure(CredentialsFile.Profile, monitor.Acquire(CredentialsFile.Profile).Fingerprint, "ExpiredToken");
        monitor.Dispose();

        // "Restart": new monitor over the same persisted halts and unchanged file.
        var (restarted, _, validator, _) = await Create(dir, "expired", store);

        Assert.True(restarted.IsHalted(CredentialsFile.Profile));
        Assert.Empty(validator.ValidatedFingerprints);
    }

    [Fact]
    public async Task Changed_hash_and_successful_sts_resumes_and_removes_halt()
    {
        using var dir = new TempDir();
        var (monitor, store, validator, path) = await Create(dir, "expired");
        monitor.ReportAuthFailure(CredentialsFile.Profile, monitor.Acquire(CredentialsFile.Profile).Fingerprint, "ExpiredToken");
        string? recovered = null;
        monitor.ProfileRecovered += p => recovered = p;

        await File.WriteAllTextAsync(path, CredentialsFile.Content("fresh"));
        await monitor.ReloadAsync();

        Assert.False(monitor.IsHalted(CredentialsFile.Profile));
        Assert.Empty(store.Halts);
        Assert.Single(validator.ValidatedFingerprints);
        Assert.Equal(CredentialsFile.Profile, recovered);
        Assert.Equal(CredentialState.Valid, monitor.GetStatus(CredentialsFile.Profile).State);
    }

    [Fact]
    public async Task Changed_hash_but_sts_rejects_rehalts_with_new_hash()
    {
        using var dir = new TempDir();
        var (monitor, store, validator, path) = await Create(dir, "expired");
        monitor.ReportAuthFailure(CredentialsFile.Profile, monitor.Acquire(CredentialsFile.Profile).Fingerprint, "ExpiredToken");
        validator.Result = _ => new ValidationResult(false, null, "ExpiredToken", true);

        await File.WriteAllTextAsync(path, CredentialsFile.Content("also-expired"));
        await monitor.ReloadAsync();

        var newHash = ProfileReader.ReadFile(path)[CredentialsFile.Profile].Fingerprint;
        Assert.True(monitor.IsHalted(CredentialsFile.Profile));
        Assert.Equal(newHash, store.Halts[CredentialsFile.Profile].Fingerprint);

        // Another reload with the same (bad) credentials must not validate again.
        await monitor.ReloadAsync();
        Assert.Single(validator.ValidatedFingerprints);
    }

    [Fact]
    public async Task Account_mismatch_is_treated_as_rejected()
    {
        using var dir = new TempDir();
        var (monitor, _, validator, path) = await Create(dir, "expired");
        monitor.ReportAuthFailure(CredentialsFile.Profile, monitor.Acquire(CredentialsFile.Profile).Fingerprint, "ExpiredToken");
        validator.Result = _ => new ValidationResult(true, "999999999999", null, false);

        await File.WriteAllTextAsync(path, CredentialsFile.Content("other-account"));
        await monitor.ReloadAsync();

        Assert.True(monitor.IsHalted(CredentialsFile.Profile));
        Assert.Equal("AccountMismatch", monitor.GetStatus(CredentialsFile.Profile).ErrorCode);
    }

    [Fact]
    public async Task Gateway_skips_halted_profile_without_calling_aws()
    {
        using var dir = new TempDir();
        var (monitor, _, _, _) = await Create(dir, "expired");
        monitor.ReportAuthFailure(CredentialsFile.Profile, monitor.Acquire(CredentialsFile.Profile).Fingerprint, "ExpiredToken");
        var sink = new CapturingSink();
        using var factory = new AwsClientFactory();
        var gateway = new AwsGateway(monitor, factory, sink);
        var target = new Target { Id = 1, ProfileName = CredentialsFile.Profile, Region = "us-east-1" };

        await Assert.ThrowsAsync<CredentialsUnavailableException>(() => gateway.ListSecretsAsync(target, CancellationToken.None));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(RequestOutcome.Skipped, entry.Outcome);
        Assert.Contains("waiting for the credentials file", entry.Message);
    }
}
