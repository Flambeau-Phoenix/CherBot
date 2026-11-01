namespace CherBot.Storage;

public sealed record DocRow(string Kind, string Id, string Name, string Json, string Updated);
public sealed record GlobalRow(string Name, string ValueJson, string LastWrite);
public sealed record UserGlobalRow(string Platform, string UserId, string UserLogin, string UserName, string Name, string ValueJson, string LastWrite);
public sealed record QuoteRow(long Id, string Timestamp, string UserId, string User, string Platform, string Game, string Quote);
public sealed record RunRow(string Id, string Started, string ActionId, string ActionName, string Trigger, string Status, long Ms, string Json);
public sealed record EventRow(long Seq, string Timestamp, string Source, string Type, string Json);
public sealed record PendingRow(string Id, string Queued, string Json);
public sealed record GroupUserRow(string Group, string Platform, string UserId, string Login, string UserName);
public sealed record PointsRow(string Platform, string UserId, string UserName, long Points, string Updated);
public sealed record FirstWordRow(string Platform, string UserId, string UserName, string Message, string Timestamp);

public sealed partial class CherBotDb
{
    // ── documents (actions, queues, commands, timers, connections) ──────────

    public List<DocRow> GetDocs(string kind) =>
        Query("SELECT kind, id, name, json, updated FROM docs WHERE kind = $k ORDER BY name",
            r => new DocRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$k", kind));

    public DocRow? GetDoc(string kind, string id) =>
        Query("SELECT kind, id, name, json, updated FROM docs WHERE kind = $k AND id = $id",
            r => new DocRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$k", kind), ("$id", id))
            .FirstOrDefault();

    public void UpsertDoc(string kind, string id, string name, string json) =>
        Execute("""
            INSERT INTO docs(kind, id, name, json, updated) VALUES($k, $id, $n, $j, $u)
            ON CONFLICT(kind, id) DO UPDATE SET name = excluded.name, json = excluded.json, updated = excluded.updated
            """, ("$k", kind), ("$id", id), ("$n", name), ("$j", json), ("$u", Now()));

    public bool DeleteDoc(string kind, string id) =>
        Execute("DELETE FROM docs WHERE kind = $k AND id = $id", ("$k", kind), ("$id", id)) > 0;

    // ── globals ────────────────────────────────────────────────────────────

    public List<GlobalRow> GetGlobals() =>
        Query("SELECT name, value_json, last_write FROM globals ORDER BY name",
            r => new GlobalRow(r.GetString(0), r.GetString(1), r.GetString(2)));

    public void SetGlobal(string name, string valueJson) =>
        Execute("""
            INSERT INTO globals(name, value_json, last_write) VALUES($n, $v, $t)
            ON CONFLICT(name) DO UPDATE SET value_json = excluded.value_json, last_write = excluded.last_write
            """, ("$n", name), ("$v", valueJson), ("$t", Now()));

    public bool DeleteGlobal(string name) => Execute("DELETE FROM globals WHERE name = $n", ("$n", name)) > 0;

    public List<UserGlobalRow> GetUserGlobals() =>
        Query("SELECT platform, user_id, user_login, user_name, name, value_json, last_write FROM user_globals",
            r => new UserGlobalRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));

    public void SetUserGlobal(string platform, string userId, string userLogin, string userName, string name, string valueJson) =>
        Execute("""
            INSERT INTO user_globals(platform, user_id, name, user_login, user_name, value_json, last_write)
            VALUES($p, $u, $n, $l, $un, $v, $t)
            ON CONFLICT(platform, user_id, name) DO UPDATE SET
              user_login = CASE WHEN excluded.user_login = '' THEN user_globals.user_login ELSE excluded.user_login END,
              user_name = CASE WHEN excluded.user_name = '' THEN user_globals.user_name ELSE excluded.user_name END,
              value_json = excluded.value_json, last_write = excluded.last_write
            """, ("$p", platform), ("$u", userId), ("$n", name), ("$l", userLogin), ("$un", userName), ("$v", valueJson), ("$t", Now()));

    public int DeleteUserGlobal(string platform, string? userId, string? name) =>
        Execute("""
            DELETE FROM user_globals WHERE platform = $p
              AND ($u IS NULL OR user_id = $u) AND ($n IS NULL OR name = $n)
            """, ("$p", platform), ("$u", userId), ("$n", name));

    // ── quotes ─────────────────────────────────────────────────────────────

    private static QuoteRow MapQuote(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));

    public long AddQuote(string userId, string user, string platform, string game, string quote)
    {
        Execute("INSERT INTO quotes(ts, user_id, user, platform, game, quote) VALUES($t, $ui, $u, $p, $g, $q)",
            ("$t", Now()), ("$ui", userId), ("$u", user), ("$p", platform), ("$g", game), ("$q", quote));
        return Scalar<long>("SELECT MAX(id) FROM quotes");
    }

    public QuoteRow? GetQuote(long id) =>
        Query("SELECT id, ts, user_id, user, platform, game, quote FROM quotes WHERE id = $id", MapQuote, ("$id", id)).FirstOrDefault();

    public QuoteRow? GetRandomQuote() =>
        Query("SELECT id, ts, user_id, user, platform, game, quote FROM quotes ORDER BY RANDOM() LIMIT 1", MapQuote).FirstOrDefault();

    public List<QuoteRow> GetQuotes() =>
        Query("SELECT id, ts, user_id, user, platform, game, quote FROM quotes ORDER BY id", MapQuote);

    public long QuoteCount() => Scalar<long>("SELECT COUNT(*) FROM quotes");

    public bool DeleteQuote(long id) => Execute("DELETE FROM quotes WHERE id = $id", ("$id", id)) > 0;

    // ── counters ───────────────────────────────────────────────────────────

    public long IncrementCounter(string scope, string key, string userId = "")
    {
        Execute("""
            INSERT INTO counters(scope, key, user_id, value, last_used) VALUES($s, $k, $u, 1, $t)
            ON CONFLICT(scope, key, user_id) DO UPDATE SET value = value + 1, last_used = excluded.last_used
            """, ("$s", scope), ("$k", key), ("$u", userId), ("$t", Now()));
        return Scalar<long>("SELECT value FROM counters WHERE scope = $s AND key = $k AND user_id = $u",
            ("$s", scope), ("$k", key), ("$u", userId));
    }

    public long GetCounter(string scope, string key, string userId = "") =>
        Scalar<long?>("SELECT value FROM counters WHERE scope = $s AND key = $k AND user_id = $u",
            ("$s", scope), ("$k", key), ("$u", userId)) ?? 0;

    public int ResetCounters(string scope, string key, bool usersOnly) =>
        Execute(usersOnly
                ? "DELETE FROM counters WHERE scope = $s AND key = $k AND user_id <> ''"
                : "DELETE FROM counters WHERE scope = $s AND key = $k AND user_id = ''",
            ("$s", scope), ("$k", key));

    // ── runs ───────────────────────────────────────────────────────────────

    public void AddRun(RunRow run) =>
        Execute("INSERT OR REPLACE INTO runs(id, started, action_id, action_name, trigger, status, ms, json) VALUES($i, $s, $a, $n, $tr, $st, $ms, $j)",
            ("$i", run.Id), ("$s", run.Started), ("$a", run.ActionId), ("$n", run.ActionName), ("$tr", run.Trigger),
            ("$st", run.Status), ("$ms", run.Ms), ("$j", run.Json));

    public List<RunRow> GetRuns(int limit, string? actionId = null) =>
        Query("""
            SELECT id, started, action_id, action_name, trigger, status, ms, json FROM runs
            WHERE ($a IS NULL OR action_id = $a) ORDER BY started DESC LIMIT $l
            """,
            r => new RunRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetString(7)),
            ("$a", actionId), ("$l", limit));

    public int PruneRuns(TimeSpan keep) =>
        Execute("DELETE FROM runs WHERE started < $c", ("$c", DateTimeOffset.UtcNow.Subtract(keep).ToString("O")));

    // ── events ─────────────────────────────────────────────────────────────

    public long AddEvent(string ts, string source, string type, string json)
    {
        using var conn = Open();
        using var cmd = Command(conn, "INSERT INTO events(ts, source, type, json) VALUES($t, $s, $ty, $j); SELECT last_insert_rowid();",
            ("$t", ts), ("$s", source), ("$ty", type), ("$j", json));
        return (long)cmd.ExecuteScalar()!;
    }

    public List<EventRow> GetEventsSince(long seq, int limit) =>
        Query("SELECT seq, ts, source, type, json FROM events WHERE seq > $s ORDER BY seq LIMIT $l",
            r => new EventRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$s", seq), ("$l", limit));

    public EventRow? GetLatestEvent(string source, string type) =>
        Query("SELECT seq, ts, source, type, json FROM events WHERE source = $s AND type = $t ORDER BY seq DESC LIMIT 1",
            r => new EventRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$s", source), ("$t", type)).FirstOrDefault();

    public long LastEventSeq() => Scalar<long?>("SELECT MAX(seq) FROM events") ?? 0;

    public int PruneEvents(TimeSpan keep) =>
        Execute("DELETE FROM events WHERE ts < $c", ("$c", DateTimeOffset.UtcNow.Subtract(keep).ToString("O")));

    // ── pending queue cache ────────────────────────────────────────────────

    public void ReplacePending(IEnumerable<PendingRow> rows) =>
        InTransaction((conn, tx) =>
        {
            using (var clear = Command(conn, "DELETE FROM pending")) { clear.Transaction = tx; clear.ExecuteNonQuery(); }
            foreach (var row in rows)
            {
                using var ins = Command(conn, "INSERT INTO pending(id, queued, json) VALUES($i, $q, $j)", ("$i", row.Id), ("$q", row.Queued), ("$j", row.Json));
                ins.Transaction = tx;
                ins.ExecuteNonQuery();
            }
        });

    public List<PendingRow> GetPending() =>
        Query("SELECT id, queued, json FROM pending ORDER BY queued", r => new PendingRow(r.GetString(0), r.GetString(1), r.GetString(2)));

    // ── secrets and key/value settings ─────────────────────────────────────

    public string? GetSecret(string name) => Scalar<string>("SELECT blob FROM secrets WHERE name = $n", ("$n", name));

    public void SetSecret(string name, string blob) =>
        Execute("INSERT INTO secrets(name, blob, updated) VALUES($n, $b, $t) ON CONFLICT(name) DO UPDATE SET blob = excluded.blob, updated = excluded.updated",
            ("$n", name), ("$b", blob), ("$t", Now()));

    public bool DeleteSecret(string name) => Execute("DELETE FROM secrets WHERE name = $n", ("$n", name)) > 0;

    public string? GetKv(string key) => Scalar<string>("SELECT json FROM kv WHERE key = $k", ("$k", key));

    public void SetKv(string key, string json) =>
        Execute("INSERT INTO kv(key, json, updated) VALUES($k, $j, $t) ON CONFLICT(key) DO UPDATE SET json = excluded.json, updated = excluded.updated",
            ("$k", key), ("$j", json), ("$t", Now()));

    // ── user groups ────────────────────────────────────────────────────────

    public List<GroupUserRow> GetGroupUsers(string? group = null) =>
        Query("SELECT group_name, platform, user_id, login, username FROM user_groups WHERE ($g IS NULL OR group_name = $g COLLATE NOCASE) ORDER BY group_name, login",
            r => new GroupUserRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$g", group));

    public bool AddGroupUser(string group, string platform, string userId, string login, string userName) =>
        Execute("INSERT OR IGNORE INTO user_groups(group_name, platform, user_id, login, username) VALUES($g, $p, $u, $l, $n)",
            ("$g", group), ("$p", platform), ("$u", userId), ("$l", login), ("$n", userName)) > 0;

    public bool RemoveGroupUser(string group, string platform, string userId) =>
        Execute("DELETE FROM user_groups WHERE group_name = $g COLLATE NOCASE AND platform = $p AND user_id = $u",
            ("$g", group), ("$p", platform), ("$u", userId)) > 0;

    public int ClearGroup(string group) => Execute("DELETE FROM user_groups WHERE group_name = $g COLLATE NOCASE", ("$g", group));

    // ── points / leaderboard ───────────────────────────────────────────────

    public long AddPoints(string platform, string userId, string userName, long delta, string reason = "script") =>
        ChangePoints(platform, userId, userName, delta, false, reason);

    public long SetPoints(string platform, string userId, string userName, long value, string reason = "admin") =>
        ChangePoints(platform, userId, userName, value, true, reason);

    public long GetPoints(string platform, string userId) =>
        Scalar<long?>("SELECT points FROM points WHERE platform = $p AND user_id = $u", ("$p", platform), ("$u", userId)) ?? 0;

    public int GetRank(string platform, string userId) =>
        (int)(Scalar<long?>("""
            SELECT 1 + (SELECT COUNT(*) FROM points p2 WHERE p2.platform = p.platform AND p2.points > p.points)
            FROM points p WHERE p.platform = $p AND p.user_id = $u
            """, ("$p", platform), ("$u", userId)) ?? 0);

    public List<PointsRow> GetLeaderboard(string platform, int limit) =>
        Query("SELECT platform, user_id, user_name, points, updated FROM points WHERE platform = $p ORDER BY points DESC, user_name LIMIT $l",
            r => new PointsRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4)), ("$p", platform), ("$l", limit));

    public int ResetPoints(string platform) => ResetPointBalances(platform);

    public List<PointsRow> SearchPoints(string platform, string? search, int limit) =>
        Query("""
            SELECT platform, user_id, user_name, points, updated FROM points
            WHERE platform = $p AND ($q IS NULL OR user_name LIKE '%' || $q || '%' OR user_id = $q)
            ORDER BY points DESC, user_name LIMIT $l
            """,
            r => new PointsRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4)),
            ("$p", platform), ("$q", string.IsNullOrWhiteSpace(search) ? null : search.Trim()), ("$l", limit));

    public PointsRow? FindPointsByName(string platform, string userName) =>
        Query("SELECT platform, user_id, user_name, points, updated FROM points WHERE platform = $p AND user_name = $n COLLATE NOCASE LIMIT 1",
            r => new PointsRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetString(4)), ("$p", platform), ("$n", userName)).FirstOrDefault();

    // ── first words ────────────────────────────────────────────────────────

    /// <summary>Records the user's first message of the current stream. Returns false when already recorded.</summary>
    public bool TryAddFirstWord(string platform, string userId, string userName, string message) =>
        Execute("INSERT OR IGNORE INTO first_words(platform, user_id, user_name, message, ts) VALUES($p, $u, $n, $m, $t)",
            ("$p", platform), ("$u", userId), ("$n", userName), ("$m", message), ("$t", Now())) > 0;

    public List<FirstWordRow> GetFirstWords(int limit) =>
        Query("SELECT platform, user_id, user_name, message, ts FROM first_words ORDER BY ts LIMIT $l",
            r => new FirstWordRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$l", limit));

    public int ResetFirstWords() => Execute("DELETE FROM first_words");
}
