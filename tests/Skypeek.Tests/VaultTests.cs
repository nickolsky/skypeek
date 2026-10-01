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
    public void Vault_folder_is_owner_only_on_linux_and_macos()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var dir = new TempDir();
        var vaultDir = Path.Combine(dir.Path, "vault");
        Directory.CreateDirectory(vaultDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        using (Vault.Create(vaultDir, "pw", "Win+Alt+A"))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(vaultDir));

        // A folder loosened later (e.g. copied from elsewhere) is tightened again on open.
        File.SetUnixFileMode(vaultDir, File.GetUnixFileMode(vaultDir) | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        using var reopened = Vault.Open(vaultDir, "pw");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(vaultDir));
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
    public void Update_check_preference_is_readable_while_locked_and_survives_a_password_change()
    {
        using var dir = new TempDir();
        using (var vault = Vault.Create(dir.Path, "old", "Win+Alt+A"))
        {
            Assert.True(Vault.ReadMeta(dir.Path)!.CheckForUpdates);
            vault.SetCheckForUpdates(false);
            vault.ChangePassword("old", "new");
        }
        Assert.False(Vault.ReadMeta(dir.Path)!.CheckForUpdates);
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

    [Fact]
    public void Paid_calls_are_counted_per_month_and_survive_the_request_log_purge()
    {
        using var dir = new TempDir();
        using var vault = Vault.Create(dir.Path, "pw", "Win+Alt+A");
        var repo = new VaultRepository(vault);
        var now = DateTime.UtcNow;
        RequestLogEntry Call(string service, string op, int units = 1, RequestOutcome outcome = RequestOutcome.Success, string parameters = "") =>
            new() { TimestampUtc = now, Profile = "p", Region = "us-east-1", Service = service, Operation = op, Units = units, Outcome = outcome, HttpStatus = 200, Parameters = parameters };
        repo.Append(Call("cloudwatch", "GetMetricData", units: 186));
        repo.Append(Call("cloudwatch", "GetMetricData", units: 14));
        repo.Append(Call("cloudwatch", "DescribeAlarms"));
        repo.Append(Call("secretsmanager", "GetSecretValue"));
        repo.Append(Call("ssm", "GetParameter", parameters: "Name=/a, WithDecryption=True"));
        repo.Append(Call("ec2", "DescribeInstances"));
        repo.Append(Call("ce", "GetCostAndUsage", outcome: RequestOutcome.Blocked));

        var usage = repo.Usage(PaidApi.MonthOf(now));
        Assert.Equal(200, usage.Single(u => u.Meter == PaidApi.Metrics.Id).Units);
        Assert.Equal(2, usage.Single(u => u.Meter == PaidApi.Metrics.Id).Calls);
        Assert.Equal(1, usage.Single(u => u.Meter == PaidApi.CloudWatchRequests.Id).Calls);
        Assert.Equal(2, usage.Single(u => u.Meter == PaidApi.Kms.Id).Calls);
        Assert.DoesNotContain(usage, u => u.Meter == PaidApi.CostExplorer.Id); // blocked: never sent
        Assert.Equal(4, usage.Count);

        repo.Purge(now.AddDays(1));
        Assert.Equal(4, repo.Usage(PaidApi.MonthOf(now)).Count);
        // When counting began does not follow the request log's purge.
        Assert.Equal(now, repo.CountingStarted()!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(0.002, PaidApi.Cost(PaidApi.Metrics, 200), 6);
        Assert.Equal(0, PaidApi.Cost(PaidApi.Kms, 2));
    }

    [Fact]
    public void Counting_start_of_an_older_vault_is_found_by_counting_back_its_metric_calls()
    {
        using var dir = new TempDir();
        var start = new DateTime(2026, 9, 30, 9, 0, 0, DateTimeKind.Utc);
        using (var vault = Vault.Create(dir.Path, "pw", "Win+Alt+A"))
        {
            var repo = new VaultRepository(vault);
            // Logged for days before counting existed, then two counted calls.
            repo.Append(new RequestLogEntry { TimestampUtc = start.AddDays(-5), Profile = "p", Region = "r", Service = "ecs", Operation = "ListClusters", HttpStatus = 200 });
            for (var i = 0; i < 4; i++)
                repo.Append(new RequestLogEntry { TimestampUtc = start.AddMinutes(-60 + i * 30), Profile = "p", Region = "r", Service = "cloudwatch", Operation = "GetMetricData", Units = 10, HttpStatus = 200 });
            // As a version 5 vault saw it: the last two calls counted, no first_ts yet.
            vault.Execute(c =>
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE api_usage SET calls = 2, units = 20; ALTER TABLE api_usage DROP COLUMN first_ts; PRAGMA user_version = 5;";
                cmd.ExecuteNonQuery();
            });
        }

        using var reopened = Vault.Open(dir.Path, "pw");
        Assert.Equal(start, new VaultRepository(reopened).CountingStarted());
    }

    [Fact]
    public void Running_cost_estimate_follows_the_settings()
    {
        var target = new Target { Id = 1, ProfileName = "p", MetricsIntervalMinutes = 15, HealthIntervalMinutes = 5, CostExplorerEnabled = true };
        var cost = RunningCost.Estimate(target, metricsPerPoll: 186, secrets: 250, alarms: 40);
        // 186 metrics × 2,880 polls × $0.01 per 1,000.
        Assert.Equal(5.36, cost.Lines.Single(l => l.Item == PaidApi.Metrics.Name).MonthlyUsd, 2);
        Assert.Equal(1.20, cost.Lines.Single(l => l.Item == PaidApi.CostExplorer.Name).MonthlyUsd, 2);
        Assert.True(cost.Lines.Single(l => l.Item == PaidApi.CloudWatchRequests.Name).IsFree);
        target.MetricsIntervalMinutes = 5;
        Assert.Equal(16.07, RunningCost.Estimate(target, 186, 250, 40).Lines.Single(l => l.Item == PaidApi.Metrics.Name).MonthlyUsd, 2);
    }

    [Fact]
    public void Running_cost_report_applies_free_tiers_per_account_and_projects_the_month()
    {
        var now = new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc); // 10 days in, 20 to go
        var month = PaidApi.MonthOf(now);
        IReadOnlyList<ApiUsageRow> usage =
        [
            new(month, "a-readonly", "us-east-1", PaidApi.Metrics.Id, 1000, 100_000),        // $1.00
            new(month, "a-readonly", "eu-west-1", PaidApi.Metrics.Id, 1000, 100_000),        // $1.00
            new(month, "a-readonly", "us-east-1", PaidApi.CloudWatchRequests.Id, 600_000, 600_000),
            new(month, "a-readonly", "eu-west-1", PaidApi.CloudWatchRequests.Id, 600_000, 600_000), // 1.2M in one account: 200k billed = $2.00
            new(month, "b-readonly", "us-east-1", PaidApi.CloudWatchRequests.Id, 600_000, 600_000), // another account: free
            new("2026-08", "a-readonly", "us-east-1", PaidApi.CostExplorer.Id, 30, 30),
        ];
        var report = RunningCostReport.Build(usage, [], p => p![..1], now, null);
        Assert.Equal(4.00, report.MonthToDateUsd, 2);
        Assert.Equal(12.00, report.ProjectedUsd, 2);
        Assert.Equal(0.30, report.PreviousMonths.Single().Usd, 2);
        Assert.Equal(2, report.ThisMonth.Count);
    }
}
