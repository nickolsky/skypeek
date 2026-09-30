using Microsoft.Data.Sqlite;

namespace Skypeek.Storage;

internal static class Schema
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE settings (key TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE targets (id INTEGER PRIMARY KEY AUTOINCREMENT, json TEXT NOT NULL);
        CREATE TABLE credential_halts (
            profile TEXT PRIMARY KEY,
            fingerprint TEXT NOT NULL,
            halted_at TEXT NOT NULL,
            error_code TEXT NOT NULL);
        CREATE TABLE catalog_items (
            target_id INTEGER NOT NULL,
            kind INTEGER NOT NULL,
            name TEXT NOT NULL,
            arn TEXT, description TEXT, type TEXT, tier TEXT, data_type TEXT, version INTEGER,
            kms_key_id TEXT, rotation_enabled INTEGER,
            last_modified TEXT, last_modified_by TEXT, last_accessed TEXT, created TEXT,
            tags_json TEXT,
            first_seen TEXT NOT NULL,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY (target_id, kind, name));
        CREATE TABLE sync_runs (
            target_id INTEGER NOT NULL,
            feature TEXT NOT NULL,
            started_at TEXT NOT NULL,
            finished_at TEXT NOT NULL,
            success INTEGER NOT NULL,
            item_count INTEGER NOT NULL,
            error TEXT,
            PRIMARY KEY (target_id, feature));
        CREATE TABLE health_snapshots (target_id INTEGER PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE request_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            profile TEXT, account_id TEXT, region TEXT,
            service TEXT NOT NULL, operation TEXT NOT NULL, parameters TEXT,
            outcome INTEGER NOT NULL, http_status INTEGER, duration_ms INTEGER NOT NULL,
            request_id TEXT, error_code TEXT, message TEXT);
        CREATE INDEX ix_request_log_ts ON request_log (ts);
        """,
        """
        ALTER TABLE request_log ADD COLUMN elevated INTEGER NOT NULL DEFAULT 0;
        """,
        """
        CREATE TABLE network_snapshots (target_id INTEGER PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL);
        """,
        """
        CREATE TABLE cost_snapshots (target_id INTEGER PRIMARY KEY, json TEXT NOT NULL, updated_at TEXT NOT NULL);
        """,
        """
        CREATE TABLE api_usage (
            month TEXT NOT NULL, profile TEXT NOT NULL, region TEXT NOT NULL, meter TEXT NOT NULL,
            calls INTEGER NOT NULL, units INTEGER NOT NULL,
            PRIMARY KEY (month, profile, region, meter));
        """,
    ];

    public static void Migrate(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar());

        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = tx;
            migrate.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
            migrate.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
