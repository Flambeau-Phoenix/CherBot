using System.Globalization;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Engine;
using CherBot.Core.Model;
using CherBot.Storage;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Twitch;

/// <summary>Twitch sub-actions (Streamer.bot type ids): chat, channel, moderation, rewards, raids, followers and user info.</summary>
public sealed class TwitchHandlers : SubActionHandlerGroup
{
    public TwitchHandlers()
    {
        // ── chat ─────────────────────────────────────────────────────────────
        On(10, async (s, c) =>
        {
            var tw = c.Service<TwitchService>();
            var text = c.Parse(s.Str("text"));
            if (string.IsNullOrWhiteSpace(text)) { c.SetArg("messageSent", false); return; }
            var res = await tw.SendChatAsync(text, s.Bool("useBot", true), s.Bool("fallback", true), null, c.Cancellation);
            c.SetArg("messageSent", res.Sent);
            if (!res.Sent) c.Log.LogWarning("Twitch message not sent: {Error}", res.Error);
        });
        On(23, async (s, c) =>
        {
            var color = s.Int("color") switch { 1 => "blue", 2 => "green", 3 => "orange", 4 => "purple", _ => "primary" };
            var role = s.Bool("useBot", true) && c.Service<TwitchAuth>().Get(TwitchRole.Bot) is not null ? TwitchRole.Bot : TwitchRole.Broadcaster;
            var moderator = c.Service<TwitchAuth>().Get(role)!.UserId;
            await Helix(c).SendAsync(HttpMethod.Post, "chat/announcements",
                new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["moderator_id"] = moderator },
                new { message = c.Parse(s.Str("text")), color }, role, c.Cancellation);
        });
        On(29, async (s, c) =>
        {
            if (await ResolveUser(c, s.Str("userLogin")) is not { } target) return;
            await Helix(c).SendAsync(HttpMethod.Post, "chat/shoutouts", new Dictionary<string, string?>
            {
                ["from_broadcaster_id"] = BroadcasterId(c), ["to_broadcaster_id"] = Id(target), ["moderator_id"] = BroadcasterId(c),
            }, ct: c.Cancellation);
        });

        // ── chat modes ───────────────────────────────────────────────────────
        On(12, (s, c) => ChatSettings(c, new() { ["subscriber_mode"] = s.Int("status") == 0 }));
        On(13, (s, c) => ChatSettings(c, new() { ["emote_mode"] = s.Int("status") == 0 }));
        On(14, (s, c) => ChatSettings(c, s.Int("status") == 0
            ? new() { ["slow_mode"] = true, ["slow_mode_wait_time"] = Math.Clamp(ParseInt(c.Parse(s.Str("duration")), 30), 3, 120) }
            : new() { ["slow_mode"] = false }));
        On(28, (s, c) => ChatSettings(c, s.Int("status") == 0
            ? new() { ["follower_mode"] = true, ["follower_mode_duration"] = ParseInt(c.Parse(s.Str("duration")), 0) }
            : new() { ["follower_mode"] = false }));

        // ── channel ──────────────────────────────────────────────────────────
        On(15, async (s, c) => await Channel(c, new { title = c.Parse(s.Str("title")) }));
        On(16, async (s, c) =>
        {
            var gameId = c.Parse(s.Str("gameId"));
            if (string.IsNullOrWhiteSpace(gameId))
            {
                var name = c.Parse(s.Str("game"));
                var found = HelixClient.First(await Helix(c).GetAsync("games", new Dictionary<string, string?> { ["name"] = name }, ct: c.Cancellation))
                            ?? HelixClient.First(await Helix(c).GetAsync("search/categories", new Dictionary<string, string?> { ["query"] = name, ["first"] = "1" }, ct: c.Cancellation));
                if (found is null)
                {
                    c.Log.LogWarning("Set Channel Game: no category matches '{Game}'", name);
                    return;
                }
                gameId = found.Value.GetProperty("id").GetString()!;
            }
            await Channel(c, new { game_id = gameId });
        });
        On(17, async (s, c) =>
        {
            var res = await Helix(c).SendAsync(HttpMethod.Post, "streams/markers", body: new { user_id = BroadcasterId(c), description = c.Parse(s.Str("description")) }, ct: c.Cancellation);
            if (HelixClient.First(res) is { } m) c.SetArg("createMarkerPosition", m.GetProperty("position_seconds").GetInt32());
        });
        On(18, async (s, c) =>
        {
            var res = await Helix(c).SendAsync(HttpMethod.Post, "clips", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c) }, ct: c.Cancellation);
            var clip = HelixClient.First(res);
            c.SetArg("createClipSuccess", clip is not null);
            if (clip is { } cl)
            {
                var id = cl.GetProperty("id").GetString();
                c.SetArg("createClipId", id);
                c.SetArg("createClipCreatedUrl", $"https://clips.twitch.tv/{id}");
                c.SetArg("createClipEditUrl", cl.GetProperty("edit_url").GetString());
            }
        });
        On(19, async (s, c) => await Helix(c).SendAsync(HttpMethod.Post, "channels/commercial",
            body: new { broadcaster_id = BroadcasterId(c), length = Math.Max(30, s.Int("duration", 30)) }, ct: c.Cancellation));
        On(251, async (s, c) => await Channel(c, new { tags = Array.Empty<string>() }));
        On(252, async (s, c) => await Channel(c, new { tags = s.StrList("tags").Select(t => c.Parse(t)).Take(10).ToArray() }));
        On(253, async (s, c) =>
        {
            var tags = await CurrentTags(c);
            var tag = c.Parse(s.Str("tag"));
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag);
            await Channel(c, new { tags = tags.Take(10).ToArray() });
        });
        On(254, async (s, c) =>
        {
            var tag = c.Parse(s.Str("tag"));
            var tags = (await CurrentTags(c)).Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
            await Channel(c, new { tags });
        });

        // ── moderation ───────────────────────────────────────────────────────
        On(20, async (s, c) =>
        {
            if (await ResolveUser(c, s.Str("userLogin")) is not { } target) return;
            var duration = Math.Clamp(ParseInt(c.Parse(s.Str("duration")), 600), 1, 1_209_600);
            await Helix(c).SendAsync(HttpMethod.Post, "moderation/bans",
                new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["moderator_id"] = BroadcasterId(c) },
                new { data = new { user_id = Id(target), duration, reason = c.Parse(s.Str("reason")) } }, ct: c.Cancellation);
        });
        On(500, async (s, c) =>
        {
            if (await ResolveUser(c, s.Str("userLogin")) is not { } target) return;
            await Helix(c).SendAsync(HttpMethod.Post, "raids", new Dictionary<string, string?>
            {
                ["from_broadcaster_id"] = BroadcasterId(c), ["to_broadcaster_id"] = Id(target),
            }, ct: c.Cancellation);
        });
        On(501, async (_, c) => await Helix(c).SendAsync(HttpMethod.Delete, "raids", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c) }, ct: c.Cancellation));
        On(502, (s, c) => UserRole(c, s, HttpMethod.Post, "channels/vips"));
        On(503, (s, c) => UserRole(c, s, HttpMethod.Delete, "channels/vips"));
        On(504, (s, c) => UserRole(c, s, HttpMethod.Post, "moderation/moderators"));
        On(505, (s, c) => UserRole(c, s, HttpMethod.Delete, "moderation/moderators"));

        // ── followers, subscribers, user info ───────────────────────────────
        On(506, async (_, c) =>
        {
            var res = await Helix(c).GetAsync("channels/followers", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["first"] = "1" }, ct: c.Cancellation);
            c.SetArg("followerCount", res.GetProperty("total").GetInt32());
        });
        On(507, async (_, c) =>
        {
            var res = await Helix(c).GetAsync("channels/followers", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["first"] = "1" }, ct: c.Cancellation);
            if (HelixClient.First(res) is { } f)
            {
                c.SetArg("latestFollower", f.GetProperty("user_name").GetString());
                c.SetArg("latestFollowerUserName", f.GetProperty("user_login").GetString());
                c.SetArg("latestFollowerId", f.GetProperty("user_id").GetString());
                c.SetArg("latestFollowerDate", f.GetProperty("followed_at").GetString());
            }
        });
        On(508, async (_, c) =>
        {
            var res = await Helix(c).GetAsync("subscriptions", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["first"] = "1" }, ct: c.Cancellation);
            c.SetArg("subscriberCount", res.GetProperty("total").GetInt32());
            if (res.TryGetProperty("points", out var p)) c.SetArg("subscriberPoints", p.GetInt32());
        });
        On(50, async (s, c) =>
        {
            var target = await ResolveUser(c, s.Str("userLogin"));
            c.SetArg("addTargetResult", target is not null);
            if (target is not { } t) return;
            c.SetArg("targetUser", t.GetProperty("display_name").GetString());
            c.SetArg("targetUserName", t.GetProperty("login").GetString());
            c.SetArg("targetUserId", t.GetProperty("id").GetString());
            var desc = t.GetProperty("description").GetString() ?? "";
            c.SetArg("targetDescription", desc);
            c.SetArg("targetDescriptionEscaped", Uri.EscapeDataString(desc));
            var img = t.GetProperty("profile_image_url").GetString() ?? "";
            c.SetArg("targetUserProfileImageUrl", img);
            c.SetArg("targetUserProfileImageUrlEscaped", Uri.EscapeDataString(img));
            var type = t.GetProperty("broadcaster_type").GetString() ?? "";
            c.SetArg("targetUserType", type);
            c.SetArg("targetIsAffiliate", type == "affiliate");
            c.SetArg("targetIsPartner", type == "partner");
            var created = t.GetProperty("created_at").GetDateTimeOffset();
            c.SetArg("createdAt", created);
            c.SetArg("accountAge", (long)(DateTimeOffset.UtcNow - created).TotalSeconds);
            var channel = HelixClient.First(await Helix(c).GetAsync("channels", new Dictionary<string, string?> { ["broadcaster_id"] = Id(t) }, ct: c.Cancellation));
            if (channel is { } ch)
            {
                c.SetArg("targetChannelTitle", ch.GetProperty("title").GetString());
                c.SetArg("game", ch.GetProperty("game_name").GetString());
                c.SetArg("gameId", ch.GetProperty("game_id").GetString());
                var tags = ch.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array ? tg.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
                c.SetArg("tagCount", tags.Count);
                c.SetArg("tags", tags);
                c.SetArg("tagsDelimited", string.Join(", ", tags));
                for (var i = 0; i < tags.Count; i++) c.SetArg($"tag{i}", tags[i]);
            }
        });
        On(51, async (s, c) =>
        {
            if (await ResolveUser(c, s.Str("userLogin")) is not { } target) return;
            var res = await Helix(c).GetAsync("channels/followers", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["user_id"] = Id(target) }, ct: c.Cancellation);
            var f = HelixClient.First(res);
            c.SetArg("isFollowing", f is not null);
            if (f is { } fol)
            {
                var since = fol.GetProperty("followed_at").GetDateTimeOffset();
                var age = DateTimeOffset.UtcNow - since;
                c.SetArg("followDate", since);
                c.SetArg("followAgeDays", (int)age.TotalDays);
                c.SetArg("followAgeLong", $"{(int)(age.TotalDays / 365)} years, {(int)(age.TotalDays % 365 / 30)} months, {(int)(age.TotalDays % 30)} days");
            }
        });
        On(52, async (s, c) =>
        {
            if (await ResolveUser(c, s.Str("userLogin")) is not { } target) return;
            var res = await Helix(c).GetAsync("teams/channel", new Dictionary<string, string?> { ["broadcaster_id"] = Id(target) }, ct: c.Cancellation);
            var teams = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                ? d.EnumerateArray().Select(x => x.GetProperty("team_display_name").GetString() ?? "").ToList() : [];
            c.SetArg("teamCount", teams.Count);
            for (var i = 0; i < teams.Count; i++) c.SetArg($"team{i}", teams[i]);
        });

        // ── rewards ──────────────────────────────────────────────────────────
        On(11, async (s, c) =>
        {
            var redemptionId = c.ArgString("redemptionId");
            var rewardId = c.ArgString("rewardId");
            if (redemptionId.Length == 0 || rewardId.Length == 0)
            {
                c.Log.LogWarning("Update Redemption Status needs a reward redemption trigger (redemptionId/rewardId)");
                return;
            }
            await Helix(c).SendAsync(new HttpMethod("PATCH"), "channel_points/custom_rewards/redemptions", new Dictionary<string, string?>
            {
                ["id"] = redemptionId, ["broadcaster_id"] = BroadcasterId(c), ["reward_id"] = rewardId,
            }, new { status = s.Int("status") == 0 ? "FULFILLED" : "CANCELED" }, ct: c.Cancellation);
        });
        On(200, (s, c) => RewardBool(c, s, "is_enabled", r => r.TryGetProperty("is_enabled", out var v) && v.GetBoolean()));
        On(201, (s, c) => RewardBool(c, s, "is_paused", r => r.TryGetProperty("is_paused", out var v) && v.GetBoolean()));
        On(202, async (s, c) => await RewardNumber(c, s, "cost", "cost", s.Int("operator")));
        On(204, async (s, c) =>
        {
            var seconds = await Compute(c, s, "cooldown", "global_cooldown_setting", "global_cooldown_seconds", s.Bool("additive") ? 1 : 0);
            await UpdateReward(c, RewardId(c, s), new Dictionary<string, object?> { ["is_global_cooldown_enabled"] = seconds > 0, ["global_cooldown_seconds"] = Math.Max(1, seconds) });
        });
        On(205, async (s, c) => await UpdateReward(c, RewardId(c, s), new Dictionary<string, object?> { ["title"] = c.Parse(s.Str("title")) }));
        On(206, async (s, c) => await UpdateReward(c, RewardId(c, s), new Dictionary<string, object?> { ["prompt"] = c.Parse(s.Str("prompt")) }));
        On(207, async (s, c) =>
        {
            var body = new Dictionary<string, object?>();
            if (s.Str("title").Length > 0) body["title"] = c.Parse(s.Str("title"));
            if (s.Str("prompt").Length > 0) body["prompt"] = c.Parse(s.Str("prompt"));
            if (s.Str("cost").Length > 0) body["cost"] = await Compute(c, s, "cost", null, "cost", s.Int("operator"));
            await UpdateReward(c, RewardId(c, s), body);
        });
        On(208, async (s, c) =>
        {
            if (await GetReward(c, RewardId(c, s)) is not { } r) return;
            c.SetArg("rewardId", r.GetProperty("id").GetString());
            c.SetArg("rewardName", r.GetProperty("title").GetString());
            c.SetArg("rewardPrompt", r.GetProperty("prompt").GetString());
            c.SetArg("rewardCost", r.GetProperty("cost").GetInt32());
            c.SetArg("rewardEnabled", r.GetProperty("is_enabled").GetBoolean());
            c.SetArg("rewardPaused", r.GetProperty("is_paused").GetBoolean());
            c.SetArg("rewardBackgroundColor", r.GetProperty("background_color").GetString());
        });
        On(209, (s, c) => c.Service<CherBotDb>().ResetCounters("reward", RewardId(c, s), usersOnly: false));
        On(210, (s, c) => c.Service<CherBotDb>().ResetCounters("reward", RewardId(c, s), usersOnly: true));
        On(211, (s, c) => RewardGroup(c, s, "is_enabled"));
        On(212, (s, c) => RewardGroup(c, s, "is_paused"));
        On(213, async (s, c) =>
        {
            var color = s.Bool("random") ? $"#{Random.Shared.Next(0x1000000):X6}" : c.Parse(s.Str("backgroundColor"));
            await UpdateReward(c, RewardId(c, s), new Dictionary<string, object?> { ["background_color"] = color });
        });
        On(214, async (s, c) =>
        {
            var n = await Compute(c, s, "redeems", "max_per_user_per_stream_setting", "max_per_user_per_stream", s.Bool("additive") ? 1 : 0);
            await UpdateReward(c, RewardId(c, s), new Dictionary<string, object?> { ["is_max_per_user_per_stream_enabled"] = n > 0, ["max_per_user_per_stream"] = Math.Max(1, n) });
        });
        On(215, async (s, c) =>
        {
            var n = await Compute(c, s, "redeems", "max_per_stream_setting", "max_per_stream", s.Bool("additive") ? 1 : 0);
            await UpdateReward(c, RewardId(c, s), new Dictionary<string, object?> { ["is_max_per_stream_enabled"] = n > 0, ["max_per_stream"] = Math.Max(1, n) });
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static HelixClient Helix(RunContext c) => c.Service<HelixClient>();

    private static string BroadcasterId(RunContext c) =>
        c.Service<TwitchAuth>().Get(TwitchRole.Broadcaster)?.UserId ?? throw new InvalidOperationException("Twitch broadcaster account is not linked");

    private static string Id(JsonElement user) => user.GetProperty("id").GetString()!;

    private static int ParseInt(string s, int fallback) =>
        int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static async Task<JsonElement?> ResolveUser(RunContext c, string raw)
    {
        var login = c.Parse(raw).Trim().TrimStart('@');
        if (login.Length == 0) return null;
        var user = await Helix(c).GetUserByLoginAsync(login, c.Cancellation);
        if (user is null) c.Log.LogWarning("Twitch user '{Login}' not found", login);
        return user;
    }

    private static async Task ChatSettings(RunContext c, Dictionary<string, object?> body) =>
        await Helix(c).SendAsync(new HttpMethod("PATCH"), "chat/settings",
            new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["moderator_id"] = BroadcasterId(c) }, body, ct: c.Cancellation);

    private static async Task Channel(RunContext c, object body) =>
        await Helix(c).SendAsync(new HttpMethod("PATCH"), "channels", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c) }, body, ct: c.Cancellation);

    private static async Task<List<string>> CurrentTags(RunContext c)
    {
        var ch = HelixClient.First(await Helix(c).GetAsync("channels", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c) }, ct: c.Cancellation));
        return ch is { } x && x.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(e => e.GetString() ?? "").ToList() : [];
    }

    private static async Task UserRole(RunContext c, SubActionDef s, HttpMethod method, string path)
    {
        if (await ResolveUser(c, s.Str("userLogin")) is not { } target) return;
        await Helix(c).SendAsync(method, path, new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["user_id"] = Id(target) }, ct: c.Cancellation);
    }

    private static string RewardId(RunContext c, SubActionDef s) => c.Parse(s.Str("rewardId"));

    private static async Task<JsonElement?> GetReward(RunContext c, string rewardId) =>
        HelixClient.First(await Helix(c).GetAsync("channel_points/custom_rewards",
            new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["id"] = rewardId }, ct: c.Cancellation));

    private static async Task UpdateReward(RunContext c, string rewardId, Dictionary<string, object?> body)
    {
        if (body.Count == 0) return;
        await Helix(c).SendAsync(new HttpMethod("PATCH"), "channel_points/custom_rewards",
            new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId(c), ["id"] = rewardId }, body, ct: c.Cancellation);
    }

    private static async Task RewardBool(RunContext c, SubActionDef s, string field, Func<JsonElement, bool> current)
    {
        var id = RewardId(c, s);
        var state = s.Int("state");
        // 0 = on (enabled / paused), 1 = off, 2 = toggle.
        var value = state == 2 ? !(await GetReward(c, id) is { } r && current(r)) : state == 0;
        await UpdateReward(c, id, new Dictionary<string, object?> { [field] = value });
    }

    private static async Task RewardGroup(RunContext c, SubActionDef s, string field)
    {
        var group = c.Parse(s.Str("groupName"));
        var ids = c.Service<TwitchService>().RewardSettings().Values.Where(r => string.Equals(r.Group, group, StringComparison.OrdinalIgnoreCase)).Select(r => r.RewardId);
        foreach (var id in ids)
            await UpdateReward(c, id, new Dictionary<string, object?> { [field] = s.Int("state") == 0 });
    }

    /// <summary>Applies a set/add/subtract operator (0/1/2) to a reward number, reading the current value when needed.</summary>
    private static async Task<int> Compute(RunContext c, SubActionDef s, string valueField, string? settingObject, string settingField, int op)
    {
        var amount = ParseInt(c.Parse(s.Str(valueField)), 0);
        if (op == 0) return amount;
        var current = 0;
        if (await GetReward(c, RewardId(c, s)) is { } r)
        {
            var holder = settingObject is null ? r : r.GetProperty(settingObject);
            if (holder.TryGetProperty(settingField, out var v) && v.ValueKind == JsonValueKind.Number) current = v.GetInt32();
        }
        return op == 2 ? Math.Max(0, current - amount) : current + amount;
    }

    private static async Task RewardNumber(RunContext c, SubActionDef s, string valueField, string apiField, int op) =>
        await UpdateReward(c, RewardId(c, s), new Dictionary<string, object?> { [apiField] = Math.Max(1, await Compute(c, s, valueField, null, apiField, op)) });
}
