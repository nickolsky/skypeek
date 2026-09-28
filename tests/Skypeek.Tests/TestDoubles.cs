using System.Collections.Concurrent;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;

namespace Skypeek.Tests;

internal sealed class CapturingSink : IRequestLogSink
{
    public ConcurrentQueue<RequestLogEntry> Entries { get; } = new();
    public void Write(RequestLogEntry entry) => Entries.Enqueue(entry);
}

internal sealed class MemoryHaltStore : ICredentialHaltStore
{
    public Dictionary<string, CredentialHalt> Halts { get; } = new();
    public IReadOnlyList<CredentialHalt> LoadHalts() => Halts.Values.ToList();
    public void SaveHalt(CredentialHalt halt) => Halts[halt.Profile] = halt;
    public void DeleteHalt(string profile) => Halts.Remove(profile);
}

internal sealed class FakeValidator : ICredentialValidator
{
    public Func<ProfileCredentials, ValidationResult> Result { get; set; } = c => new ValidationResult(true, c.AccountId, null, false);
    public List<string> ValidatedFingerprints { get; } = new();

    public Task<ValidationResult> ValidateAsync(ProfileCredentials credentials, string region, CancellationToken ct)
    {
        ValidatedFingerprints.Add(credentials.Fingerprint);
        return Task.FromResult(Result(credentials));
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "awsmgr-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}

internal static class CredentialsFile
{
    public const string Profile = "123456789012_AWSReadOnlyAccess";

    public static string Content(string token) => $"""
        [{Profile}]
        aws_access_key_id = ASIAEXAMPLEKEY
        aws_secret_access_key = secretExample
        aws_session_token = {token}

        [123456789012_AWSAdministratorAccess]
        aws_access_key_id = ASIAADMIN
        aws_secret_access_key = adminSecret
        aws_session_token = adminToken
        """;
}
