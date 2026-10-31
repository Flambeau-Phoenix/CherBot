using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Core.State;
using CherBot.Integrations.CustomCommands;
using CherBot.Integrations.Games;
using CherBot.Integrations.Giveaways;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Modules;
using CherBot.Integrations.Obs;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

/// <summary>Loyalty, games, scene commands, giveaways, modules, custom commands and the helper endpoints.</summary>
public class FeatureTests
{
    private static TestHost Host(FakeTwitchHandler fake, params string[] extra)
    {
        var h = new TestHost(configure: b => b.Services.AddHttpClient("twitch").ConfigurePrimaryHttpMessageHandler(() => fake),
            extraArgs: ["--CherBot:Twitch:ClientId", "test-client", "--CherBot:Twitch:AutoConnect", "false", .. extra]);
        h.Get<TwitchAuth>().Save(TwitchRole.Broadcaster,
            new TwitchToken("b-token", "refresh-0", DateTimeOffset.UtcNow.AddHours(4), "streamer", "1001", "Streamer", ["user:write:chat"]));
        return h;
    }

    /// <summary>Loyalty off, so balances only change through the feature under test.</summary>
    private static void NoLoyalty(TestHost h) => h.Get<LoyaltyService>().Save(new LoyaltySettings { Enabled = false });

    private static void Chat(TestHost h, string text, string userId = "2002", string login = "viewer", string name = "Viewer",
        string[]? badges = null, string[]? emotes = null)
    {
        var words = text.Split(' ');
        var payload = JsonSerializer.SerializeToElement(new
        {
            broadcaster_user_id = "1001", broadcaster_user_login = "streamer", broadcaster_user_name = "Streamer",
            chatter_user_id = userId, chatter_user_login = login, chatter_user_name = name, message_id = "m-" + Guid.NewGuid().ToString("N"),
            message = new
            {
                text,
                fragments = words.Select((w, i) => emotes?.Contains(w) == true
                    ? new { type = "emote", text = w }
                    : new { type = "text", text = i < words.Length - 1 ? w + " " : w }).ToArray(),
            },
            color = "", message_type = "text",
            badges = (badges ?? []).Select(b => new { set_id = b, id = "1", info = "" }).ToArray(),
        });
        h.Get<TwitchService>().HandleNotification("channel.chat.message", payload);
    }

    private static List<string> SentChat(FakeTwitchHandler fake) =>
        fake.Calls.Where(c => c.Url.EndsWith("/helix/chat/messages") && c.Body is not null)
            .Select(c => JsonDocument.Parse(c.Body!).RootElement.GetProperty("message").GetString()!).ToList();

    private static Task WaitForChat(FakeTwitchHandler fake, string contains, int timeoutMs = 10_000) =>
        WaitUntil(() => SentChat(fake).Any(m => m.Contains(contains)), timeoutMs);

    private sealed class SeqRandom(params int[] values) : IGameRandom
    {
        private readonly Queue<int> _values = new(values);
        public int Next(int min, int maxExclusive) => _values.Count > 0 ? Math.Clamp(_values.Dequeue(), min, maxExclusive - 1) : min;
    }

    // ── seeding and catalog fixes ──────────────────────────────────────────

    [Fact]
    public async Task Seeded_feature_commands_start_disabled()
    {
        await using var h = Host(new FakeTwitchHandler());
        var cmds = h.Get<ConfigService>().Commands;
        foreach (var name in new[] { "!gamble", "!slots", "!coinflip", "!duel", "!accept", "!decline", "!heist", "!roulette", "!scene", "!scenes", "!giveaway", "!permit" })
            Assert.False(cmds.Single(c => c.Name == name).Enabled, name);
        Assert.Equal("moderator", cmds.Single(c => c.Name == "!scene").Str("minRole"));
        Assert.Equal("moderator", cmds.Single(c => c.Name == "!giveaway").Str("minRole"));
        Assert.Contains(cmds, c => c.Name == "!points" && c.Enabled);
    }

    [Fact]
    public async Task Variable_and_obs_vendor_events_carry_their_trigger_fields()
    {
        await using var e = new TestEngine();
        var events = e.Capture(ev => ev.Type.StartsWith("GlobalVariable"));
        e.Get<VariableService>().SetGlobal("deaths", 3, persisted: true);
        e.Get<VariableService>().SetGlobal("deaths", 4, persisted: true);
        await WaitUntil(() => events.Count >= 2);
        Assert.All(events, ev => Assert.Equal("deaths", ev.Data["variableName"]));
        Assert.True(TriggerMatcher.Accepts(Trigger("GlobalVariableUpdated", new { variableName = "deaths" }), events[1]));
        Assert.False(TriggerMatcher.Accepts(Trigger("GlobalVariableUpdated", new { variableName = "wins" }), events[1]));

        var vendor = BotEvent.FromTypeName("ObsVendorEvent", new Dictionary<string, object?> { ["vendorName"] = "move-plugin", ["eventName"] = "MoveEnded" });
        Assert.True(TriggerMatcher.Accepts(Trigger("ObsVendorEvent", new { vendorName = "move-plugin" }), vendor));
        Assert.False(TriggerMatcher.Accepts(Trigger("ObsVendorEvent", new { vendorName = "other" }), vendor));
    }

    [Fact]
    public async Task Mapper_emits_emotes_and_stripped_text()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var chats = new List<BotEvent>();
        h.Get<EventBus>().Subscribe("t", ev => { lock (chats) chats.Add(ev); return Task.CompletedTask; }, ev => ev.Type == "ChatMessage");
        Chat(h, "hello Kappa world Kappa", emotes: ["Kappa"]);
        await WaitUntil(() => { lock (chats) return chats.Count > 0; });
        var d = chats[0].Data;
        Assert.Equal(["Kappa", "Kappa"], (List<string>)d["emotes"]!);
        Assert.Equal(2, d["emoteCount"]);
        Assert.Equal("hello world", d["messageStripped"]);
    }

    // ── loyalty ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Loyalty_awards_chat_points_with_multipliers_cooldown_and_bonuses()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var loyalty = h.Get<LoyaltyService>();
        loyalty.Save(new LoyaltySettings { ChatPoints = 2, FirstWordPoints = 0, SubscriberMultiplier = 2.0, ChatCooldownSeconds = 60, FollowBonus = 50, CurrencyName = "cherries" });
        var db = h.Get<CherBotDb>();

        Chat(h, "hi", userId: "3001", login: "subfan", name: "SubFan", badges: ["subscriber"]);
        Chat(h, "hi again", userId: "3001", login: "subfan", name: "SubFan", badges: ["subscriber"]);
        Chat(h, "hello", userId: "3002", login: "plain", name: "Plain");
        Assert.Equal(0, db.GetPoints("twitch", "3001")); // first words pay FirstWordPoints (0 here)
        Chat(h, "third", userId: "3002", login: "plain", name: "Plain");
        Assert.Equal(0, db.GetPoints("twitch", "3002")); // still inside the 60s cooldown after the first message

        loyalty.Save(new LoyaltySettings { ChatPoints = 2, SubscriberMultiplier = 2.0, ChatCooldownSeconds = 0, FollowBonus = 50, CurrencyName = "cherries" });
        Chat(h, "again", userId: "3001", login: "subfan", name: "SubFan", badges: ["subscriber"]);
        Assert.Equal(4, db.GetPoints("twitch", "3001"));

        h.Get<TwitchService>().HandleNotification("channel.follow",
            JsonSerializer.SerializeToElement(new { user_id = "3003", user_login = "newfan", user_name = "NewFan", followed_at = "2026-01-01T00:00:00Z" }));
        await WaitUntil(() => db.GetPoints("twitch", "3003") == 50);

        var res = await h.Api.GetFromJsonAsync<JsonElement>("/v1/points?search=newfan");
        Assert.Equal(50, res.GetProperty("points")[0].GetProperty("points").GetInt64());
        Assert.Equal("cherries", (await h.Api.GetFromJsonAsync<JsonElement>("/v1/loyalty/settings")).GetProperty("settings").GetProperty("currencyName").GetString());
    }

    // ── games ──────────────────────────────────────────────────────────────

    [Fact]
    public void Bets_parse_amounts_suffixes_and_fractions()
    {
        Assert.Equal(1000, GameService.ParseBet("1k", 5000));
        Assert.Equal(250, GameService.ParseBet("half", 500));
        Assert.Equal(100, GameService.ParseBet("25%", 400));
        Assert.Equal(77, GameService.ParseBet("all", 77));
        Assert.Equal(2_500_000, GameService.ParseBet("2.5m", 0));
        Assert.Null(GameService.ParseBet("lots", 100));
        Assert.Null(GameService.ParseBet("0", 100));
    }

    [Fact]
    public async Task Games_pay_out_from_the_injected_random()
    {
        await using var h = Host(new FakeTwitchHandler());
        var games = h.Get<GameService>();
        var db = h.Get<CherBotDb>();
        var alice = new GamePlayer("u1", "alice", "Alice");
        db.SetPoints("twitch", "u1", "Alice", 1000);
        games.Save(new GameSettings
        {
            Gamble = new() { CooldownSeconds = 0 }, Slots = new() { CooldownSeconds = 0 },
            Coinflip = new() { CooldownSeconds = 0 }, Roulette = new() { CooldownSeconds = 0 },
        });

        games.Random = new SeqRandom(10);
        Assert.Contains("won 100", games.Gamble(alice, "100"));
        Assert.Equal(1100, db.GetPoints("twitch", "u1"));

        games.Random = new SeqRandom(5, 5, 5);
        Assert.Contains("JACKPOT", games.Slots(alice, "100"));
        Assert.Equal(1100 + 4900, db.GetPoints("twitch", "u1"));

        games.Random = new SeqRandom(1);
        Assert.Contains("lost 100", games.Coinflip(alice, "100", "heads"));
        games.Random = new SeqRandom(0);
        Assert.Contains("won 100", games.Coinflip(alice, "heads", "100"));

        games.Random = new SeqRandom(1);
        Assert.Contains("1 red", games.Roulette(alice, "100", "red"));
        Assert.Equal(6000 - 100 + 100 + 100, db.GetPoints("twitch", "u1"));

        Assert.Contains("only have", games.Gamble(alice, "9000"));
        Assert.Contains("maximum bet", games.Gamble(alice, "999999"));
        Assert.Contains("minimum bet", games.Gamble(alice, "1"));
    }

    [Fact]
    public async Task Duels_and_heists_settle_balances()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var games = h.Get<GameService>();
        var db = h.Get<CherBotDb>();
        var alice = new GamePlayer("u1", "alice", "Alice");
        var bob = new GamePlayer("u2", "bob", "Bob");
        db.SetPoints("twitch", "u1", "Alice", 500);
        db.SetPoints("twitch", "u2", "Bob", 500);

        Assert.Contains("challenges @bob", games.DuelChallenge(alice, "@bob", "100"));
        games.Random = new SeqRandom(0);
        Assert.Contains("Alice defeats Bob", games.DuelAccept(bob));
        Assert.Equal((600L, 400L), (db.GetPoints("twitch", "u1"), db.GetPoints("twitch", "u2")));
        Assert.Contains("nobody has challenged", games.DuelAccept(bob));

        var release = new TaskCompletionSource();
        games.Delay = (_, _) => release.Task;
        games.Random = new SeqRandom(1, 100);
        games.Save(new GameSettings { Heist = new() { BaseSuccessPercent = 50, MaxSuccessPercent = 60, PayoutMultiplier = 2.0 } });
        Assert.Contains("planning a heist", games.HeistJoin(alice, "100"));
        Assert.Contains("joined the heist", games.HeistJoin(bob, "100"));
        Assert.Equal(500, db.GetPoints("twitch", "u1"));
        release.SetResult();
        await WaitForChat(fake, "Heist complete");
        Assert.Equal((700L, 300L), (db.GetPoints("twitch", "u1"), db.GetPoints("twitch", "u2")));
    }

    [Fact]
    public async Task Enabled_game_command_replies_in_chat()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        NoLoyalty(h);
        var config = h.Get<ConfigService>();
        var gamble = config.Commands.Single(c => c.Name == "!gamble");
        gamble.Enabled = true;
        config.SaveCommand(gamble);
        h.Get<CherBotDb>().SetPoints("twitch", "2002", "Viewer", 500);
        h.Get<GameService>().Random = new SeqRandom(99);

        Chat(h, "!gamble 100");
        await WaitForChat(fake, "rolled 99 and lost 100");
        Assert.Equal(400, h.Get<CherBotDb>().GetPoints("twitch", "2002"));
    }

    // ── scenes ─────────────────────────────────────────────────────────────

    [Fact]
    public void Scene_names_match_exactly_then_by_prefix_then_by_substring()
    {
        string[] scenes = ["Main", "Gameplay", "Game Over", "BRB"];
        Assert.Equal("BRB", SceneCommandHandlers.Match(scenes, "brb").Match);
        Assert.Equal("Main", SceneCommandHandlers.Match(scenes, "ma").Match);
        Assert.Equal("Gameplay", SceneCommandHandlers.Match(scenes, "play").Match);
        var (match, candidates) = SceneCommandHandlers.Match(scenes, "game");
        Assert.Null(match);
        Assert.Equal(["Gameplay", "Game Over"], candidates);
        Assert.Null(SceneCommandHandlers.Match(scenes, "zzz").Match);
    }

    [Fact]
    public async Task Scene_from_chat_switches_obs_and_respects_the_allow_list()
    {
        await using var obs = new FakeObsServer(null);
        await using var h = new TestHost(extraArgs:
        [
            "--CherBot:Twitch:AutoConnect", "false",
            "--CherBot:Obs:Connections:0:Name", "Main OBS", "--CherBot:Obs:Connections:0:Host", "127.0.0.1",
            "--CherBot:Obs:Connections:0:Port", obs.Port.ToString(), "--CherBot:Obs:Connections:0:RetrySeconds", "2",
        ]);
        await WaitUntil(() => h.Get<ObsService>().Client(null) is not null, 10_000);

        async Task<RunResult> Run(string input, params string[] allowed)
        {
            var a = new ActionDef { Name = "scene " + Guid.NewGuid(), SubActions = [Sa(SceneCommandHandlers.SceneFromChat, new { allowedScenes = allowed })] };
            h.Get<ConfigService>().SaveAction(a);
            return await h.Get<ActionDispatcher>().RunNowAsync(a, new Dictionary<string, object?> { ["rawInput"] = input }, new DispatchSource("test"), CancellationToken.None);
        }

        Assert.Equal("Switched to Gameplay.", (await Run("play")).Args["reply"]);
        Assert.Contains(obs.Requests, r => r.Type == "SetCurrentProgramScene" && r.Data.GetProperty("sceneName").GetString() == "Gameplay");
        Assert.Contains("several scenes", (string)(await Run("game")).Args["reply"]!);
        Assert.Contains("No scene matches", (string)(await Run("brb", "Main", "Gameplay")).Args["reply"]!);

        var res = await h.Api.GetFromJsonAsync<JsonElement>("/v1/obs/scenes");
        Assert.Equal("Main", res.GetProperty("current").GetString());
        Assert.Equal(["Main", "Gameplay", "Game Over", "BRB"], res.GetProperty("scenes").EnumerateArray().Select(x => x.GetString()));
    }

    // ── giveaways ──────────────────────────────────────────────────────────

    private static GiveawayDef NewGiveaway(GiveawayService svc, Action<GiveawayDef>? setup = null)
    {
        var d = new GiveawayDef { Name = "Steam key", Prize = "a Steam key", Commands = ["!join"] };
        setup?.Invoke(d);
        return svc.Save(d);
    }

    [Fact]
    public async Task Giveaway_entries_from_chat_respect_tickets_cost_and_role()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        NoLoyalty(h);
        var svc = h.Get<GiveawayService>();
        var db = h.Get<CherBotDb>();
        var d = NewGiveaway(svc, g => { g.MaxTickets = 3; g.EntryCost = 10; g.SubscriberBonusTickets = 1; });
        var cmd = h.Get<ConfigService>().GetCommand(d.CommandId!.Value)!;
        Assert.False(cmd.Enabled);

        Assert.True((await svc.StartAsync(d.Id)).Ok);
        Assert.True(h.Get<ConfigService>().GetCommand(d.CommandId!.Value)!.Enabled);
        await WaitForChat(fake, "Type !join to enter (until stopped)");

        db.SetPoints("twitch", "4001", "Alice", 100);
        Chat(h, "!join", userId: "4001", login: "alice", name: "Alice");
        await WaitForChat(fake, "Alice, you're in with 1 ticket(s)!");
        Chat(h, "!join", userId: "4001", login: "alice", name: "Alice");
        await WaitForChat(fake, "Alice, you're already entered");
        Chat(h, "!join 2", userId: "4001", login: "alice", name: "Alice");
        await WaitForChat(fake, "Alice, you're in with 3 ticket(s)!");
        Assert.Equal(70, db.GetPoints("twitch", "4001"));

        db.SetPoints("twitch", "4002", "Bob", 5);
        Chat(h, "!join", userId: "4002", login: "bob", name: "Bob");
        await WaitForChat(fake, "Bob, entering costs 10");

        db.SetPoints("twitch", "4003", "Sub", 10);
        Chat(h, "!join", userId: "4003", login: "sub", name: "Sub", badges: ["subscriber"]);
        await WaitForChat(fake, "Sub, you're in with 2 ticket(s)!");

        var entries = svc.Entries(d.Id);
        Assert.Equal([3, 2], entries.Select(e => e.TotalTickets));

        Assert.True((await svc.CancelAsync(d.Id)).Ok);
        Assert.Equal((100L, 10L), (db.GetPoints("twitch", "4001"), db.GetPoints("twitch", "4003")));
        Assert.False(h.Get<ConfigService>().GetCommand(d.CommandId!.Value)!.Enabled);
    }

    [Fact]
    public async Task Giveaway_draw_is_weighted_and_rerolls_skip_previous_winners()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var svc = h.Get<GiveawayService>();
        var d = NewGiveaway(svc, g => { g.MaxTickets = 5; g.MinRole = "subscriber"; });
        await svc.StartAsync(d.Id);
        var sub = new Dictionary<string, object?> { ["isSubscribed"] = true };
        Assert.False((await svc.EnterAsync(d.Id, new Dictionary<string, object?> { ["userId"] = "x", ["user"] = "Viewer" }, null)).Ok);
        await svc.EnterAsync(d.Id, new Dictionary<string, object?>(sub) { ["userId"] = "a", ["user"] = "Ann" }, "1");
        await svc.EnterAsync(d.Id, new Dictionary<string, object?>(sub) { ["userId"] = "b", ["user"] = "Ben" }, "4");

        svc.Pick = max => max - 1;
        var drawn = await svc.StopAsync(d.Id);
        Assert.Equal("Winner: Ben", drawn.Message);
        await WaitForChat(fake, "Congratulations Ben, you won a Steam key!");

        var reroll = await svc.RerollAsync(d.Id);
        Assert.Equal("Winner: Ann", reroll.Message);
        Assert.Equal(["rerolled", "won"], svc.Winners(d.Id).Select(w => w.Status));
        await svc.RerollAsync(d.Id);
        Assert.False((await svc.DrawAsync(d.Id)).Ok);
    }

    [Fact]
    public async Task Giveaway_closes_on_time_resumes_after_restart_and_redraws_unclaimed_winners()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var svc = h.Get<GiveawayService>();
        var d = NewGiveaway(svc, g => { g.DurationSeconds = 60; g.ClaimSeconds = 30; });
        await svc.StartAsync(d.Id);
        await svc.EnterAsync(d.Id, new Dictionary<string, object?> { ["userId"] = "a", ["user"] = "Ann" }, null);
        await svc.EnterAsync(d.Id, new Dictionary<string, object?> { ["userId"] = "b", ["user"] = "Ben" }, null);
        var closesAt = svc.Get(d.Id)!.ClosesAt;
        Assert.NotNull(closesAt);

        // A fresh service (as after a restart) reloads the open giveaway with its original closing time.
        var restarted = new GiveawayService(h.Get<CherBotDb>(), h.Get<ConfigService>(), h.Get<EventBus>(), h.Get<TwitchService>(), h.Get<LoyaltyService>(),
            h.Get<HelixClient>(), h.Get<TwitchAuth>(), h.Get<TemplateEngine>(), NullLogger<GiveawayService>.Instance);
        Assert.Equal((GiveawayStatus.Open, closesAt), (restarted.Get(d.Id)!.Status, restarted.Get(d.Id)!.ClosesAt));

        svc.Pick = _ => 0;
        svc.Clock = () => DateTimeOffset.UtcNow.AddSeconds(61);
        await svc.TickAsync();
        Assert.Equal(GiveawayStatus.Drawn, svc.Get(d.Id)!.Status);
        await WaitForChat(fake, "Ann won a Steam key! Ann, say something in chat within 30s");

        svc.Clock = () => DateTimeOffset.UtcNow.AddSeconds(31);
        await svc.TickAsync();
        await WaitForChat(fake, "Ann didn't claim");
        Assert.Equal(["expired", "pending"], svc.Winners(d.Id).Select(w => w.Status));

        Chat(h, "it's me!", userId: "b", login: "ben", name: "Ben");
        await WaitUntil(() => svc.Winners(d.Id).Last().Status == "claimed");
        await WaitForChat(fake, "Ben claimed a Steam key");
    }

    [Fact]
    public async Task Giveaway_api_and_chat_control_command()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var created = await (await h.Api.PostAsJsonAsync("/v1/giveaways", new { name = "Art", prize = "a drawing", commands = new[] { "!art" }, keyword = "pickme" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("giveaway").GetProperty("id").GetGuid();

        var control = h.Get<ConfigService>().Commands.Single(c => c.Name == "!giveaway");
        control.Enabled = true;
        h.Get<ConfigService>().SaveCommand(control);
        Chat(h, "!giveaway start art", badges: ["moderator"]);
        await WaitUntil(() => h.Get<GiveawayService>().Get(id)!.Status == GiveawayStatus.Open);

        Chat(h, "oh pickme please", userId: "5001", login: "kw", name: "Keyword");
        await WaitUntil(() => h.Get<GiveawayService>().Entries(id).Count == 1);

        var entries = await h.Api.GetFromJsonAsync<JsonElement>($"/v1/giveaways/{id}/entries");
        Assert.Equal("Keyword", entries.GetProperty("entries")[0].GetProperty("userName").GetString());
        var draw = await h.Api.PostAsync($"/v1/giveaways/{id}/draw", null);
        Assert.Equal(HttpStatusCode.OK, draw.StatusCode);
        var winners = await h.Api.GetFromJsonAsync<JsonElement>($"/v1/giveaways/{id}/winners");
        var winnerId = winners.GetProperty("winners")[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await h.Api.PostAsync($"/v1/giveaways/{id}/winners/{winnerId}/claim", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await h.Api.PostAsync($"/v1/giveaways/{id}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await h.Api.DeleteAsync($"/v1/giveaways/{id}")).StatusCode);
        Assert.DoesNotContain(h.Get<ConfigService>().Commands, c => c.Name == "Giveaway: Art");
    }

    // ── modules ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_alerts_thank_supporters_and_respect_thresholds()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var alerts = h.Get<ChatAlertsModule>();
        alerts.Pick = _ => 0;
        var s = alerts.Settings;
        s.Enabled = true;
        s.Alerts["cheer"].MinAmount = 1000;
        alerts.Save(s);
        var tw = h.Get<TwitchService>();

        tw.HandleNotification("channel.follow", JsonSerializer.SerializeToElement(new { user_id = "6001", user_login = "fan", user_name = "Fan" }));
        await WaitForChat(fake, "Thanks for the follow, Fan!");

        tw.HandleNotification("channel.cheer", JsonSerializer.SerializeToElement(new { is_anonymous = false, user_id = "6002", user_login = "b", user_name = "Bits", bits = 500, message = "" }));
        tw.HandleNotification("channel.chat.notification", JsonSerializer.SerializeToElement(new
        {
            chatter_user_id = "6003", chatter_user_login = "g", chatter_user_name = "Gifter", chatter_is_anonymous = false, notice_type = "sub_gift",
            message = new { text = "" },
            sub_gift = new { duration_months = 1, cumulative_total = 5, recipient_user_id = "9", recipient_user_name = "Lucky", recipient_user_login = "lucky", sub_tier = "1000", community_gift_id = "bomb-1" },
        }));
        tw.HandleNotification("channel.raid", JsonSerializer.SerializeToElement(new { from_broadcaster_user_id = "7", from_broadcaster_user_login = "r", from_broadcaster_user_name = "Raider", viewers = 42 }));
        await WaitForChat(fake, "Raider is here with 42 viewers");
        Assert.DoesNotContain(alerts.Sent, m => m.Contains("Bits") || m.Contains("Lucky"));

        var test = await (await h.Api.PostAsJsonAsync("/v1/modules/chat-alerts/test", new { alert = "resub" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("LoyalFan resubscribed for 12 months! Thank you for the support! 🎉", test.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Emote_pyramids_are_celebrated_rewarded_or_reported_broken()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        NoLoyalty(h);
        var pyramid = h.Get<EmotePyramidModule>();
        pyramid.Save(new EmotePyramidSettings { Enabled = true, RewardBase = 10, RewardPerWidth = 5, BrokenMessage = "{breaker} broke {user}'s pyramid!" });
        string[] kappa = ["Kappa"];

        foreach (var row in new[] { "Kappa", "Kappa Kappa", "Kappa Kappa Kappa", "Kappa Kappa", "Kappa" })
            Chat(h, row, userId: "7001", login: "builder", name: "Builder", emotes: kappa);
        await WaitUntil(() => pyramid.Completed == 1);
        await WaitForChat(fake, "Builder built a 3-wide Kappa pyramid!");
        Assert.Equal(25, h.Get<CherBotDb>().GetPoints("twitch", "7001"));

        Chat(h, "Kappa", userId: "7001", login: "builder", name: "Builder", emotes: kappa);
        Chat(h, "Kappa Kappa", userId: "7001", login: "builder", name: "Builder", emotes: kappa);
        Chat(h, "lol", userId: "7002", login: "rude", name: "Rude");
        await WaitForChat(fake, "Rude broke Builder's pyramid!");

        // Words that aren't emotes don't count when "emotes only" is on.
        foreach (var row in new[] { "hey", "hey hey", "hey hey hey", "hey hey", "hey" }) Chat(h, row, userId: "7003", login: "w", name: "Words");
        await Task.Delay(300);
        Assert.Equal(1, pyramid.Completed);
    }

    [Fact]
    public async Task Spam_filter_blocks_before_commands_logs_and_escalates()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        NoLoyalty(h);
        var filter = h.Get<SpamFilterModule>();
        var s = filter.Settings;
        s.Enabled = true;
        s.Caps.Enabled = true;
        filter.Save(s);
        Assert.True(h.Get<ConfigService>().Commands.Single(c => c.Name == "!permit").Enabled);

        var triggered = new List<BotEvent>();
        h.Get<EventBus>().Subscribe("t", ev => { lock (triggered) triggered.Add(ev); return Task.CompletedTask; }, ev => ev.TypeId == 401);

        Chat(h, "!points visit evil-site.com/free now", userId: "8001", login: "spammer", name: "Spammer");
        await filter.LastPunishment;
        await WaitUntil(() => h.Get<CherBotDb>().GetModLog(10).Any(l => l.Filter == "links"));
        Assert.Contains(fake.Calls, c => c.Method == "DELETE" && c.Url.Contains("/helix/moderation/chat") && c.Url.Contains("message_id=m-"));
        await Task.Delay(300);
        lock (triggered) Assert.Empty(triggered);

        filter.Permit("spammer");
        Chat(h, "!points visit evil-site.com/free now", userId: "8001", login: "spammer", name: "Spammer");
        await WaitUntil(() => { lock (triggered) return triggered.Count == 1; });

        // Allowed domains, subscribers and normal text pass; shouting doesn't.
        Assert.Null(filter.Evaluate(new Dictionary<string, object?> { ["userId"] = "x", ["message"] = "watch twitch.tv/streamer" }));
        Assert.Null(filter.Evaluate(new Dictionary<string, object?> { ["userId"] = "x", ["message"] = "THIS IS SO LOUD RIGHT NOW", ["isSubscribed"] = true }));
        Assert.Equal("caps", filter.Evaluate(new Dictionary<string, object?> { ["userId"] = "x", ["message"] = "THIS IS SO LOUD RIGHT NOW" })?.Name);

        s.Escalation = true;
        filter.Save(s);
        Assert.Equal([("warn", 10), ("timeout", 10), ("timeout", 600), ("timeout", 600)],
            Enumerable.Range(0, 4).Select(_ => filter.ActionFor("repeat-offender", s.Caps)));

        var sim = await (await h.Api.PostAsJsonAsync("/v1/chat/simulate", new { message = "free stuff at scam.xyz", user = "Sim" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(sim.GetProperty("filtered").GetBoolean());
        var log = await h.Api.GetFromJsonAsync<JsonElement>("/v1/modules/spam-filter/log");
        Assert.True(log.GetProperty("log").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Modules_api_lists_every_module_and_toggles_them()
    {
        await using var h = Host(new FakeTwitchHandler());
        var list = await h.Api.GetFromJsonAsync<JsonElement>("/v1/modules");
        Assert.Equal(["chat-alerts", "emote-pyramid", "spam-filter", "loyalty", "games", "giveaways", "scene-commands"],
            list.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("id").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await h.Api.PostAsJsonAsync("/v1/modules/games/enabled", new { enabled = true })).StatusCode);
        Assert.All(h.Get<ConfigService>().Commands.Where(c => c.Group == GameService.Group), c => Assert.True(c.Enabled));

        var settings = await h.Api.GetFromJsonAsync<JsonElement>("/v1/modules/emote-pyramid/settings");
        Assert.Equal(3, settings.GetProperty("minWidth").GetInt32());
        var put = await h.Api.PutAsJsonAsync("/v1/modules/emote-pyramid/settings", new { enabled = true, minWidth = 4 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal((true, 4), (h.Get<EmotePyramidModule>().Enabled, h.Get<EmotePyramidModule>().Settings.MinWidth));
    }

    // ── custom commands ────────────────────────────────────────────────────

    [Fact]
    public async Task Custom_commands_sync_to_commands_and_expand_tokens()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        NoLoyalty(h);
        var resp = await h.Api.PostAsJsonAsync("/v1/custom-commands", new
        {
            name = "!hug", aliases = new[] { "!cuddle" }, globalCooldown = 0, userCooldown = 0,
            responses = new[] { "{user} hugs {touser}! ({count}) {random:7-7} {pick:x} {2}" },
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("command").GetProperty("id").GetGuid();
        var svc = h.Get<CustomCommandService>();
        var def = svc.Get(id)!;
        var cmd = h.Get<ConfigService>().GetCommand(def.CommandId!.Value)!;
        Assert.Equal(("Custom Commands", "!hug\n!cuddle"), (cmd.Group, cmd.Command));
        Assert.Equal(CustomCommandHandlers.Response, h.Get<ConfigService>().GetAction(def.ActionId!.Value)!.SubActions.Single().Type);

        Chat(h, "!cuddle @Friend extra");
        await WaitForChat(fake, "Viewer hugs Friend! (1) 7 x extra");

        var test = await (await h.Api.PostAsJsonAsync($"/v1/custom-commands/{id}/test", new { user = "Tester", input = "@Pal" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Tester hugs Pal! (2) 7 x", test.GetProperty("lines")[0].GetString());
        Assert.Single(SentChat(fake), m => m.Contains("hugs"));

        var dup = await h.Api.PostAsJsonAsync("/v1/custom-commands", new { name = "!other", aliases = new[] { "!hug" }, responses = new[] { "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        var imported = await (await h.Api.PostAsJsonAsync("/v1/custom-commands/import", new { text = "!discord join us at discord.gg/x\nnot a command\n!hug again" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, imported.GetProperty("added").GetInt32());
        Assert.Equal(2, imported.GetProperty("skipped").GetArrayLength());

        // Editing the action elsewhere (the builder) marks the definition advanced.
        var action = h.Get<ConfigService>().GetAction(def.ActionId!.Value)!;
        action.SubActions.Add(Sa(123, new { variableName = "x", value = "1" }));
        h.Get<ConfigService>().SaveAction(action);
        Assert.True(svc.Get(id)!.Advanced);
    }

    [Fact]
    public async Task Custom_command_cost_is_charged_and_checked()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        var svc = h.Get<CustomCommandService>();
        var def = svc.Save(new CustomCommandDef { Name = "!vip", Cost = 50, Responses = ["{user} has {points} {currency} left."] });
        var args = new Dictionary<string, object?> { ["user"] = "Poor", ["userId"] = "9001" };
        Assert.Equal(["Poor, !vip costs 50 points."], await svc.RespondAsync(def, args, CancellationToken.None));
        h.Get<CherBotDb>().SetPoints("twitch", "9001", "Poor", 80);
        Assert.Equal(["Poor has 30 points left."], await svc.RespondAsync(def, args, CancellationToken.None));
    }

    // ── helper endpoints ───────────────────────────────────────────────────

    [Fact]
    public async Task Helper_endpoints_duplicate_test_and_store_ui_settings()
    {
        await using var h = Host(new FakeTwitchHandler());
        var config = h.Get<ConfigService>();
        var original = new ActionDef
        {
            Name = "greet", Triggers = [Trigger("TwitchFollow")],
            SubActions = [Sa(123, new { variableName = "greeting", value = "hi %user%" }, Sa(123, new { variableName = "inner", value = "1" }))],
        };
        config.SaveAction(original);

        var copy = await (await h.Api.PostAsync($"/v1/actions/{original.Id}/duplicate", null)).Content.ReadFromJsonAsync<JsonElement>();
        var copyId = copy.GetProperty("id").GetGuid();
        var copied = config.GetAction(copyId)!;
        Assert.Equal(("greet (copy)", false), (copied.Name, copied.Enabled));
        Assert.NotEqual(original.Triggers[0].Id, copied.Triggers[0].Id);
        Assert.NotEqual(original.SubActions[0].Id, copied.SubActions[0].Id);

        h.Get<EventBus>().Publish(BotEvent.FromTypeName("TwitchFollow", new Dictionary<string, object?> { ["user"] = "SampleFan" }));
        await WaitUntil(() => h.Get<CherBotDb>().GetLatestEvent("Twitch", "Follow") is not null);
        var test = await (await h.Api.PostAsJsonAsync($"/v1/actions/{original.Id}/test", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("latest Twitch.Follow", test.GetProperty("sample").GetString());
        Assert.Equal("hi SampleFan", test.GetProperty("result").GetProperty("args").GetProperty("greeting").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await h.Api.PutAsJsonAsync("/v1/ui/settings", new { pinned = new[] { original.Id } })).StatusCode);
        Assert.Equal(original.Id, (await h.Api.GetFromJsonAsync<JsonElement>("/v1/ui/settings")).GetProperty("pinned")[0].GetGuid());

        Assert.Equal(HttpStatusCode.NoContent, (await h.Api.PutAsJsonAsync("/v1/users/twitch/123/globals/wins", new { value = 5, userLogin = "fan" })).StatusCode);
        Assert.Equal(5L, Convert.ToInt64(h.Get<VariableService>().GetUser("twitch", "123", "wins")));
        Assert.Equal(HttpStatusCode.NoContent, (await h.Api.DeleteAsync("/v1/users/twitch/123/globals/wins")).StatusCode);

        var games = await h.Api.GetFromJsonAsync<JsonElement>("/v1/games/settings");
        Assert.Equal(45, games.GetProperty("gamble").GetProperty("winChancePercent").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await h.Api.GetAsync("/v1/obs/scenes")).StatusCode);
    }
}
