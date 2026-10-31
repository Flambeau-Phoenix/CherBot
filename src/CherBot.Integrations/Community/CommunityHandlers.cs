using System.Globalization;
using CherBot.Core;
using CherBot.Core.Engine;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Core.State;
using CherBot.Integrations.Twitch;
using CherBot.Storage;

namespace CherBot.Integrations.Community;

/// <summary>
/// CherBot's own sub-actions for the community features carried over from the Python prototype. Each computes a chat reply
/// into the <c>reply</c> argument (send it with a Send Message sub-action) and exposes its data as arguments.
/// Subcommands come from the command's <c>input0</c>, <c>input1</c>, ... arguments.
/// </summary>
public sealed class CommunityHandlers : SubActionHandlerGroup
{
    public const int Points = 50001, Leaderboard = 50002, FirstWords = 50003, DiscordHub = 50004, AwardPoints = 50005;

    static CommunityHandlers()
    {
        SubActionTypes.RegisterCustom(new(Points, "CherBotPoints", "Points Command", "CherBot / Community", ["platform:string"]));
        SubActionTypes.RegisterCustom(new(Leaderboard, "CherBotLeaderboard", "Points Leaderboard", "CherBot / Community", ["count:int", "platform:string"]));
        SubActionTypes.RegisterCustom(new(FirstWords, "CherBotFirstWords", "First Words Command", "CherBot / Community", []));
        SubActionTypes.RegisterCustom(new(DiscordHub, "CherBotDiscordHub", "Discord Hub Command", "CherBot / Community", ["defaultUrl:string"]));
        SubActionTypes.RegisterCustom(new(AwardPoints, "CherBotAwardPoints", "Award Points", "CherBot / Community", ["amount:string", "target:int", "platform:string"]));
    }

    public CommunityHandlers()
    {
        On(Points, (s, c) => PointsCommand(s, c));
        On(Leaderboard, (s, c) =>
        {
            var count = Math.Clamp(s.Int("count", 5), 1, 25);
            var (text, rows) = LeaderboardText(c, Platform(s), count);
            c.SetArg("leaderboard", rows);
            c.SetArg("leaderboardText", text);
            c.SetArg("reply", $"🏆 Leaderboard: {text}");
        });
        On(FirstWords, (s, c) => FirstWordsCommand(c));
        On(DiscordHub, (s, c) => DiscordCommand(s, c));
        On(AwardPoints, async (s, c) =>
        {
            var amount = long.TryParse(c.Parse(s.Str("amount")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0;
            var (id, name) = s.Int("target") == 1
                ? (c.ArgString("targetUserId"), c.ArgString("targetUser"))
                : (c.ArgString("userId"), c.ArgString("user", c.ArgString("userName")));
            if (id.Length == 0) return;
            var total = c.Service<CherBotDb>().AddPoints(Platform(s), id, name, amount);
            c.SetArg("points", total);
            await Task.CompletedTask;
        });
    }

    internal static string Currency(RunContext c) => c.OptionalService<Loyalty.LoyaltyService>()?.Currency ?? "points";

    private static string Platform(SubActionDef s) => s.Str("platform", "twitch") is { Length: > 0 } p ? p : "twitch";

    private static bool IsMod(RunContext c) => Flag(c, "isModerator") || Flag(c, "isBroadcaster");
    private static bool Flag(RunContext c, string name) => c.GetArg(name) is true || c.ArgString(name).Equals("true", StringComparison.OrdinalIgnoreCase);
    private static string Input(RunContext c, int i) => c.ArgString($"input{i}");
    private static string Who(RunContext c) => c.ArgString("user", c.ArgString("userName", "someone"));

    private static (string Text, List<Dictionary<string, object?>> Rows) LeaderboardText(RunContext c, string platform, int count)
    {
        var rows = c.Service<CherBotDb>().GetLeaderboard(platform, count)
            .Select((r, i) => new Dictionary<string, object?> { ["rank"] = i + 1, ["userId"] = r.UserId, ["userName"] = r.UserName, ["points"] = r.Points })
            .ToList();
        var cur = Currency(c);
        var text = rows.Count == 0 ? "No entries yet" : string.Join(" | ", rows.Select(r => $"#{r["rank"]} {r["userName"]} ({r["points"]} {cur})"));
        return (text, rows);
    }

    /// <summary>Finds a user id for a login: points table first, then Twitch, otherwise a login-keyed placeholder.</summary>
    private static async Task<(string Id, string Name)> ResolveTarget(RunContext c, string platform, string login)
    {
        login = login.TrimStart('@').Trim();
        if (c.Service<CherBotDb>().FindPointsByName(platform, login) is { } row) return (row.UserId, row.UserName);
        if (platform == "twitch" && c.OptionalService<TwitchAuth>()?.Get(TwitchRole.Broadcaster) is not null)
        {
            try
            {
                if (await c.Service<HelixClient>().GetUserByLoginAsync(login, c.Cancellation) is { } u)
                    return (u.GetProperty("id").GetString()!, u.GetProperty("display_name").GetString() ?? login);
            }
            catch (HelixException) { /* fall through */ }
        }
        return ("login:" + login.ToLowerInvariant(), login);
    }

    private static async Task PointsCommand(SubActionDef s, RunContext c)
    {
        var db = c.Service<CherBotDb>();
        var platform = Platform(s);
        var sub = Input(c, 0).ToLowerInvariant();
        var userId = c.ArgString("userId");
        var user = Who(c);

        if (sub is "add" or "set" && Input(c, 1).Length > 0 && Input(c, 2).Length > 0)
        {
            if (!IsMod(c))
            {
                c.SetArg("reply", $"{user}, only moderators can {sub} {Currency(c)}.");
                c.SetArg("denied", true);
                return;
            }
            var (id, name) = await ResolveTarget(c, platform, Input(c, 1));
            var amount = long.TryParse(Input(c, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var total = sub == "add" ? db.AddPoints(platform, id, name, amount) : db.SetPoints(platform, id, name, amount);
            c.SetArg("targetUser", name);
            c.SetArg("targetPoints", total);
            c.SetArg("amount", amount);
            c.SetArg("reply", sub == "add" ? $"Added {amount} {Currency(c)} to {name} (Total: {total})." : $"Set {name} to {total} {Currency(c)}.");
            return;
        }

        if (sub == "give" && Input(c, 1).Length > 0 && Input(c, 2).Length > 0)
        {
            var amount = Math.Max(0, long.TryParse(Input(c, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0);
            var have = db.GetPoints(platform, userId);
            if (amount <= 0 || have < amount)
            {
                c.SetArg("reply", $"{user}, you only have {have} {Currency(c)}.");
                return;
            }
            var (id, name) = await ResolveTarget(c, platform, Input(c, 1));
            var mine = db.AddPoints(platform, userId, user, -amount);
            var theirs = db.AddPoints(platform, id, name, amount);
            c.SetArg("targetUser", name);
            c.SetArg("targetPoints", theirs);
            c.SetArg("amount", amount);
            c.SetArg("reply", $"{user} gave {amount} {Currency(c)} to {name}! ({user}: {mine}, {name}: {theirs})");
            return;
        }

        if (sub is "top" or "lb" or "leaderboard")
        {
            var (text, rows) = LeaderboardText(c, platform, 5);
            c.SetArg("leaderboard", rows);
            c.SetArg("reply", $"🏆 Leaderboard: {text}");
            return;
        }

        var points = db.GetPoints(platform, userId);
        var rank = db.GetRank(platform, userId);
        c.SetArg("points", points);
        c.SetArg("rank", rank);
        c.SetArg("reply", rank > 0 ? $"{user} has {points} {Currency(c)} (Rank #{rank})." : $"{user} has {points} {Currency(c)}.");
    }

    private static void FirstWordsCommand(RunContext c)
    {
        var db = c.Service<CherBotDb>();
        var sub = Input(c, 0).ToLowerInvariant();
        var user = Who(c);

        if (sub == "reset")
        {
            if (!IsMod(c))
            {
                c.SetArg("reply", $"{user}, only moderators can reset first words.");
                c.SetArg("denied", true);
                return;
            }
            var cleared = db.ResetFirstWords();
            c.SetArg("clearedCount", cleared);
            c.SetArg("reply", $"First words reset by {user} ({cleared} entries cleared).");
            return;
        }

        var all = db.GetFirstWords(500);
        if (sub is "top" or "list")
        {
            var top = all.Take(5).ToList();
            c.SetArg("reply", top.Count == 0
                ? "No first words recorded yet!"
                : "First chatters: " + string.Join(" | ", top.Select((r, i) => $"#{i + 1} {r.UserName} (\"{r.Message}\")")));
            return;
        }

        var userId = c.ArgString("userId");
        var index = all.FindIndex(r => r.UserId == userId);
        var leader = all.FirstOrDefault();
        var points = db.GetPoints("twitch", userId);
        if (index < 0)
        {
            c.SetArg("reply", leader is null ? "No first words recorded yet!" : $"Stream #1 first chatter: {leader.UserName}!");
            return;
        }
        var mine = all[index];
        c.SetArg("firstRank", index + 1);
        c.SetArg("firstWord", mine.Message);
        c.SetArg("firstTime", mine.Timestamp);
        c.SetArg("streamFirstUser", leader!.UserName);
        c.SetArg("reply", index == 0
            ? $"🥇 {user} is #1 first chatter! First words: \"{mine.Message}\" ({points} {Currency(c)})"
            : $"{user} was chatter #{index + 1} (first words: \"{mine.Message}\", {points} {Currency(c)}). Stream #1: {leader.UserName}!");
    }

    private static void DiscordCommand(SubActionDef s, RunContext c)
    {
        var vars = c.Service<VariableService>();
        var url = CherBotJson.Format(vars.GetGlobal("discord_url")) is { Length: > 0 } g ? g : s.Str("defaultUrl", "https://discord.gg/cherbot");
        var sub = Input(c, 0).ToLowerInvariant() is { Length: > 0 } x ? x : "link";
        var rest = string.Join(' ', Enumerable.Range(1, 30).Select(i => Input(c, i)).Where(w => w.Length > 0));
        var user = Who(c);
        var isVipPlus = Flag(c, "isVip") || IsMod(c);

        string reply = sub switch
        {
            "link" or "invite" => $"Join the Discord hub: {url} (subcommands: !disc rules | schedule | lfg | vip)",
            "rules" => "Discord Hub Rules: 1) Be respectful 2) No spam/self-promo 3) Keep spoilers in #spoilers.",
            "schedule" or "events" => $"Community events are announced in {url}",
            "lfg" => $"🎮 {user} is LFG for {(rest.Length > 0 ? rest : "games")}! Hop into #lfg-lobby in {url}",
            "vip" when isVipPlus => $"✨ VIP Lounge unlocked for {user} — check #vip-lounge in {url}",
            "vip" => $"{user}, the VIP lounge is for VIPs and moderators.",
            "set" when IsMod(c) && rest.Length > 0 => SetUrl(rest.Split(' ')[0]),
            "set" => $"{user}, only moderators can change the Discord link.",
            "announce" when IsMod(c) => $"📣 [Discord Hub Announcement by {user}] {(rest.Length > 0 ? rest : "Community check-in starting now!")} ({url})",
            "announce" => $"{user}, only moderators can post hub announcements.",
            _ => $"Discord Hub ({url}) — unknown subcommand '{sub}'. Try: !disc link | rules | schedule | lfg",
        };

        string SetUrl(string newUrl)
        {
            vars.SetGlobal("discord_url", newUrl, persisted: true);
            url = newUrl;
            return $"Discord hub URL updated to {newUrl} by {user}.";
        }

        c.SetArg("discordUrl", url);
        c.SetArg("hubSubcommand", sub);
        c.SetArg("reply", reply);
    }
}
