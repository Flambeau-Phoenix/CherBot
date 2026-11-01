using Microsoft.Data.Sqlite;

namespace CherBot.Storage;

public sealed record StreamSession(string BroadcasterId, string StreamId, string StartedAt, bool Live, string? WinnerId, string? WinnerName);
public sealed record PointContext(string BroadcasterId, string StreamId, bool RecordHistory);
public sealed record HistoryRow(long Id, string Timestamp, string UserId, string UserName, string StreamId, string Kind, string Detail, long Amount);
public sealed record TrackedUser(string UserId, string Login, string UserName, string Role, string LastSeen, long Messages, long Commands, long Streams);

public sealed partial class CherBotDb
{
    private const string LiveSchema = """
        CREATE TABLE stream_sessions(
            broadcaster TEXT NOT NULL, stream_id TEXT NOT NULL, started TEXT NOT NULL, live INTEGER NOT NULL,
            winner_id TEXT, winner_name TEXT, PRIMARY KEY(broadcaster,stream_id));
        CREATE INDEX idx_sessions_live ON stream_sessions(broadcaster,live);
        CREATE TABLE user_activity(
            broadcaster TEXT NOT NULL, user_id TEXT NOT NULL, stream_id TEXT NOT NULL, login TEXT NOT NULL,
            user_name TEXT NOT NULL, role TEXT NOT NULL, last_seen TEXT NOT NULL,
            messages INTEGER NOT NULL DEFAULT 0, commands INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(broadcaster,user_id,stream_id));
        CREATE INDEX idx_activity_last ON user_activity(broadcaster,last_seen);
        CREATE TABLE user_history(
            id INTEGER PRIMARY KEY AUTOINCREMENT, broadcaster TEXT NOT NULL, stream_id TEXT NOT NULL,
            user_id TEXT NOT NULL, user_name TEXT NOT NULL, ts TEXT NOT NULL, kind TEXT NOT NULL,
            detail TEXT NOT NULL DEFAULT '', amount INTEGER NOT NULL DEFAULT 0, source_id TEXT);
        CREATE UNIQUE INDEX idx_history_source ON user_history(broadcaster,source_id) WHERE source_id IS NOT NULL;
        CREATE INDEX idx_history_user ON user_history(broadcaster,user_id,ts);
        CREATE INDEX idx_history_ts ON user_history(ts);
        CREATE TABLE point_transactions(
            id INTEGER PRIMARY KEY AUTOINCREMENT, broadcaster TEXT NOT NULL, stream_id TEXT NOT NULL,
            platform TEXT NOT NULL, user_id TEXT NOT NULL, user_name TEXT NOT NULL,
            ts TEXT NOT NULL, reason TEXT NOT NULL, delta INTEGER NOT NULL, balance INTEGER NOT NULL);
        CREATE INDEX idx_point_history ON point_transactions(broadcaster,user_id,ts);
        CREATE INDEX idx_point_period ON point_transactions(broadcaster,ts,reason);
        """;

    // Supplied by the host's tracking service. Storage has no dependency on Twitch or the engine.
    public Func<PointContext>? PointsContext { get; set; }
    public string? PointsHistoryError { get; private set; }

    public StreamSession? GetStreamSession(string broadcaster) => Query(
        "SELECT broadcaster,stream_id,started,live,winner_id,winner_name FROM stream_sessions WHERE broadcaster=$b ORDER BY started DESC LIMIT 1",
        r => new StreamSession(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3) != 0,
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)), ("$b", broadcaster)).FirstOrDefault();

    public bool StartStream(string broadcaster, string streamId, string startedAt)
    {
        var added = false;
        InTransaction((conn, tx) =>
        {
            using var latest = Command(conn, "SELECT started FROM stream_sessions WHERE broadcaster=$b ORDER BY started DESC LIMIT 1", ("$b", broadcaster));
            latest.Transaction = tx;
            var previousStart = latest.ExecuteScalar() as string;
            if (DateTimeOffset.TryParse(previousStart, out var previous) && DateTimeOffset.TryParse(startedAt, out var incoming) && incoming < previous) return;
            using var insert = Command(conn, "INSERT OR IGNORE INTO stream_sessions(broadcaster,stream_id,started,live) VALUES($b,$s,$t,1)",
                ("$b", broadcaster), ("$s", streamId), ("$t", startedAt));
            insert.Transaction = tx;
            added = insert.ExecuteNonQuery() != 0;
            using var update = Command(conn, "UPDATE stream_sessions SET live=CASE WHEN stream_id=$s THEN 1 ELSE 0 END WHERE broadcaster=$b",
                ("$b", broadcaster), ("$s", streamId));
            update.Transaction = tx;
            update.ExecuteNonQuery();
            if (added)
            {
                using var reset = Command(conn, "DELETE FROM first_words");
                reset.Transaction = tx;
                reset.ExecuteNonQuery();
            }
        });
        return added;
    }

    public void EndStream(string broadcaster) => Execute("UPDATE stream_sessions SET live=0 WHERE broadcaster=$b", ("$b", broadcaster));

    public bool ClaimFirst(string broadcaster, string streamId, string userId, string userName, long bonus)
    {
        var won = false;
        InTransaction((conn, tx) =>
        {
            using var claim = Command(conn, "UPDATE stream_sessions SET winner_id=$u,winner_name=$n WHERE broadcaster=$b AND stream_id=$s AND live=1 AND winner_id IS NULL",
                ("$b", broadcaster), ("$s", streamId), ("$u", userId), ("$n", userName));
            claim.Transaction = tx;
            won = claim.ExecuteNonQuery() == 1;
            if (won && bonus != 0) ChangePoints(conn, tx, "twitch", userId, userName, bonus, false, "first");
        });
        return won;
    }

    private long ChangePoints(SqliteConnection conn, SqliteTransaction tx, string platform, string userId, string name, long value, bool set, string reason)
    {
        using var read = Command(conn, "SELECT points FROM points WHERE platform=$p AND user_id=$u", ("$p", platform), ("$u", userId));
        read.Transaction = tx;
        var before = Convert.ToInt64(read.ExecuteScalar() ?? 0L);
        var balance = set ? value : checked(before + value);
        using var write = Command(conn, """
            INSERT INTO points(platform,user_id,user_name,points,updated) VALUES($p,$u,$n,$v,$t)
            ON CONFLICT(platform,user_id) DO UPDATE SET points=$v,user_name=CASE WHEN $n='' THEN points.user_name ELSE $n END,updated=$t
            """, ("$p", platform), ("$u", userId), ("$n", name), ("$v", balance), ("$t", Now()));
        write.Transaction = tx;
        write.ExecuteNonQuery();
        if (PointsContext?.Invoke() is { RecordHistory: true } context && balance != before)
        {
            using var journal = Command(conn, """
                INSERT INTO point_transactions(broadcaster,stream_id,platform,user_id,user_name,ts,reason,delta,balance)
                VALUES($b,$s,$p,$u,$n,$t,$r,$d,$v)
                """, ("$b", context.BroadcasterId), ("$s", context.StreamId), ("$p", platform), ("$u", userId),
                ("$n", name), ("$t", Now()), ("$r", reason), ("$d", checked(balance - before)), ("$v", balance));
            journal.Transaction = tx;
            try { journal.ExecuteNonQuery(); PointsHistoryError = null; }
            catch (SqliteException ex) { PointsHistoryError = ex.Message; }
        }
        return balance;
    }

    public long ChangePoints(string platform, string userId, string name, long value, bool set, string reason)
    {
        long result = 0;
        InTransaction((conn, tx) => result = ChangePoints(conn, tx, platform, userId, name, value, set, reason));
        return result;
    }

    public bool TransferPoints(string platform, string fromId, string fromName, string toId, string toName, long amount)
    {
        if (amount <= 0 || fromId == toId) return false;
        var transferred = false;
        InTransaction((conn, tx) =>
        {
            using var read = Command(conn, "SELECT points FROM points WHERE platform=$p AND user_id=$u", ("$p", platform), ("$u", fromId));
            read.Transaction = tx;
            if (Convert.ToInt64(read.ExecuteScalar() ?? 0L) < amount) return;
            ChangePoints(conn, tx, platform, fromId, fromName, -amount, false, "transfer");
            ChangePoints(conn, tx, platform, toId, toName, amount, false, "transfer");
            transferred = true;
        });
        return transferred;
    }

    private int ResetPointBalances(string platform)
    {
        var count = 0;
        InTransaction((conn, tx) =>
        {
            var rows = new List<(string Id, string Name)>();
            using (var query = Command(conn, "SELECT user_id,user_name FROM points WHERE platform=$p", ("$p", platform)))
            { query.Transaction = tx; using var reader = query.ExecuteReader(); while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1))); }
            foreach (var row in rows) ChangePoints(conn, tx, platform, row.Id, row.Name, 0, true, "reset");
            using var remove = Command(conn, "DELETE FROM points WHERE platform=$p", ("$p", platform)); remove.Transaction = tx; count = remove.ExecuteNonQuery();
        });
        return count;
    }

    public void RecordActivity(string broadcaster, string stream, string id, string login, string name, string role, bool command, string sourceId, string? message)
    {
        InTransaction((conn, tx) =>
        {
            using var history = Command(conn, """
                INSERT OR IGNORE INTO user_history(broadcaster,stream_id,user_id,user_name,ts,kind,detail,source_id)
                VALUES($b,$s,$u,$n,$t,$k,$d,$source)
                """, ("$b", broadcaster), ("$s", stream), ("$u", id), ("$n", name), ("$t", Now()),
                ("$k", message is null ? "activity" : "message"), ("$d", message ?? ""), ("$source", sourceId));
            history.Transaction = tx;
            if (history.ExecuteNonQuery() == 0) return;
            using var activity = Command(conn, """
                INSERT INTO user_activity(broadcaster,user_id,stream_id,login,user_name,role,last_seen,messages,commands)
                VALUES($b,$u,$s,$l,$n,$r,$t,1,$c)
                ON CONFLICT(broadcaster,user_id,stream_id) DO UPDATE SET login=$l,user_name=$n,role=$r,last_seen=$t,messages=messages+1,commands=commands+$c
                """, ("$b", broadcaster), ("$u", id), ("$s", stream), ("$l", login), ("$n", name), ("$r", role), ("$t", Now()), ("$c", command ? 1 : 0));
            activity.Transaction = tx;
            activity.ExecuteNonQuery();
        });
    }

    public List<TrackedUser> SearchTrackedUsers(string broadcaster, string? search, int limit, int offset) => Query("""
        SELECT user_id,login,user_name,role,MAX(last_seen),SUM(messages),SUM(commands),COUNT(DISTINCT CASE WHEN stream_id<>'' THEN stream_id END)
        FROM user_activity WHERE broadcaster=$b AND ($q='' OR user_name LIKE '%'||$q||'%' OR login LIKE '%'||$q||'%' OR user_id=$q)
        GROUP BY user_id ORDER BY MAX(last_seen) DESC,user_id LIMIT $l OFFSET $o
        """, r => new TrackedUser(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7)),
        ("$b", broadcaster), ("$q", search ?? ""), ("$l", limit), ("$o", offset));

    public List<HistoryRow> UserHistory(string broadcaster, string? userId, string? kind, string? stream, string? since, string? until, string? search, int limit, int offset) => Query("""
        SELECT id,ts,user_id,user_name,stream_id,kind,detail,amount FROM (
            SELECT id,ts,user_id,user_name,stream_id,kind,detail,amount FROM user_history WHERE broadcaster=$b
            UNION ALL
            SELECT -id,ts,user_id,user_name,stream_id,'points',reason,delta FROM point_transactions WHERE broadcaster=$b
        ) WHERE ($u IS NULL OR user_id=$u) AND ($k IS NULL OR kind=$k) AND ($s IS NULL OR stream_id=$s)
            AND ($t IS NULL OR ts >= $t) AND ($end IS NULL OR ts <= $end) AND ($q IS NULL OR detail LIKE '%'||$q||'%')
        ORDER BY ts DESC,id DESC LIMIT $l OFFSET $o
        """, r => new HistoryRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt64(7)),
        ("$b", broadcaster), ("$u", userId), ("$k", kind), ("$s", stream), ("$t", since), ("$end", until), ("$q", search), ("$l", limit), ("$o", offset));

    public List<PointsRow> EarnedLeaderboard(string broadcaster, string? stream, string since, int limit) => Query("""
        SELECT platform,user_id,user_name,SUM(delta),MAX(ts) FROM point_transactions
        WHERE broadcaster=$b AND ts >= $t AND ($s IS NULL OR stream_id=$s)
            AND reason IN ('chat','watch','first-message','follow','sub','resub','gift','cheer','raid','first') AND delta>0
        GROUP BY platform,user_id ORDER BY SUM(delta) DESC,user_name,user_id LIMIT $l
        """, r => new PointsRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4)),
        ("$b", broadcaster), ("$s", stream), ("$t", since), ("$l", limit));

    public int PurgeUserHistory(string broadcaster, string? userId, bool diagnostics)
    {
        var removed = 0;
        InTransaction((conn, tx) =>
        {
            foreach (var table in new[] { "user_history", "point_transactions", "user_activity" })
            {
                using var cmd = Command(conn, $"DELETE FROM {table} WHERE broadcaster=$b AND ($u IS NULL OR user_id=$u)", ("$b", broadcaster), ("$u", userId));
                cmd.Transaction = tx;
                removed += cmd.ExecuteNonQuery();
            }
            if (!diagnostics) return;
            // Structured diagnostics may contain identities in nested arguments; inspect JSON recursively rather than substring matching.
            foreach (var table in new[] { "events", "runs" })
            {
                using var cmd = Command(conn, $"DELETE FROM {table} WHERE $u IS NULL OR EXISTS (SELECT 1 FROM json_tree({table}.json) WHERE key IN ('userId','targetUserId','user_id') AND CAST(value AS TEXT)=$u)", ("$u", userId));
                cmd.Transaction = tx;
                removed += cmd.ExecuteNonQuery();
            }
            foreach (var table in new[] { "first_words", "mod_log" })
            {
                using var cmd = Command(conn, $"DELETE FROM {table} WHERE $u IS NULL OR user_id=$u", ("$u", userId));
                cmd.Transaction = tx;
                removed += cmd.ExecuteNonQuery();
            }
        });
        return removed;
    }

    public void PruneTracking(int detailDays, int chatDays)
    {
        var detail = DateTimeOffset.UtcNow.AddDays(-detailDays).ToString("O");
        Execute("DELETE FROM user_history WHERE ts<$t", ("$t", detail));
        Execute("UPDATE user_history SET detail='',kind='activity' WHERE kind='message' AND ts<$t", ("$t", DateTimeOffset.UtcNow.AddDays(-chatDays).ToString("O")));
        Execute("DELETE FROM point_transactions WHERE ts<$t", ("$t", detail));
        Execute("DELETE FROM user_activity WHERE last_seen<$t", ("$t", detail));
    }
}
