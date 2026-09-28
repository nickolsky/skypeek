using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;
using Skypeek.Storage;

namespace Skypeek.Tests;

public class VaultTests
{
    [Fact]
    public void Wrong_password_fails_and_right_password_opens()
    {
        using var dir = new TempDir();
        using (var vault = Vault.Create(dir.Path, "correct horse", "Win+Alt+A"))
            new VaultRepository(vault).SaveSettings(new AppSettings { LockoutMinutes = 7 });

        Assert.Throws<InvalidPasswordException>(() => Vault.Open(dir.Path, "wrong"));

        using var reopened = Vault.Open(dir.Path, "correct horse");
        Assert.Equal(7, new VaultRepository(reopened).LoadSettings().LockoutMinutes);
    }

    [Fact]
    public void Database_file_is_encrypted_on_disk()
    {
        using var dir = new TempDir();
        using (var vault = Vault.Create(dir.Path, "pw", "Win+Alt+A"))
            new VaultRepository(vault).SaveTarget(new Target { ProfileName = "PLAINTEXT_MARKER_PROFILE", Region = "us-east-1" });

        var bytes = File.ReadAllBytes(dir.File("vault.db"));
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("SQLite format 3", text);
        Assert.DoesNotContain("PLAINTEXT_MARKER_PROFILE", text);
    }

    [Fact]
    public void Reauth_compares_against_in_memory_key()
    {
        using var dir = new TempDir();
        using var vault = Vault.Create(dir.Path, "pw1", "Win+Alt+A");
        Assert.True(vault.VerifyPassword("pw1"));
        Assert.False(vault.VerifyPassword("pw2"));
    }

    [Fact]
    public void Change_password_rekeys_database()
    {
        using var dir = new TempDir();
        using (var vault = Vault.Create(dir.Path, "old", "Win+Alt+A"))
        {
            new VaultRepository(vault).SaveHalt(new CredentialHalt("p", "hash", DateTime.UtcNow, "ExpiredToken"));
            vault.ChangePassword("old", "new");
            Assert.True(vault.VerifyPassword("new"));
        }

        Assert.Throws<InvalidPasswordException>(() => Vault.Open(dir.Path, "old"));
        using var reopened = Vault.Open(dir.Path, "new");
        Assert.Single(new VaultRepository(reopened).LoadHalts());
    }

    [Fact]
    public void Catalog_snapshot_keeps_first_seen_and_marks_baseline()
    {
        using var dir = new TempDir();
        using var vault = Vault.Create(dir.Path, "pw", "Win+Alt+A");
        var repo = new VaultRepository(vault);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        repo.ReplaceSnapshot(1, CatalogKind.Parameter, [new CatalogItem { Name = "/a", Kind = CatalogKind.Parameter }], t0);
        repo.ReplaceSnapshot(1, CatalogKind.Parameter,
            [new CatalogItem { Name = "/a", Kind = CatalogKind.Parameter }, new CatalogItem { Name = "/b", Kind = CatalogKind.Parameter, Tags = new() { ["team"] = "x" } }],
            t0.AddHours(6));

        var items = repo.LoadAll().ToDictionary(i => i.Name);
        Assert.Equal(DateTime.MinValue, items["/a"].FirstSeen);
        Assert.Equal(t0.AddHours(6), items["/b"].FirstSeen);
        Assert.Equal("x", items["/b"].Tags["team"]);
    }

    [Fact]
    public void Request_log_roundtrip_and_purge()
    {
        using var dir = new TempDir();
        using var vault = Vault.Create(dir.Path, "pw", "Win+Alt+A");
        var repo = new VaultRepository(vault);
        repo.Append(new RequestLogEntry { TimestampUtc = DateTime.UtcNow.AddDays(-40), Service = "ssm", Operation = "GetParameter" });
        repo.Append(new RequestLogEntry { TimestampUtc = DateTime.UtcNow, Service = "ecs", Operation = "ListClusters", Outcome = RequestOutcome.Success });

        Assert.Equal(1, repo.Purge(DateTime.UtcNow.AddDays(-30)));
        var entry = Assert.Single(repo.Recent(10));
        Assert.Equal("ListClusters", entry.Operation);
    }
}
