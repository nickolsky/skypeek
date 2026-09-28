using System.Globalization;
using System.Text.Json;
using Skypeek.Core;
using Skypeek.Core.Credentials;
using Skypeek.Core.Logging;
using Skypeek.Core.Models;
using Microsoft.Data.Sqlite;

namespace Skypeek.Storage;

/// <summary>All persistence on top of the encrypted vault.</summary>
public sealed class VaultRepository(Vault vault) :
    ISettingsStore, ICatalogStore, ISyncRunStore, IHealthStore, ICredentialHaltStore, IRequestLogStore
{
    private static readonly JsonSerializerOptions Json = new();

    // ---------------- settings & targets ----------------

    public AppSettings LoadSettings() => vault.Execute(c =>
    {
        using var cmd = Command(c, "SELECT json FROM settings WHERE key = 'app'");
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings() : new AppSettings();
    });

    public void SaveSettings(AppSettings settings) => vault.Execute(c =>
    {
        using var cmd = Command(c, "INSERT INTO settings (key, json) VALUES ('app', $json) ON CONFLICT(key) DO UPDATE SET json = excluded.json",
            ("$json", JsonSerializer.Serialize(settings, Json)));
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<Target> LoadTargets() => vault.Execute(c =>
    {
        using var cmd = Command(c, "SELECT id, json FROM targets ORDER BY id");
        using var reader = cmd.ExecuteReader();
        var list = new List<Target>();
        while (reader.Read())
        {
            var target = JsonSerializer.Deserialize<Target>(reader.GetString(1), Json)!;
            target.Id = reader.GetInt64(0);
            list.Add(target);
        }
        return (IReadOnlyList<Target>)list;
    });

    public Target SaveTarget(Target target) => vault.Execute(c =>
    {
        var copy = target.Clone();
        if (copy.Id == 0)
        {
            using var insert = Command(c, "INSERT INTO targets (json) VALUES ('{}'); SELECT last_insert_rowid();");
            copy.Id = (long)insert.ExecuteScalar()!;
        }
        using var update = Command(c, "UPDATE targets SET json = $json WHERE id = $id", ("$json", JsonSerializer.Serialize(copy, Json)), ("$id", copy.Id));
        update.ExecuteNonQuery();
        return copy;
    });

    public void DeleteTarget(long id) => vault.Execute(c =>
    {
        using var tx = c.BeginTransaction();
        foreach (var sql in new[]
                 {
                     "DELETE FROM targets WHERE id = $id",
                     "DELETE FROM catalog_items WHERE target_id = $id",
                     "DELETE FROM sync_runs WHERE target_id = $id",
                     "DELETE FROM health_snapshots WHERE target_id = $id",
                 })
        {
            using var cmd = Command(c, sql, ("$id", id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    });

    // ---------------- catalog ----------------

    public IReadOnlyList<CatalogItem> LoadAll() => vault.Execute(c =>
    {
        using var cmd = Command(c, """
            SELECT target_id, kind, name, arn, description, type, tier, data_type, version, kms_key_id, rotation_enabled,
                   last_modified, last_modified_by, last_accessed, created, tags_json, first_seen, fetched_at
            FROM catalog_items
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<CatalogItem>();
        while (r.Read())
        {
            list.Add(new CatalogItem
            {
                TargetId = r.GetInt64(0),
                Kind = (CatalogKind)r.GetInt32(1),
                Name = r.GetString(2),
                Arn = Str(r, 3),
                Description = Str(r, 4),
                Type = Str(r, 5),
                Tier = Str(r, 6),
                DataType = Str(r, 7),
                Version = r.IsDBNull(8) ? null : r.GetInt64(8),
                KmsKeyId = Str(r, 9),
                RotationEnabled = r.IsDBNull(10) ? null : r.GetInt64(10) != 0,
                LastModified = Date(r, 11),
                LastModifiedBy = Str(r, 12),
                LastAccessed = Date(r, 13),
                Created = Date(r, 14),
                Tags = Str(r, 15) is { } tags ? JsonSerializer.Deserialize<Dictionary<string, string>>(tags, Json) ?? new() : new(),
                FirstSeen = Date(r, 16) ?? DateTime.MinValue,
                FetchedAt = Date(r, 17) ?? DateTime.MinValue,
            });
        }
        return (IReadOnlyList<CatalogItem>)list;
    });

    public void ReplaceSnapshot(long targetId, CatalogKind kind, IReadOnlyList<CatalogItem> items, DateTime now) => vault.Execute(c =>
    {
        using var tx = c.BeginTransaction();

        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var existing = Command(c, "SELECT name, first_seen FROM catalog_items WHERE target_id = $t AND kind = $k", ("$t", targetId), ("$k", (int)kind)))
        {
            existing.Transaction = tx;
            using var r = existing.ExecuteReader();
            while (r.Read())
                firstSeen[r.GetString(0)] = r.GetString(1);
        }

        // Items in the first ever snapshot are the baseline (MinValue) rather than "new".
        var hadSnapshot = firstSeen.Count > 0;
        var newStamp = hadSnapshot ? Iso(now) : Iso(DateTime.MinValue);

        using (var delete = Command(c, "DELETE FROM catalog_items WHERE target_id = $t AND kind = $k", ("$t", targetId), ("$k", (int)kind)))
        {
            delete.Transaction = tx;
            delete.ExecuteNonQuery();
        }

        using var insert = Command(c, """
            INSERT OR REPLACE INTO catalog_items
                (target_id, kind, name, arn, description, type, tier, data_type, version, kms_key_id, rotation_enabled,
                 last_modified, last_modified_by, last_accessed, created, tags_json, first_seen, fetched_at)
            VALUES ($t, $k, $name, $arn, $desc, $type, $tier, $dt, $ver, $kms, $rot, $lm, $lmb, $la, $cr, $tags, $fs, $fa)
            """);
        insert.Transaction = tx;
        foreach (var p in new[] { "$t", "$k", "$name", "$arn", "$desc", "$type", "$tier", "$dt", "$ver", "$kms", "$rot", "$lm", "$lmb", "$la", "$cr", "$tags", "$fs", "$fa" })
            insert.Parameters.Add(new SqliteParameter(p, DBNull.Value));

        foreach (var item in items)
        {
            Set(insert, "$t", targetId);
            Set(insert, "$k", (int)kind);
            Set(insert, "$name", item.Name);
            Set(insert, "$arn", item.Arn);
            Set(insert, "$desc", item.Description);
            Set(insert, "$type", item.Type);
            Set(insert, "$tier", item.Tier);
            Set(insert, "$dt", item.DataType);
            Set(insert, "$ver", item.Version);
            Set(insert, "$kms", item.KmsKeyId);
            Set(insert, "$rot", item.RotationEnabled is null ? null : item.RotationEnabled.Value ? 1 : 0);
            Set(insert, "$lm", IsoOrNull(item.LastModified));
            Set(insert, "$lmb", item.LastModifiedBy);
            Set(insert, "$la", IsoOrNull(item.LastAccessed));
            Set(insert, "$cr", IsoOrNull(item.Created));
            Set(insert, "$tags", item.Tags.Count > 0 ? JsonSerializer.Serialize(item.Tags, Json) : null);
            Set(insert, "$fs", firstSeen.GetValueOrDefault(item.Name) ?? newStamp);
            Set(insert, "$fa", Iso(now));
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    });

    void ICatalogStore.DeleteTarget(long targetId) => DeleteTarget(targetId);

    // ---------------- sync runs ----------------

    public void Record(SyncRun run) => vault.Execute(c =>
    {
        using var cmd = Command(c, """
            INSERT INTO sync_runs (target_id, feature, started_at, finished_at, success, item_count, error)
            VALUES ($t, $f, $s, $e, $ok, $n, $err)
            ON CONFLICT(target_id, feature) DO UPDATE SET
                started_at = excluded.started_at, finished_at = excluded.finished_at, success = excluded.success,
                item_count = excluded.item_count, error = excluded.error
            """,
            ("$t", run.TargetId), ("$f", run.Feature), ("$s", Iso(run.StartedAt)), ("$e", Iso(run.FinishedAt)),
            ("$ok", run.Success ? 1 : 0), ("$n", run.ItemCount), ("$err", run.Error));
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<SyncRun> LatestRuns() => vault.Execute(c =>
    {
        using var cmd = Command(c, "SELECT target_id, feature, started_at, finished_at, success, item_count, error FROM sync_runs");
        using var r = cmd.ExecuteReader();
        var list = new List<SyncRun>();
        while (r.Read())
            list.Add(new SyncRun(r.GetInt64(0), r.GetString(1), Date(r, 2)!.Value, Date(r, 3)!.Value, r.GetInt64(4) != 0, r.GetInt32(5), Str(r, 6)));
        return (IReadOnlyList<SyncRun>)list;
    });

    // ---------------- health ----------------

    public void Save(long targetId, TargetHealth health)
    {
        var json = JsonSerializer.Serialize(health, Json);
        vault.Execute(c =>
        {
            using var cmd = Command(c, """
                INSERT INTO health_snapshots (target_id, json, updated_at) VALUES ($t, $j, $u)
                ON CONFLICT(target_id) DO UPDATE SET json = excluded.json, updated_at = excluded.updated_at
                """, ("$t", targetId), ("$j", json), ("$u", Iso(DateTime.UtcNow)));
            cmd.ExecuteNonQuery();
        });
    }

    IReadOnlyList<TargetHealth> IHealthStore.LoadAll() => vault.Execute(c =>
    {
        using var cmd = Command(c, "SELECT json FROM health_snapshots");
        using var r = cmd.ExecuteReader();
        var list = new List<TargetHealth>();
        while (r.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<TargetHealth>(r.GetString(0), Json) is { } h)
                    list.Add(h);
            }
            catch (JsonException)
            {
                // Snapshot from an older format; it is rebuilt on the next poll.
            }
        }
        return (IReadOnlyList<TargetHealth>)list;
    });

    // ---------------- credential halts ----------------

    public IReadOnlyList<CredentialHalt> LoadHalts() => vault.Execute(c =>
    {
        using var cmd = Command(c, "SELECT profile, fingerprint, halted_at, error_code FROM credential_halts");
        using var r = cmd.ExecuteReader();
        var list = new List<CredentialHalt>();
        while (r.Read())
            list.Add(new CredentialHalt(r.GetString(0), r.GetString(1), Date(r, 2)!.Value, r.GetString(3)));
        return (IReadOnlyList<CredentialHalt>)list;
    });

    public void SaveHalt(CredentialHalt halt) => vault.Execute(c =>
    {
        using var cmd = Command(c, """
            INSERT INTO credential_halts (profile, fingerprint, halted_at, error_code) VALUES ($p, $f, $h, $e)
            ON CONFLICT(profile) DO UPDATE SET fingerprint = excluded.fingerprint, halted_at = excluded.halted_at, error_code = excluded.error_code
            """, ("$p", halt.Profile), ("$f", halt.Fingerprint), ("$h", Iso(halt.HaltedAtUtc)), ("$e", halt.ErrorCode));
        cmd.ExecuteNonQuery();
    });

    public void DeleteHalt(string profile) => vault.Execute(c =>
    {
        using var cmd = Command(c, "DELETE FROM credential_halts WHERE profile = $p", ("$p", profile));
        cmd.ExecuteNonQuery();
    });

    // ---------------- request log ----------------

    public void Append(RequestLogEntry e) => vault.Execute(c =>
    {
        using var cmd = Command(c, """
            INSERT INTO request_log (ts, profile, account_id, region, service, operation, parameters, outcome, http_status, duration_ms, request_id, error_code, message, elevated)
            VALUES ($ts, $p, $a, $r, $s, $o, $params, $out, $http, $ms, $rid, $err, $msg, $el);
            SELECT last_insert_rowid();
            """,
            ("$ts", Iso(e.TimestampUtc)), ("$p", e.Profile), ("$a", e.AccountId), ("$r", e.Region), ("$s", e.Service), ("$o", e.Operation),
            ("$params", e.Parameters), ("$out", (int)e.Outcome), ("$http", e.HttpStatus), ("$ms", e.DurationMs), ("$rid", e.RequestId),
            ("$err", e.ErrorCode), ("$msg", e.Message), ("$el", e.Elevated ? 1 : 0));
        e.Id = (long)cmd.ExecuteScalar()!;
    });

    public IReadOnlyList<RequestLogEntry> Recent(int limit) => vault.Execute(c =>
    {
        using var cmd = Command(c, """
            SELECT id, ts, profile, account_id, region, service, operation, parameters, outcome, http_status, duration_ms, request_id, error_code, message, elevated
            FROM request_log ORDER BY id DESC LIMIT $n
            """, ("$n", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<RequestLogEntry>();
        while (r.Read())
        {
            list.Add(new RequestLogEntry
            {
                Id = r.GetInt64(0),
                TimestampUtc = Date(r, 1)!.Value,
                Profile = Str(r, 2),
                AccountId = Str(r, 3),
                Region = Str(r, 4),
                Service = r.GetString(5),
                Operation = r.GetString(6),
                Parameters = Str(r, 7) ?? "",
                Outcome = (RequestOutcome)r.GetInt32(8),
                HttpStatus = r.IsDBNull(9) ? null : r.GetInt32(9),
                DurationMs = r.GetInt64(10),
                RequestId = Str(r, 11),
                ErrorCode = Str(r, 12),
                Message = Str(r, 13),
                Elevated = r.GetInt64(14) != 0,
            });
        }
        return (IReadOnlyList<RequestLogEntry>)list;
    });

    public int Purge(DateTime olderThanUtc) => vault.Execute(c =>
    {
        using var cmd = Command(c, "DELETE FROM request_log WHERE ts < $ts", ("$ts", Iso(olderThanUtc)));
        return cmd.ExecuteNonQuery();
    });

    // ---------------- helpers ----------------

    private static SqliteCommand Command(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static void Set(SqliteCommand cmd, string name, object? value) => cmd.Parameters[name].Value = value ?? DBNull.Value;

    private static string Iso(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString("O", CultureInfo.InvariantCulture);

    private static string? IsoOrNull(DateTime? value) => value is null ? null : Iso(value.Value);

    private static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static DateTime? Date(SqliteDataReader r, int i) =>
        r.IsDBNull(i) ? null : DateTime.Parse(r.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
