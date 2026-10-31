using System.Text.Json;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Integrations.Community;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class TwitchTests
{
    private static TestHost Host(FakeTwitchHandler fake, params string[] extra) =>
        new(configure: b => b.Services.AddHttpClient("twitch").ConfigurePrimaryHttpMessageHandler(() => fake),
            extraArgs: ["--CherBot:Twitch:ClientId", "test-client", .. extra]);

    private static void Link(TestHost h, TwitchRole role, string token, string login, string id) =>
        h.Get<TwitchAuth>().Save(role, new TwitchToken(token, "refresh-0", DateTimeOffset.UtcNow.AddHours(4), login, id, login, ["user:write:chat"]));

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    private const string ChatJson = """
        {"broadcaster_user_id":"1001","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer",
         "chatter_user_id":"2002","chatter_user_login":"viewer","chatter_user_name":"Viewer","message_id":"m-1",
         "message":{"text":"hello chat","fragments":[]},"color":"#FF0000",
         "badges":[{"set_id":"moderator","id":"1","info":""},{"set_id":"subscriber","id":"12","info":"12"}],
         "message_type":"text","cheer":null,"reply":null}
        """;

    [Fact]
    public async Task Mapper_turns_chat_into_chat_message_and_first_words_once()
    {
        await using var e = new TestEngine();
        var mapper = new TwitchEventMapper(e.Get<CherBotDb>());
        var first = mapper.Map("channel.chat.message", Payload(ChatJson));
        Assert.Equal(["ChatMessage", "FirstWord"], first.Select(x => x.Type));
        var chat = first[0].Data;
        Assert.Equal("hello chat", chat["message"]);
        Assert.Equal("viewer", chat["userName"]);
        Assert.Equal("Viewer", chat["user"]);
        Assert.Equal(true, chat["isModerator"]);
        Assert.Equal(true, chat["isSubscribed"]);
        Assert.Equal(3, chat["role"]);

        var second = mapper.Map("channel.chat.message", Payload(ChatJson));
        Assert.Equal(["ChatMessage"], second.Select(x => x.Type));

        mapper.Map("stream.online", Payload("""{"broadcaster_user_id":"1001","type":"live","started_at":"2026-01-01T00:00:00Z"}"""));
        Assert.Contains(mapper.Map("channel.chat.message", Payload(ChatJson)), x => x.Type == "FirstWord");
    }

    [Fact]
    public async Task Mapper_handles_subs_gifts_cheers_raids_and_redemptions()
    {
        await using var e = new TestEngine();
        var mapper = new TwitchEventMapper(e.Get<CherBotDb>()) { RewardSkipsQueue = id => id == "r-fast" };

        var gift = mapper.Map("channel.chat.notification", Payload("""
            {"chatter_user_id":"3","chatter_user_login":"gifter","chatter_user_name":"Gifter","chatter_is_anonymous":false,
             "notice_type":"sub_gift","system_message":"","message":{"text":""},
             "sub_gift":{"duration_months":1,"cumulative_total":5,"recipient_user_id":"4","recipient_user_name":"Lucky","recipient_user_login":"lucky","sub_tier":"1000","community_gift_id":null}}
            """)).Single();
        Assert.Equal("GiftSub", gift.Type);
        Assert.Equal("Gifter", gift.Data["user"]);
        Assert.Equal("Lucky", gift.Data["recipientUser"]);
        Assert.Equal("tier 1", gift.Data["tier"]);

        var bomb = mapper.Map("channel.chat.notification", Payload("""
            {"chatter_is_anonymous":true,"notice_type":"community_sub_gift","message":{"text":""},
             "community_sub_gift":{"id":"c1","total":10,"sub_tier":"2000","cumulative_total":50}}
            """)).Single();
        Assert.Equal("GiftBomb", bomb.Type);
        Assert.Equal(10, bomb.Data["gifts"]);
        Assert.Equal("AnAnonymousGifter", bomb.Data["user"]);

        var resub = mapper.Map("channel.chat.notification", Payload("""
            {"chatter_user_id":"5","chatter_user_login":"loyal","chatter_user_name":"Loyal","notice_type":"resub","message":{"text":"still here"},
             "resub":{"cumulative_months":24,"duration_months":1,"streak_months":6,"sub_tier":"1000","is_prime":true,"is_gift":false}}
            """)).Single();
        Assert.Equal("ReSub", resub.Type);
        Assert.Equal(24, resub.Data["cumulativeMonths"]);
        Assert.Equal("prime", resub.Data["tier"]);

        var cheer = mapper.Map("channel.cheer", Payload("""{"is_anonymous":false,"user_id":"6","user_login":"bits","user_name":"Bits","bits":500,"message":"Cheer500 gg"}""")).Single();
        Assert.Equal(500, cheer.Data["bits"]);
        Assert.True(CherBot.Core.Engine.TriggerMatcher.Accepts(Trigger("TwitchCheer", new { min = 100 }), cheer));

        var raid = mapper.Map("channel.raid", Payload("""{"from_broadcaster_user_id":"7","from_broadcaster_user_login":"raider","from_broadcaster_user_name":"Raider","viewers":42}""")).Single();
        Assert.Equal(("Raid", 42, "Raider"), (raid.Type, raid.Data["viewers"], raid.Data["user"]));

        var redemption = mapper.Map("channel.channel_points_custom_reward_redemption.add", Payload("""
            {"id":"red-1","user_id":"8","user_login":"fan","user_name":"Fan","user_input":"play this","status":"unfulfilled",
             "reward":{"id":"r-fast","title":"Song","cost":100,"prompt":"Pick a song"}}
            """)).Single();
        Assert.Equal("r-fast", redemption.Data["rewardId"]);
        Assert.Equal("play this", redemption.Data["rawInput"]);
        Assert.Equal(true, redemption.Data["skipQueue"]);
        Assert.Equal(1L, redemption.Data["counter"]);
    }

    [Fact]
    public async Task Device_flow_links_the_account_and_stores_an_encrypted_token()
    {
        var fake = new FakeTwitchHandler { PendingPolls = 1, Login = "mybot", UserId = "2222" };
        await using var h = Host(fake, "--CherBot:Twitch:AutoConnect", "false");
        var auth = h.Get<TwitchAuth>();
        var flow = await auth.StartDeviceFlowAsync(TwitchRole.Bot, CancellationToken.None);
        Assert.Equal("ABCD-EFGH", flow.UserCode);
        await WaitUntil(() => auth.Flow(TwitchRole.Bot)?.Status == "complete", 10_000);

        var token = auth.Get(TwitchRole.Bot)!;
        Assert.Equal(("mybot", "2222", "device-token"), (token.Login, token.UserId, token.AccessToken));
        var stored = h.Get<CherBotDb>().GetSecret("twitch.token.bot")!;
        Assert.DoesNotContain("device-token", stored);
    }

    [Fact]
    public async Task Chat_send_uses_the_bot_account_and_refreshes_after_401()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake, "--CherBot:Twitch:AutoConnect", "false");
        Link(h, TwitchRole.Broadcaster, "b-token", "streamer", "1001");
        Link(h, TwitchRole.Bot, "stale-bot-token", "mybot", "2222");
        fake.RejectTokens.Add("stale-bot-token");

        var res = await h.Get<TwitchService>().SendChatAsync("hi from cherbot", useBot: true, fallback: true, null, CancellationToken.None);
        Assert.True(res.Sent);
        Assert.Equal("bot", res.Account);
        var send = fake.Calls.Last(c => c.Url.EndsWith("/helix/chat/messages"));
        Assert.Equal("refreshed-token", send.Auth);
        using var body = JsonDocument.Parse(send.Body!);
        Assert.Equal("1001", body.RootElement.GetProperty("broadcaster_id").GetString());
        Assert.Equal("2222", body.RootElement.GetProperty("sender_id").GetString());
        Assert.Contains(fake.Calls, c => c.Url.Contains("/oauth2/token") && c.Body!.Contains("refresh_token"));
    }

    [Fact]
    public async Task EventSub_session_subscribes_and_publishes_notifications()
    {
        var fake = new FakeTwitchHandler();
        await using var server = new FakeEventSubServer();
        await using var h = Host(fake, "--CherBot:Twitch:AutoConnect", "true");
        var tw = h.Get<TwitchService>();
        tw.EventSubUrl = server.Url;
        var chats = new List<BotEvent>();
        h.Get<EventBus>().Subscribe("t", ev => { lock (chats) chats.Add(ev); return Task.CompletedTask; }, ev => ev.Source == "Twitch");
        Link(h, TwitchRole.Broadcaster, "b-token", "streamer", "1001");

        await WaitUntil(() => tw.IsConnected, 15_000);
        await WaitUntil(() => fake.Calls.Count(c => c.Method == "POST" && c.Url.EndsWith("/helix/eventsub/subscriptions")) >= 10, 10_000);
        var sub = fake.Calls.First(c => c.Method == "POST" && c.Body!.Contains("\"channel.chat.message\""));
        Assert.Contains("\"session_id\":\"session-1\"", sub.Body);
        Assert.Contains(fake.Calls, c => c.Method == "DELETE" && c.Url.Contains("id=stale-1"));

        server.Notify("channel.chat.message", JsonDocument.Parse(ChatJson).RootElement);
        await WaitUntil(() => { lock (chats) return chats.Any(c => c.Type == "ChatMessage"); });
        Assert.Equal("connected", tw.GetStatus().State);
    }

    [Fact]
    public async Task Seeded_points_command_replies_in_chat()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake, "--CherBot:Twitch:AutoConnect", "false");
        Link(h, TwitchRole.Broadcaster, "b-token", "streamer", "1001");
        Assert.Contains(h.Get<ConfigService>().Commands, c => c.Name == "!points");
        Assert.Contains(h.Get<ConfigService>().Commands, c => c.Name == "!disc");

        var tw = h.Get<TwitchService>();
        tw.HandleNotification("channel.chat.message", JsonDocument.Parse(ChatJson.Replace("hello chat", "first!")).RootElement);
        tw.HandleNotification("channel.chat.message", JsonDocument.Parse(ChatJson.Replace("hello chat", "!points")).RootElement);

        await WaitUntil(() => fake.Calls.Any(c => c.Url.EndsWith("/helix/chat/messages") && c.Body!.Contains("points")), 10_000);
        var reply = JsonDocument.Parse(fake.Calls.Last(c => c.Url.EndsWith("/helix/chat/messages")).Body!).RootElement.GetProperty("message").GetString();
        Assert.Equal("Viewer has 11 points (Rank #1).", reply);

        tw.HandleNotification("channel.chat.message", JsonDocument.Parse(ChatJson.Replace("hello chat", "!disc set https://discord.gg/new")).RootElement);
        await WaitUntil(() => fake.Calls.Any(c => c.Body?.Contains("discord.gg/new") == true), 10_000);
    }

    [Fact]
    public async Task Python_prototype_database_migrates()
    {
        var pyDb = Path.Combine(Path.GetTempPath(), "cherbot-tests", $"py-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(pyDb)!);
        using (var conn = new SqliteConnection($"Data Source={pyDb}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE leaderboard(user_id TEXT PRIMARY KEY, user_name TEXT, points INTEGER, updated_ts TEXT);
                CREATE TABLE first_words(user_id TEXT PRIMARY KEY, user_name TEXT, word TEXT, ts TEXT, count INTEGER);
                CREATE TABLE quotes(id INTEGER PRIMARY KEY, quote TEXT, added_by TEXT, ts TEXT);
                CREATE TABLE globals(key TEXT PRIMARY KEY, value TEXT, persisted INTEGER);
                CREATE TABLE user_globals(platform TEXT, user_id TEXT, key TEXT, value TEXT, persisted INTEGER);
                INSERT INTO leaderboard VALUES('u1','Alice',120,''),('u2','Bob',30,'');
                INSERT INTO first_words VALUES('u1','Alice','hi','2026-01-01T00:00:00Z',1);
                INSERT INTO quotes VALUES(1,'a classic','Bob','');
                INSERT INTO globals VALUES('discord_url','https://discord.gg/x',1),('temp','x',0);
                INSERT INTO user_globals VALUES('twitch','u1','wins','3',1);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        await using var h = new TestHost(extraArgs: ["--CherBot:Twitch:AutoConnect", "false"]);
        var report = h.Get<PythonMigration>().Run(pyDb);
        Assert.Equal((2, 1, 1, 1, 1), (report.Points, report.FirstWords, report.Quotes, report.Globals, report.UserGlobals));
        var db = h.Get<CherBotDb>();
        Assert.Equal(120, db.GetPoints("twitch", "u1"));
        Assert.Equal("a classic", db.GetQuotes().Single().Quote);
        Assert.Equal(3, h.Get<CherBot.Core.State.VariableService>().GetUser("twitch", "u1", "wins"));
    }
}
