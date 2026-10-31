using Microsoft.Data.Sqlite;

namespace CherBot.Storage;

public sealed record GiveawayEntryRow(string Giveaway, string Platform, string UserId, string UserLogin, string UserName, int Tickets, int Bonus, long Paid, string Timestamp)
{
    public int TotalTickets => Tickets + Bonus;
}

public sealed record GiveawayWinnerRow(long Id, string Giveaway, int Run, string Platform, string UserId, string UserLogin, string UserName, string DrawnAt, string Status, string? ClaimedAt);

public sealed record ModLogRow(long Id, string Timestamp, string Platform, string UserId, string UserName, string Filter, string Excerpt, string Action, bool Success, string Detail);

public sealed partial class CherBotDb
{
    // ── giveaway entries ───────────────────────────────────────────────────

    private const string EntryColumns = "giveaway, platform, user_id, user_login, user_name, tickets, bonus, paid, ts";

    private static GiveawayEntryRow MapEntry(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt64(7), r.GetString(8));

    public List<GiveawayEntryRow> GetGiveawayEntries(string giveaway) =>
        Query($"SELECT {EntryColumns} FROM giveaway_entries WHERE giveaway = $g ORDER BY ts", MapEntry, ("$g", giveaway));

    public GiveawayEntryRow? GetGiveawayEntry(string giveaway, string platform, string userId) =>
        Query($"SELECT {EntryColumns} FROM giveaway_entries WHERE giveaway = $g AND platform = $p AND user_id = $u", MapEntry,
            ("$g", giveaway), ("$p", platform), ("$u", userId)).FirstOrDefault();

    public void UpsertGiveawayEntry(GiveawayEntryRow e) =>
        Execute("""
            INSERT INTO giveaway_entries(giveaway, platform, user_id, user_login, user_name, tickets, bonus, paid, ts)
            VALUES($g, $p, $u, $l, $n, $t, $b, $paid, $ts)
            ON CONFLICT(giveaway, platform, user_id) DO UPDATE SET user_login = $l, user_name = $n, tickets = $t, bonus = $b, paid = $paid
            """, ("$g", e.Giveaway), ("$p", e.Platform), ("$u", e.UserId), ("$l", e.UserLogin), ("$n", e.UserName),
            ("$t", e.Tickets), ("$b", e.Bonus), ("$paid", e.Paid), ("$ts", e.Timestamp.Length > 0 ? e.Timestamp : Now()));

    public bool DeleteGiveawayEntry(string giveaway, string platform, string userId) =>
        Execute("DELETE FROM giveaway_entries WHERE giveaway = $g AND platform = $p AND user_id = $u", ("$g", giveaway), ("$p", platform), ("$u", userId)) > 0;

    public int ClearGiveawayEntries(string giveaway) => Execute("DELETE FROM giveaway_entries WHERE giveaway = $g", ("$g", giveaway));

    // ── giveaway winners ───────────────────────────────────────────────────

    private const string WinnerColumns = "id, giveaway, run, platform, user_id, user_login, user_name, drawn_at, status, claimed_at";

    private static GiveawayWinnerRow MapWinner(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9));

    public long AddGiveawayWinner(string giveaway, int run, string platform, string userId, string userLogin, string userName, string status) =>
        Scalar<long>("""
            INSERT INTO giveaway_winners(giveaway, run, platform, user_id, user_login, user_name, drawn_at, status)
            VALUES($g, $r, $p, $u, $l, $n, $t, $s) RETURNING id
            """, ("$g", giveaway), ("$r", run), ("$p", platform), ("$u", userId), ("$l", userLogin), ("$n", userName), ("$t", Now()), ("$s", status));

    public List<GiveawayWinnerRow> GetGiveawayWinners(string giveaway) =>
        Query($"SELECT {WinnerColumns} FROM giveaway_winners WHERE giveaway = $g ORDER BY id", MapWinner, ("$g", giveaway));

    public GiveawayWinnerRow? GetGiveawayWinner(long id) =>
        Query($"SELECT {WinnerColumns} FROM giveaway_winners WHERE id = $id", MapWinner, ("$id", id)).FirstOrDefault();

    public bool SetGiveawayWinnerStatus(long id, string status) =>
        Execute("UPDATE giveaway_winners SET status = $s, claimed_at = CASE WHEN $s = 'claimed' THEN $t ELSE claimed_at END WHERE id = $id",
            ("$s", status), ("$t", Now()), ("$id", id)) > 0;

    public int ClearGiveawayWinners(string giveaway) => Execute("DELETE FROM giveaway_winners WHERE giveaway = $g", ("$g", giveaway));

    // ── moderation log ─────────────────────────────────────────────────────

    public long AddModLog(string platform, string userId, string userName, string filter, string excerpt, string action, bool success, string detail = "") =>
        Scalar<long>("""
            INSERT INTO mod_log(ts, platform, user_id, user_name, filter, excerpt, action, success, detail)
            VALUES($t, $p, $u, $n, $f, $e, $a, $s, $d) RETURNING id
            """, ("$t", Now()), ("$p", platform), ("$u", userId), ("$n", userName), ("$f", filter),
            ("$e", excerpt.Length > 200 ? excerpt[..200] : excerpt), ("$a", action), ("$s", success ? 1 : 0), ("$d", detail));

    public List<ModLogRow> GetModLog(int limit) =>
        Query("SELECT id, ts, platform, user_id, user_name, filter, excerpt, action, success, detail FROM mod_log ORDER BY id DESC LIMIT $l",
            r => new ModLogRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.GetString(7), r.GetInt32(8) != 0, r.GetString(9)), ("$l", limit));

    public int PruneModLog(TimeSpan keep) =>
        Execute("DELETE FROM mod_log WHERE ts < $t", ("$t", DateTimeOffset.UtcNow.Subtract(keep).ToString("O")));
}
