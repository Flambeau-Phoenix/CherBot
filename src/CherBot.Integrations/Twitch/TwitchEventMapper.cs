using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Events;
using CherBot.Storage;

namespace CherBot.Integrations.Twitch;

/// <summary>
/// Maps EventSub notifications to Streamer.bot-compatible events (EventType ids and argument names), and tracks
/// first words per stream. Pure apart from the first-words table, so it is easy to test with recorded payloads.
/// </summary>
public sealed class TwitchEventMapper(CherBotDb db)
{
    public const string Platform = "twitch";

    /// <summary>Settings per reward id, e.g. which rewards skip their action's queue.</summary>
    public Func<string, bool> RewardSkipsQueue { get; set; } = _ => false;

    public IReadOnlyList<BotEvent> Map(string subscriptionType, JsonElement e)
    {
        var events = new List<BotEvent>();
        switch (subscriptionType)
        {
            case "channel.chat.message":
            {
                var data = ChatMessage(e);
                var first = db.TryAddFirstWord(Platform, (string)data["userId"]!, (string)data["user"]!, (string)data["message"]!);
                data["firstWordOfStream"] = first;
                events.Add(BotEvent.FromTypeName("TwitchChatMessage", data));
                if (first) events.Add(BotEvent.FromTypeName("TwitchFirstWord", new Dictionary<string, object?>(data)));
                break;
            }
            case "channel.chat.message_delete":
                events.Add(BotEvent.FromTypeName("TwitchChatMessageDeleted", new Dictionary<string, object?>
                {
                    ["targetMessageId"] = S(e, "message_id"), ["msgId"] = S(e, "message_id"),
                    ["targetUser"] = S(e, "target_user_name"), ["targetUserName"] = S(e, "target_user_login"), ["targetUserId"] = S(e, "target_user_id"),
                }));
                break;
            case "channel.chat.notification":
                MapNotification(e, events);
                break;
            case "channel.follow":
            {
                var d = User(e, "user");
                d["followDate"] = S(e, "followed_at");
                events.Add(BotEvent.FromTypeName("TwitchFollow", d));
                break;
            }
            case "channel.cheer":
            {
                var anonymous = B(e, "is_anonymous");
                var d = anonymous ? Anonymous() : User(e, "user");
                d["bits"] = I(e, "bits");
                d["message"] = S(e, "message");
                d["messageStripped"] = S(e, "message");
                d["anonymous"] = anonymous;
                events.Add(BotEvent.FromTypeName("TwitchCheer", d));
                break;
            }
            case "channel.raid":
            {
                var d = User(e, "from_broadcaster_user");
                d["viewers"] = I(e, "viewers");
                events.Add(BotEvent.FromTypeName("TwitchRaid", d));
                break;
            }
            case "channel.channel_points_custom_reward_redemption.add":
            {
                var d = User(e, "user");
                var reward = e.TryGetProperty("reward", out var r) ? r : default;
                var rewardId = reward.ValueKind == JsonValueKind.Object ? S(reward, "id") : "";
                d["rewardId"] = rewardId;
                d["rewardName"] = reward.ValueKind == JsonValueKind.Object ? S(reward, "title") : "";
                d["rewardCost"] = reward.ValueKind == JsonValueKind.Object ? I(reward, "cost") : 0;
                d["rewardPrompt"] = reward.ValueKind == JsonValueKind.Object ? S(reward, "prompt") : "";
                d["redemptionId"] = S(e, "id");
                d["rawInput"] = S(e, "user_input");
                d["rawInputEscaped"] = System.Net.WebUtility.HtmlEncode(S(e, "user_input"));
                d["rawInputUrlEncoded"] = Uri.EscapeDataString(S(e, "user_input"));
                d["counter"] = db.IncrementCounter("reward", rewardId);
                d["userCounter"] = db.IncrementCounter("reward", rewardId, (string)d["userId"]!);
                if (RewardSkipsQueue(rewardId)) d["skipQueue"] = true;
                events.Add(BotEvent.FromTypeName("TwitchRewardRedemption", d));
                break;
            }
            case "stream.online":
                db.ResetFirstWords();
                events.Add(BotEvent.FromTypeName("TwitchStreamOnline", new Dictionary<string, object?>
                {
                    ["broadcastUserId"] = S(e, "broadcaster_user_id"), ["broadcastUserName"] = S(e, "broadcaster_user_login"),
                    ["broadcastUser"] = S(e, "broadcaster_user_name"), ["startedAt"] = S(e, "started_at"), ["type"] = S(e, "type"),
                }));
                break;
            case "stream.offline":
                events.Add(BotEvent.FromTypeName("TwitchStreamOffline", new Dictionary<string, object?>
                {
                    ["broadcastUserId"] = S(e, "broadcaster_user_id"), ["broadcastUserName"] = S(e, "broadcaster_user_login"),
                }));
                break;
            case "channel.update":
                events.Add(BotEvent.FromTypeName("TwitchStreamUpdate", new Dictionary<string, object?>
                {
                    ["status"] = S(e, "title"), ["title"] = S(e, "title"), ["gameId"] = S(e, "category_id"), ["gameName"] = S(e, "category_name"),
                    ["language"] = S(e, "language"),
                }));
                break;
            case "channel.shoutout.create":
            {
                var d = User(e, "to_broadcaster_user");
                d["viewerCount"] = I(e, "viewer_count");
                events.Add(BotEvent.FromTypeName("TwitchShoutoutCreated", d));
                break;
            }
            case "channel.shoutout.receive":
            {
                var d = User(e, "from_broadcaster_user");
                d["viewerCount"] = I(e, "viewer_count");
                d["viewers"] = I(e, "viewer_count");
                events.Add(BotEvent.FromTypeName("TwitchShoutoutReceived", d));
                break;
            }
            case "channel.ad_break.begin":
                events.Add(BotEvent.FromTypeName("TwitchAdRun", new Dictionary<string, object?>
                {
                    ["adLength"] = I(e, "duration_seconds"), ["isAutomatic"] = B(e, "is_automatic"), ["startedAt"] = S(e, "started_at"),
                }));
                break;
        }
        return events;
    }

    private static Dictionary<string, object?> ChatMessage(JsonElement e)
    {
        var d = User(e, "chatter_user");
        var message = e.TryGetProperty("message", out var m) ? m : default;
        var text = message.ValueKind == JsonValueKind.Object ? S(message, "text") : "";
        var badges = e.TryGetProperty("badges", out var b) && b.ValueKind == JsonValueKind.Array
            ? b.EnumerateArray().Select(x => S(x, "set_id")).ToList()
            : [];
        var isBroadcaster = badges.Contains("broadcaster");
        var isMod = badges.Contains("moderator");
        var isVip = badges.Contains("vip");
        var isSub = badges.Contains("subscriber") || badges.Contains("founder");

        var emotes = new List<string>();
        var stripped = new System.Text.StringBuilder();
        if (message.ValueKind == JsonValueKind.Object && message.TryGetProperty("fragments", out var frags) && frags.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in frags.EnumerateArray())
            {
                if (S(f, "type") == "emote") emotes.Add(S(f, "text"));
                else stripped.Append(S(f, "text"));
            }
        }
        else stripped.Append(text);

        d["message"] = text;
        d["messageStripped"] = string.Join(' ', stripped.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        d["emotes"] = emotes;
        d["emoteCount"] = emotes.Count;
        d["rawInput"] = text;
        d["msgId"] = S(e, "message_id");
        d["messageId"] = S(e, "message_id");
        d["color"] = S(e, "color");
        d["badges"] = badges;
        d["isBroadcaster"] = isBroadcaster;
        d["isModerator"] = isMod || isBroadcaster;
        d["isVip"] = isVip;
        d["isSubscribed"] = isSub;
        d["role"] = isBroadcaster ? 4 : isMod ? 3 : isVip ? 2 : 1;
        d["messageType"] = S(e, "message_type");
        d["isInternal"] = false;
        d["bits"] = e.TryGetProperty("cheer", out var c) && c.ValueKind == JsonValueKind.Object ? I(c, "bits") : 0;
        if (e.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.Object)
        {
            d["isReply"] = true;
            d["reply.msgId"] = S(reply, "parent_message_id");
            d["reply.msgBody"] = S(reply, "parent_message_body");
            d["reply.userId"] = S(reply, "parent_user_id");
            d["reply.userLogin"] = S(reply, "parent_user_login");
            d["reply.userName"] = S(reply, "parent_user_name");
        }
        else d["isReply"] = false;
        if (e.TryGetProperty("source_broadcaster_user_id", out var src) && src.ValueKind == JsonValueKind.String)
            d["isSharedChat"] = src.GetString() != S(e, "broadcaster_user_id");
        return d;
    }

    private static void MapNotification(JsonElement e, List<BotEvent> events)
    {
        var type = S(e, "notice_type");
        var anonymous = B(e, "chatter_is_anonymous");
        var d = anonymous ? Anonymous() : User(e, "chatter_user");
        var message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object ? S(m, "text") : "";
        d["message"] = message;
        d["systemMessage"] = S(e, "system_message");
        d["anonymous"] = anonymous;

        JsonElement P(string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        string Tier(JsonElement p) => S(p, "sub_tier");

        switch (type)
        {
            case "sub":
            {
                var p = P("sub");
                d["tier"] = TierName(Tier(p), B(p, "is_prime"));
                d["subTier"] = Tier(p);
                d["isPrime"] = B(p, "is_prime");
                d["isMultiMonth"] = I(p, "duration_months") > 1;
                d["multiMonthDuration"] = I(p, "duration_months");
                events.Add(BotEvent.FromTypeName("TwitchSub", d));
                break;
            }
            case "resub":
            {
                var p = P("resub");
                d["tier"] = TierName(Tier(p), B(p, "is_prime"));
                d["subTier"] = Tier(p);
                d["isPrime"] = B(p, "is_prime");
                d["cumulativeMonths"] = I(p, "cumulative_months");
                d["monthStreak"] = I(p, "streak_months");
                d["streakShared"] = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("streak_months", out var sm) && sm.ValueKind == JsonValueKind.Number;
                d["rawInput"] = message;
                events.Add(BotEvent.FromTypeName("TwitchReSub", d));
                break;
            }
            case "sub_gift":
            {
                var p = P("sub_gift");
                d["tier"] = TierName(Tier(p), false);
                d["subTier"] = Tier(p);
                d["recipientUser"] = S(p, "recipient_user_name");
                d["recipientUserName"] = S(p, "recipient_user_login");
                d["recipientId"] = S(p, "recipient_user_id");
                d["monthsGifted"] = I(p, "duration_months");
                d["totalSubsGifted"] = I(p, "cumulative_total");
                d["fromGiftBomb"] = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("community_gift_id", out var cg) && cg.ValueKind == JsonValueKind.String;
                events.Add(BotEvent.FromTypeName("TwitchGiftSub", d));
                break;
            }
            case "community_sub_gift":
            {
                var p = P("community_sub_gift");
                d["tier"] = TierName(Tier(p), false);
                d["subTier"] = Tier(p);
                d["gifts"] = I(p, "total");
                d["totalGifts"] = I(p, "cumulative_total");
                events.Add(BotEvent.FromTypeName("TwitchGiftBomb", d));
                break;
            }
            case "announcement":
            {
                d["announcementColor"] = S(P("announcement"), "color");
                events.Add(BotEvent.FromTypeName("TwitchAnnouncement", d));
                break;
            }
        }
    }

    private static string TierName(string tier, bool prime) => prime ? "prime" : tier switch
    {
        "1000" => "tier 1",
        "2000" => "tier 2",
        "3000" => "tier 3",
        _ => tier,
    };

    /// <summary>Common user arguments from {prefix}_id / _login / _name fields.</summary>
    private static Dictionary<string, object?> User(JsonElement e, string prefix) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = S(e, $"{prefix}_name"),
        ["userName"] = S(e, $"{prefix}_login"),
        ["userId"] = S(e, $"{prefix}_id"),
        ["userType"] = Platform,
        ["platform"] = Platform,
    };

    private static Dictionary<string, object?> Anonymous() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["user"] = "AnAnonymousGifter", ["userName"] = "ananonymousgifter", ["userId"] = "", ["userType"] = Platform, ["platform"] = Platform,
    };

    private static string S(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int I(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool B(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
