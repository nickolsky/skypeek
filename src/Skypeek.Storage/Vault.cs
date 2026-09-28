using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Skypeek.Storage;

public sealed class InvalidPasswordException() : Exception("Wrong master password.");

/// <summary>Non-secret metadata stored next to the database (KDF parameters and bootstrap preferences).</summary>
public sealed class VaultMeta
{
    public int Version { get; set; } = 1;
    public string Kdf { get; set; } = "argon2id";
    public string Salt { get; set; } = "";
    public int MemoryKb { get; set; } = 65536;
    public int Iterations { get; set; } = 3;
    public int Parallelism { get; set; } = 2;

    /// <summary>Needed before unlock (the hotkey that opens the unlock prompt), so it cannot live inside the vault.</summary>
    public string Hotkey { get; set; } = "Win+Alt+A";
}

public static class KeyDerivation
{
    public static byte[] DeriveKey(string password, VaultMeta meta)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = Convert.FromBase64String(meta.Salt),
            MemorySize = meta.MemoryKb,
            Iterations = meta.Iterations,
            DegreeOfParallelism = meta.Parallelism,
        };
        return argon.GetBytes(32);
    }
}

/// <summary>
/// SQLCipher-encrypted SQLite database. The raw 256-bit key is derived from the master password with Argon2id and kept
/// in memory while the app runs so background refresh can write even when the UI is locked.
/// </summary>
public sealed class Vault : IDisposable
{
    private const string DbFile = "vault.db";
    private const string MetaFile = "vault.meta";
    private const string PendingMetaFile = "vault.meta.new";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static int _sqliteInitialized;

    private readonly string _directory;
    private readonly object _gate = new();
    private byte[] _key;
    private VaultMeta _meta;
    private SqliteConnection _connection;

    private Vault(string directory, VaultMeta meta, byte[] key, SqliteConnection connection)
    {
        _directory = directory;
        _meta = meta;
        _key = key;
        _connection = connection;
    }

    /// <summary>%LOCALAPPDATA%\Skypeek, or SKYPEEK_HOME when set (e.g. for a separate test vault).</summary>
    public static string DefaultDirectory =>
        Environment.GetEnvironmentVariable("SKYPEEK_HOME") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skypeek");

    /// <summary>
    /// The vault folder to use. The app was called AwsManager before 1.0: its folder is moved once so settings, lists
    /// and history are kept, and used in place if it cannot be moved (e.g. the old version is still running).
    /// </summary>
    public static string ResolveDirectory()
    {
        if (Environment.GetEnvironmentVariable("SKYPEEK_HOME") is { Length: > 0 })
            return DefaultDirectory;
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AwsManager");
        if (!Directory.Exists(legacy) || Directory.Exists(DefaultDirectory))
            return DefaultDirectory;
        try
        {
            Directory.Move(legacy, DefaultDirectory);
            return DefaultDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
    }

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, DbFile)) && File.Exists(Path.Combine(directory, MetaFile));

    public static VaultMeta? ReadMeta(string directory)
    {
        var path = Path.Combine(directory, MetaFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<VaultMeta>(File.ReadAllText(path)) : null;
    }

    public static void UpdateBootstrapHotkey(string directory, string hotkey)
    {
        var meta = ReadMeta(directory);
        if (meta is null)
            return;
        meta.Hotkey = hotkey;
        WriteMetaAtomic(directory, meta);
    }

    public static Vault Create(string directory, string password, string hotkey)
    {
        Directory.CreateDirectory(directory);
        if (Exists(directory))
            throw new InvalidOperationException("A vault already exists.");

        var meta = new VaultMeta { Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), Hotkey = hotkey };
        var key = KeyDerivation.DeriveKey(password, meta);
        var connection = OpenConnection(Path.Combine(directory, DbFile), key);
        Schema.Migrate(connection);
        WriteMetaAtomic(directory, meta);
        return new Vault(directory, meta, key, connection);
    }

    public static Vault Open(string directory, string password)
    {
        // A pending meta exists only if a password change was interrupted; try both.
        var candidates = new[] { PendingMetaFile, MetaFile }
            .Select(f => Path.Combine(directory, f))
            .Where(File.Exists)
            .Select(f => JsonSerializer.Deserialize<VaultMeta>(File.ReadAllText(f))!)
            .ToList();

        foreach (var meta in candidates)
        {
            var key = KeyDerivation.DeriveKey(password, meta);
            try
            {
                var connection = OpenConnection(Path.Combine(directory, DbFile), key);
                Schema.Migrate(connection);
                var pending = Path.Combine(directory, PendingMetaFile);
                if (File.Exists(pending))
                {
                    WriteMetaAtomic(directory, meta);
                    File.Delete(pending);
                }
                return new Vault(directory, meta, key, connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 26) // SQLITE_NOTADB: wrong key
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        throw new InvalidPasswordException();
    }

    /// <summary>Deletes the vault. Settings are lost; the catalog is re-downloaded on the next sync.</summary>
    public static void Wipe(string directory)
    {
        foreach (var file in new[] { DbFile, MetaFile, PendingMetaFile, DbFile + "-journal", DbFile + "-wal", DbFile + "-shm" })
        {
            var path = Path.Combine(directory, file);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    public VaultMeta Meta => _meta;

    /// <summary>Re-authentication while the store stays open: derive again and compare in constant time.</summary>
    public bool VerifyPassword(string password)
    {
        var candidate = KeyDerivation.DeriveKey(password, _meta);
        try
        {
            lock (_gate) return CryptographicOperations.FixedTimeEquals(candidate, _key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public void ChangePassword(string currentPassword, string newPassword)
    {
        if (!VerifyPassword(currentPassword))
            throw new InvalidPasswordException();

        var newMeta = new VaultMeta { Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), Hotkey = _meta.Hotkey };
        var newKey = KeyDerivation.DeriveKey(newPassword, newMeta);

        lock (_gate)
        {
            var pending = Path.Combine(_directory, PendingMetaFile);
            File.WriteAllText(pending, JsonSerializer.Serialize(newMeta, JsonOptions));
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA rekey = \"x'{Convert.ToHexString(newKey)}'\";";
                cmd.ExecuteNonQuery();
            }
            WriteMetaAtomic(_directory, newMeta);
            File.Delete(pending);
            CryptographicOperations.ZeroMemory(_key);
            _key = newKey;
            _meta = newMeta;
        }
    }

    public void SetHotkey(string hotkey)
    {
        lock (_gate)
        {
            _meta.Hotkey = hotkey;
            WriteMetaAtomic(_directory, _meta);
        }
    }

    /// <summary>Runs work on the single shared connection. SQLite access is serialized.</summary>
    public T Execute<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate) return work(_connection);
    }

    public void Execute(Action<SqliteConnection> work)
    {
        lock (_gate) work(_connection);
    }

    private static SqliteConnection OpenConnection(string path, byte[] key)
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 0)
            SQLitePCL.Batteries_V2.Init();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";";
            cmd.ExecuteNonQuery();
            // Forces SQLCipher to decrypt page 1; throws SQLITE_NOTADB for a wrong key.
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void WriteMetaAtomic(string directory, VaultMeta meta)
    {
        var path = Path.Combine(directory, MetaFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(meta, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
