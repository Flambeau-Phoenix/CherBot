using CherBot.Core.State;
using CherBot.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Community;

public sealed record MigrationReport(int Points, int FirstWords, int Quotes, int Globals, int UserGlobals, List<string> Skipped);

/// <summary>One-off import of the Python prototype's cherbot.db (points, first words, quotes, persisted globals).</summary>
public sealed class PythonMigration(CherBotDb db, VariableService vars, ILogger<PythonMigration> log)
{
    public MigrationReport Run(string pythonDbPath)
    {
        if (!File.Exists(pythonDbPath)) throw new FileNotFoundException("Python cherbot.db not found", pythonDbPath);
        var skipped = new List<string>();
        int points = 0, firstWords = 0, quotes = 0, globals = 0, userGlobals = 0;

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = pythonDbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        var tables = new HashSet<string>(Read(conn, "SELECT name FROM sqlite_master WHERE type='table'", r => r.GetString(0)));

        if (tables.Contains("leaderboard"))
            foreach (var (id, name, pts) in Read(conn, "SELECT user_id, COALESCE(user_name,''), points FROM leaderboard", r => (r.GetString(0), r.GetString(1), r.GetInt64(2))))
            {
                db.SetPoints("twitch", id, name, pts);
                points++;
            }
        else skipped.Add("leaderboard");

        if (tables.Contains("first_words"))
        {
            foreach (var (id, name, word) in Read(conn, "SELECT user_id, COALESCE(user_name,''), COALESCE(word,'') FROM first_words ORDER BY ts", r => (r.GetString(0), r.GetString(1), r.GetString(2))))
            {
                if (db.TryAddFirstWord("twitch", id, name, word)) firstWords++;
            }
        }
        else skipped.Add("first_words");

        if (tables.Contains("quotes"))
            foreach (var (quote, by) in Read(conn, "SELECT quote, COALESCE(added_by,'') FROM quotes ORDER BY id", r => (r.GetString(0), r.GetString(1))))
            {
                db.AddQuote("", by, "twitch", "", quote);
                quotes++;
            }
        else skipped.Add("quotes");

        if (tables.Contains("globals"))
            foreach (var (key, value, persisted) in Read(conn, "SELECT key, value, persisted FROM globals", r => (r.GetString(0), r.GetString(1), r.GetInt64(2))))
            {
                if (persisted == 0) continue;
                vars.SetGlobal(key, CherBot.Core.CherBotJson.AutoType(value), persisted: true);
                globals++;
            }
        else skipped.Add("globals");

        if (tables.Contains("user_globals"))
            foreach (var (platform, id, key, value, persisted) in Read(conn, "SELECT platform, user_id, key, value, persisted FROM user_globals",
                         r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4))))
            {
                if (persisted == 0) continue;
                vars.SetUser(platform, id, key, CherBot.Core.CherBotJson.AutoType(value), persisted: true);
                userGlobals++;
            }
        else skipped.Add("user_globals");

        log.LogInformation("Python migration: {Points} points, {FirstWords} first words, {Quotes} quotes, {Globals} globals, {UserGlobals} user globals",
            points, firstWords, quotes, globals, userGlobals);
        return new MigrationReport(points, firstWords, quotes, globals, userGlobals, skipped);
    }

    private static List<T> Read<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
