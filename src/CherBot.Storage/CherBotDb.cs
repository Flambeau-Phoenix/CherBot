using Microsoft.Data.Sqlite;

namespace CherBot.Storage;

/// <summary>
/// SQLite persistence for CherBot. One file per instance, WAL journal, pooled connections.
/// Stores engine records as JSON documents so this layer stays independent of the engine model.
/// </summary>
public sealed partial class CherBotDb
{
    private readonly string _connectionString;

    public string Path { get; }

    public CherBotDb(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
        Migrate();
    }

    internal SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public int Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public T? Scalar<T>(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, args);
        var result = cmd.ExecuteScalar();
        if (result is null || result is DBNull) return default;
        return (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var conn = Open();
        using var cmd = Command(conn, sql, args);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        work(conn, tx);
        tx.Commit();
    }

    public static SqliteCommand Command(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Migrate()
    {
        using var conn = Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);";
            create.ExecuteNonQuery();
        }

        long current;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            current = (long)read.ExecuteScalar()!;
        }

        for (var i = (int)current; i < Migrations.Length; i++)
        {
            using var tx = conn.BeginTransaction();
            using (var step = conn.CreateCommand())
            {
                step.Transaction = tx;
                step.CommandText = Migrations[i];
                step.ExecuteNonQuery();
            }
            using (var mark = conn.CreateCommand())
            {
                mark.Transaction = tx;
                mark.CommandText = "INSERT INTO schema_version(version) VALUES ($v);";
                mark.Parameters.AddWithValue("$v", i + 1);
                mark.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public int SchemaVersion => (int)(Scalar<long>("SELECT COALESCE(MAX(version), 0) FROM schema_version;"));

    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE docs(
            kind TEXT NOT NULL,
            id TEXT NOT NULL,
            name TEXT NOT NULL DEFAULT '',
            json TEXT NOT NULL,
            updated TEXT NOT NULL,
            PRIMARY KEY(kind, id));
        CREATE INDEX idx_docs_kind_name ON docs(kind, name);

        CREATE TABLE globals(
            name TEXT PRIMARY KEY,
            value_json TEXT NOT NULL,
            last_write TEXT NOT NULL);

        CREATE TABLE user_globals(
            platform TEXT NOT NULL,
            user_id TEXT NOT NULL,
            name TEXT NOT NULL,
            user_login TEXT NOT NULL DEFAULT '',
            user_name TEXT NOT NULL DEFAULT '',
            value_json TEXT NOT NULL,
            last_write TEXT NOT NULL,
            PRIMARY KEY(platform, user_id, name));
        CREATE INDEX idx_user_globals_name ON user_globals(platform, name);

        CREATE TABLE quotes(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            user_id TEXT NOT NULL DEFAULT '',
            user TEXT NOT NULL DEFAULT '',
            platform TEXT NOT NULL DEFAULT '',
            game TEXT NOT NULL DEFAULT '',
            quote TEXT NOT NULL);

        CREATE TABLE counters(
            scope TEXT NOT NULL,
            key TEXT NOT NULL,
            user_id TEXT NOT NULL DEFAULT '',
            value INTEGER NOT NULL DEFAULT 0,
            last_used TEXT,
            PRIMARY KEY(scope, key, user_id));

        CREATE TABLE runs(
            id TEXT PRIMARY KEY,
            started TEXT NOT NULL,
            action_id TEXT NOT NULL,
            action_name TEXT NOT NULL,
            trigger TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL,
            ms INTEGER NOT NULL,
            json TEXT NOT NULL);
        CREATE INDEX idx_runs_started ON runs(started);

        CREATE TABLE events(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            source TEXT NOT NULL,
            type TEXT NOT NULL,
            json TEXT NOT NULL);
        CREATE INDEX idx_events_ts ON events(ts);

        CREATE TABLE pending(
            id TEXT PRIMARY KEY,
            queued TEXT NOT NULL,
            json TEXT NOT NULL);

        CREATE TABLE secrets(
            name TEXT PRIMARY KEY,
            blob TEXT NOT NULL,
            updated TEXT NOT NULL);

        CREATE TABLE kv(
            key TEXT PRIMARY KEY,
            json TEXT NOT NULL,
            updated TEXT NOT NULL);

        CREATE TABLE user_groups(
            group_name TEXT NOT NULL,
            platform TEXT NOT NULL,
            user_id TEXT NOT NULL,
            login TEXT NOT NULL DEFAULT '',
            username TEXT NOT NULL DEFAULT '',
            PRIMARY KEY(group_name, platform, user_id));

        CREATE TABLE points(
            platform TEXT NOT NULL,
            user_id TEXT NOT NULL,
            user_name TEXT NOT NULL DEFAULT '',
            points INTEGER NOT NULL DEFAULT 0,
            updated TEXT NOT NULL,
            PRIMARY KEY(platform, user_id));
        CREATE INDEX idx_points_rank ON points(platform, points DESC);

        CREATE TABLE first_words(
            platform TEXT NOT NULL,
            user_id TEXT NOT NULL,
            user_name TEXT NOT NULL DEFAULT '',
            message TEXT NOT NULL DEFAULT '',
            ts TEXT NOT NULL,
            PRIMARY KEY(platform, user_id));
        """,
        """
        CREATE TABLE giveaway_entries(
            giveaway TEXT NOT NULL,
            platform TEXT NOT NULL,
            user_id TEXT NOT NULL,
            user_login TEXT NOT NULL DEFAULT '',
            user_name TEXT NOT NULL DEFAULT '',
            tickets INTEGER NOT NULL DEFAULT 1,
            bonus INTEGER NOT NULL DEFAULT 0,
            paid INTEGER NOT NULL DEFAULT 0,
            ts TEXT NOT NULL,
            PRIMARY KEY(giveaway, platform, user_id));

        CREATE TABLE giveaway_winners(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            giveaway TEXT NOT NULL,
            run INTEGER NOT NULL DEFAULT 0,
            platform TEXT NOT NULL,
            user_id TEXT NOT NULL,
            user_login TEXT NOT NULL DEFAULT '',
            user_name TEXT NOT NULL DEFAULT '',
            drawn_at TEXT NOT NULL,
            status TEXT NOT NULL,
            claimed_at TEXT);
        CREATE INDEX idx_giveaway_winners ON giveaway_winners(giveaway, run);

        CREATE TABLE mod_log(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            ts TEXT NOT NULL,
            platform TEXT NOT NULL DEFAULT 'twitch',
            user_id TEXT NOT NULL DEFAULT '',
            user_name TEXT NOT NULL DEFAULT '',
            filter TEXT NOT NULL,
            excerpt TEXT NOT NULL DEFAULT '',
            action TEXT NOT NULL,
            success INTEGER NOT NULL DEFAULT 1,
            detail TEXT NOT NULL DEFAULT '');
        CREATE INDEX idx_mod_log_ts ON mod_log(ts);
        """,
    ];

    internal static string Now() => DateTimeOffset.UtcNow.ToString("O");
}
