using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Host.Sharing;
using CherBot.Host;
using CherBot.Integrations.Community;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.CustomCommands;
using CherBot.Integrations.Songs;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CherBot.Tests;

public sealed class LiveCommunityTests
{
    private static TestHost Host(FakeTwitchHandler? fake = null)
    {
        fake ??= new FakeTwitchHandler();
        var h = new TestHost(configure: b => b.Services.AddHttpClient("twitch").ConfigurePrimaryHttpMessageHandler(() => fake),
            extraArgs: ["--CherBot:Twitch:ClientId", "test-client", "--CherBot:Twitch:AutoConnect", "false"]);
        h.Get<TwitchAuth>().Save(TwitchRole.Broadcaster, new("token", "refresh", DateTimeOffset.UtcNow.AddHours(2), "streamer", "1001", "Streamer", ["clips:edit", "user:write:chat"]));
        h.Get<LoyaltyService>().Save(new() { Enabled = false });
        return h;
    }
    private static BotEvent Chat(string id, string text, string messageId) => new("Twitch", "ChatMessage", new Dictionary<string, object?>
    { ["userId"] = id, ["user"] = id, ["userName"] = id, ["message"] = text, ["msgId"] = messageId });

    [Fact]
    public async Task First_claim_is_atomic_and_survives_duplicate_online_and_restart()
    {
        await using var h = Host(); var db = h.Get<CherBotDb>();
        db.StartStream("1001", "stream-a", "2026-11-01T12:00:00Z");
        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => db.ClaimFirst("1001", "stream-a", i.ToString(), "User " + i, 100))));
        Assert.Single(claims.Where(won => won)); Assert.Equal(100, db.GetLeaderboard("twitch", 100).Sum(p => p.Points));
        Assert.False(db.StartStream("1001", "stream-a", "2026-11-01T12:00:00Z"));
        var reopened = new CherBotDb(db.Path);
        Assert.False(reopened.ClaimFirst("1001", "stream-a", "new", "New", 100));
        db.ResetFirstWords(); Assert.False(db.ClaimFirst("1001", "stream-a", "another", "Another", 100));
        db.EndStream("1001"); Assert.False(db.ClaimFirst("1001", "stream-a", "offline", "Offline", 100));
        db.StartStream("1001", "stream-b", "2026-11-02T12:00:00Z"); Assert.True(db.ClaimFirst("1001", "stream-b", "next", "Next", 0));
    }

    [Fact]
    public async Task Version_two_database_upgrades_without_losing_existing_balances()
    {
        await using var h = Host(); var db = h.Get<CherBotDb>(); db.SetPoints("twitch", "legacy", "Legacy user", 50);
        db.Execute("DROP TABLE stream_sessions; DROP TABLE user_activity; DROP TABLE user_history; DROP TABLE point_transactions; DELETE FROM schema_version WHERE version=3;");
        var upgraded = new CherBotDb(db.Path);
        Assert.Equal(3, upgraded.SchemaVersion); Assert.Equal(50, upgraded.GetPoints("twitch", "legacy")); Assert.Null(upgraded.GetKv("tracking.settings"));
    }

    [Fact]
    public async Task Reconciliation_preserves_identity_and_first_setup_preserves_legacy_ranking()
    {
        var fake = new FakeTwitchHandler { StreamId = "live-id" }; await using var h = Host(fake);
        var sessions = h.Get<StreamSessionService>(); await sessions.ReconcileAsync();
        Assert.Equal("live-id", sessions.Current!.StreamId);
        var db = h.Get<CherBotDb>(); Assert.True(db.ClaimFirst("1001", "live-id", "viewer", "Viewer", 100));
        await sessions.ReconcileAsync(); Assert.Equal("viewer", sessions.Current!.WinnerId);
        var setup = h.Get<QuickSetupService>(); Assert.Throws<InvalidOperationException>(() => setup.Apply("first", true, 100, false));
        setup.Apply("first", true, 100, true); setup.Apply("first", true, 100, false);
        var cfg = h.Get<ConfigService>(); Assert.Single(cfg.Commands.Where(c => c.Aliases().Contains("!first")));
        Assert.Contains(cfg.Commands, c => c.Aliases().Contains("!firstwords")); Assert.Contains(cfg.Commands, c => c.Aliases().Contains("!pinned"));
        var first = cfg.Commands.Single(c => c.Name == "!first"); var action = Seeding.ActionFor(cfg, first.Id)!; action.Name = "Edited in builder"; cfg.SaveAction(action);
        Assert.Throws<InvalidOperationException>(() => setup.Apply("first", true, 100, false));
        fake.StreamId = null; await sessions.ReconcileAsync(); Assert.False(sessions.Live);
    }

    [Fact]
    public async Task First_command_ignores_broadcaster_bot_and_filtered_messages()
    {
        var fake = new FakeTwitchHandler { StreamId = "command-stream" }; await using var h = Host(fake);
        await h.Get<StreamSessionService>().ReconcileAsync(); h.Get<QuickSetupService>().Apply("first", true, 100, true);
        h.Get<TwitchAuth>().Save(TwitchRole.Bot, new("bot-token", null, DateTimeOffset.UtcNow.AddHours(1), "bot", "2002", "Bot", ["user:write:chat"]));
        var twitch = h.Get<TwitchService>(); twitch.Publish(Chat("1001", "!first", "broadcaster"));
        var bot = Chat("2002", "!first", "bot"); twitch.Publish(bot); Assert.True((bool)bot.Data["isBot"]!);
        var filtered = Chat("bad", "!first", "filtered"); filtered.Data["filtered"] = true; twitch.Publish(filtered);
        twitch.Publish(Chat("viewer", "!first", "winner"));
        await TestEngine.WaitUntil(() => h.Get<StreamSessionService>().Current?.WinnerId == "viewer", 10000);
        Assert.Equal(100, h.Get<CherBotDb>().GetPoints("twitch", "viewer"));
        Assert.Equal(0, h.Get<CherBotDb>().GetPoints("twitch", "2002"));
    }

    [Fact]
    public async Task Live_tracking_is_bounded_and_off_by_default()
    {
        await using var h = Host(); var tracking = h.Get<UserTrackingService>();
        Assert.False(tracking.Settings.Enabled); Assert.False(tracking.Settings.ChatArchive);
        for (var i = 0; i < 620; i++) tracking.Observe(Chat("u", "hello @streamer", "m" + i));
        tracking.Observe(Chat("u", "duplicate", "m619"));
        Assert.Equal(500, tracking.Messages().Count); Assert.Equal(620, Assert.Single(tracking.Users()).Messages);
        Assert.Empty(h.Get<CherBotDb>().UserHistory("1001", "u", null, null, null, null, null, 100, 0));
        var result = await h.Api.GetFromJsonAsync<JsonElement>("/v1/users/u"); Assert.Equal(50, result.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Extended_history_separates_chat_text_and_excludes_transfers_admin_and_games_from_earned_rankings()
    {
        await using var h = Host(); var db = h.Get<CherBotDb>(); var tracking = h.Get<UserTrackingService>();
        db.StartStream("1001", "session", "2026-11-01T12:00:00Z"); tracking.Save(new() { Enabled = true });
        tracking.Observe(Chat("u", "private words", "one")); tracking.Observe(Chat("u", "private words", "one"));
        Assert.DoesNotContain(db.UserHistory("1001", "u", null, null, null, null, null, 100, 0), r => r.Detail.Contains("private words"));
        tracking.Save(new() { Enabled = true, ChatArchive = true }); tracking.Observe(Chat("u", "archive this", "two"));
        Assert.Contains(db.UserHistory("1001", "u", null, null, null, null, null, 100, 0), r => r.Detail == "archive this");
        db.AddPoints("twitch", "u", "User", 10, "chat"); db.AddPoints("twitch", "u", "User", 1000, "game"); db.SetPoints("twitch", "u", "User", 5000);
        Assert.True(db.TransferPoints("twitch", "u", "User", "other", "Other", 20));
        Assert.Equal(10, Assert.Single(db.EarnedLeaderboard("1001", null, "2000", 100)).Points);
        Assert.Equal(4980, db.GetPoints("twitch", "u"));
        var before = db.UserHistory("1001", "u", null, null, null, null, null, 100, 0).Count;
        tracking.Save(new() { Enabled = false }); db.AddPoints("twitch", "u", "User", 5, "chat"); Assert.Equal(before, db.UserHistory("1001", "u", null, null, null, null, null, 100, 0).Count);
    }

    [Fact]
    public async Task Retention_and_user_purge_remove_structured_copies_without_changing_balance()
    {
        await using var h = Host(); var db = h.Get<CherBotDb>(); var tracking = h.Get<UserTrackingService>(); tracking.Save(new() { Enabled = true, ChatArchive = true });
        tracking.Observe(Chat("u", "old text", "one")); db.AddPoints("twitch", "u", "User", 50, "watch");
        db.Execute("UPDATE user_history SET ts='2000-01-01T00:00:00Z'"); db.PruneTracking(90, 30);
        Assert.DoesNotContain(db.UserHistory("1001", "u", null, null, null, null, null, 100, 0), r => r.Detail == "old text");
        db.AddEvent(DateTimeOffset.UtcNow.ToString("O"), "Twitch", "ChatMessage", "{\"userId\":\"u\",\"message\":\"diagnostic\"}");
        db.AddEvent(DateTimeOffset.UtcNow.ToString("O"), "Twitch", "ChatMessage", "{\"userId\":\"other\",\"message\":\"keep\"}");
        db.PurgeUserHistory("1001", "u", true); Assert.Empty(db.UserHistory("1001", "u", null, null, null, null, null, 100, 0));
        Assert.Equal(50, db.GetPoints("twitch", "u")); Assert.Single(db.GetEventsSince(0, 100));
    }

    [Fact]
    public async Task Clip_service_waits_for_confirmation_and_rejects_offline_or_missing_scope()
    {
        var fake = new FakeTwitchHandler { StreamId = "live", ClipPendingPolls = 1 }; await using var h = Host(fake);
        var result = await h.Get<ClipService>().CreateAsync(); Assert.Equal("https://clips.twitch.tv/TestClip", result.Url);
        Assert.Equal(2, fake.Calls.Count(c => c.Url.Contains("/clips?") && c.Method == "GET"));
        fake.StreamId = null; await Assert.ThrowsAsync<InvalidOperationException>(() => h.Get<ClipService>().CreateAsync());
        h.Get<TwitchAuth>().Save(TwitchRole.Broadcaster, new("token", null, DateTimeOffset.UtcNow.AddHours(2), "streamer", "1001", "Streamer", []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Get<ClipService>().CreateAsync());
    }

    [Fact]
    public async Task Pack_export_contains_dependencies_and_apply_is_disabled_and_idempotent()
    {
        await using var source = Host(); await using var target = Host();
        var cfg = source.Get<ConfigService>(); var pair = Seeding.CommandAction(cfg, "Test", "!shared", "!shared", [Seeding.Step(10, new { text = "Hello" })]);
        var packs = source.Get<FeaturePackService>(); var pack = packs.Export(new([pair.Action!.Id], null, null, "Example", null, null, null, null));
        Assert.Single(pack.Actions); Assert.Single(pack.Commands);
        var targetPacks = target.Get<FeaturePackService>(); Assert.True(targetPacks.Preview(new(pack)).CanApply);
        targetPacks.Apply(new(pack)); Assert.False(target.Get<ConfigService>().GetCommand(pair.Command.Id)!.Enabled);
        Assert.True(targetPacks.Preview(new(pack)).AlreadyInstalled); targetPacks.Apply(new(pack));
        Assert.Single(target.Get<ConfigService>().Commands.Where(c => c.Name == "!shared"));
    }

    [Fact]
    public async Task Pack_conflicts_and_unmapped_paths_block_apply_without_partial_writes()
    {
        await using var h = Host(); var cfg = h.Get<ConfigService>(); var packs = h.Get<FeaturePackService>();
        var original = Seeding.CommandAction(cfg, "Test", "Existing", "!collision", [Seeding.Step(10, new { text = "old" })]);
        var command = new CommandDef { Name = "Different name", Command = "!collision" }; var action = new ActionDef { Name = "New action", SubActions = [Seeding.Step(1, new { soundFile = "C:/private/sound.wav" })] };
        var pack = new FeaturePack { Actions = [action], Commands = [command] }; var preview = packs.Preview(new(pack));
        Assert.NotEmpty(preview.Conflicts); Assert.Contains(preview.Missing, m => m.Contains("soundFile"));
        Assert.Throws<ArgumentException>(() => packs.Apply(new(pack))); Assert.Null(cfg.GetAction(action.Id)); Assert.NotNull(cfg.GetCommand(original.Command.Id));
        var mapped = new PackRequest(pack, true, new() { ["soundFile:C:/private/sound.wav"] = "/media/sound.wav" }); Assert.True(packs.Preview(mapped).CanApply);
        packs.Apply(mapped); Assert.False(cfg.GetCommand(original.Command.Id)!.Enabled);
    }

    [Fact]
    public async Task Packs_reject_private_settings_and_preserve_custom_command_dependencies()
    {
        await using var h = Host(); var packs = h.Get<FeaturePackService>();
        var pack = new FeaturePack(); pack.Settings["twitch.token"] = JsonSerializer.SerializeToElement("private");
        Assert.NotEmpty(packs.Preview(new(pack)).Errors); Assert.Throws<ArgumentException>(() => packs.Apply(new(pack)));
        Assert.Throws<ArgumentException>(() => packs.Export(new([], ["songs.player.hash"], null, null, null, null, null, null)));
        var definition = h.Get<CustomCommandService>().Save(new() { Name = "!sharecustom", Responses = ["Hello {user}"] });
        var exported = packs.Export(new([definition.ActionId!.Value], null, null, "Custom command", null, null, null, null));
        Assert.Single(exported.CustomCommands); Assert.Single(exported.Commands); Assert.Single(exported.Actions);
        await using var target = Host(); target.Get<FeaturePackService>().Apply(new(exported));
        Assert.NotNull(target.Get<CustomCommandService>().Get(definition.Id));
        Assert.False(target.Get<CustomCommandService>().Get(definition.Id)!.Enabled);
    }

    [Fact]
    public async Task Pack_database_failure_rolls_back_all_records_and_receipt()
    {
        await using var h = Host(); var db = h.Get<CherBotDb>(); var cfg = h.Get<ConfigService>();
        var first = new ActionDef { Name = "Would be inserted" }; var second = new ActionDef { Name = "Reject this insert" };
        var pack = new FeaturePack { Actions = [first, second] };
        db.Execute("CREATE TRIGGER reject_pack BEFORE INSERT ON docs WHEN NEW.name='Reject this insert' BEGIN SELECT RAISE(ABORT,'test rollback'); END");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => h.Get<FeaturePackService>().Apply(new(pack)));
        Assert.Null(db.GetDoc("action", first.Id.ToString())); Assert.Null(cfg.GetAction(first.Id)); Assert.Null(db.GetKv("pack." + pack.Id));
    }

    [Fact]
    public async Task Expired_player_cannot_report_results_after_another_controller_takes_over()
    {
        await using var h = Host(); var clock = new ManualClock();
        var songs = new SongRequestService(h.Get<CherBotDb>(), h.Get<ConfigService>(), h.Get<EventBus>(), h.Get<StreamSessionService>(), clock);
        songs.Save(new() { Enabled = true, OnlyWhileLive = false, ApprovalRequired = false, CooldownSeconds = 0 });
        var request = songs.Add("u", "User", "https://youtu.be/dQw4w9WgXcQ", "lease-test");
        var first = songs.Acquire(Guid.NewGuid().ToString()); Assert.Equal(request.Id, songs.PlayerState(first.LeaseId)!.Id);
        clock.Utc = clock.Utc.AddSeconds(21);
        Assert.False(songs.PlayerConnected); Assert.Throws<InvalidOperationException>(() => songs.PlayerResult(first.LeaseId, request.Id, false, null));
        var second = songs.Acquire(Guid.NewGuid().ToString()); Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.Equal(request.Id, songs.PlayerState(second.LeaseId)!.Id);
        Assert.Throws<InvalidOperationException>(() => songs.PlayerState(first.LeaseId));
        songs.PlayerResult(second.LeaseId, request.Id, false, null); Assert.Null(songs.PlayerState(second.LeaseId));
    }

    [Fact]
    public async Task Restarted_player_waits_for_previous_controller_to_stop()
    {
        await using var h = Host(); var clock = new ManualClock();
        SongRequestService Create() => new(h.Get<CherBotDb>(), h.Get<ConfigService>(), h.Get<EventBus>(), h.Get<StreamSessionService>(), clock);
        var songs = Create(); songs.Save(new() { Enabled = true, OnlyWhileLive = false, ApprovalRequired = false, CooldownSeconds = 0 });
        songs.Add("u", "User", "https://youtu.be/dQw4w9WgXcQ", "restart-player"); var lease = songs.Acquire(Guid.NewGuid().ToString()); songs.PlayerState(lease.LeaseId);
        var restarted = Create(); Assert.Throws<InvalidOperationException>(() => restarted.Acquire(Guid.NewGuid().ToString()));
        clock.Utc = clock.Utc.AddSeconds(21); var replacement = restarted.Acquire(Guid.NewGuid().ToString()); Assert.NotNull(restarted.PlayerState(replacement.LeaseId));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    [Fact]
    public async Task Online_cli_applies_json_to_running_engine_and_reports_pack_conflicts()
    {
        await using var h = Host(); var token = Path.Combine(h.Dir, "api-token"); var body = Path.Combine(h.Dir, "cli-action.json");
        await File.WriteAllTextAsync(body, "{\"name\":\"CLI-created action\",\"enabled\":false,\"triggers\":[],\"subActions\":[]}");
        var result = await OnlineCli.RunAsync(["api", "POST", "/v1/actions", "--body-file", body, "--url", h.Api.BaseAddress!.ToString(), "--token-file", token]);
        Assert.Equal(0, result); Assert.Single(h.Get<ConfigService>().Actions.Where(a => a.Name == "CLI-created action"));
        var pack = new FeaturePack { Commands = [new() { Name = "Conflicting CLI command", Command = "!points" }] };
        var file = Path.Combine(h.Dir, "cli-pack.json"); await File.WriteAllTextAsync(file, CherBotJson.Serialize(pack));
        Assert.Equal(3, await OnlineCli.RunAsync(["pack", "preview", file, "--url", h.Api.BaseAddress!.ToString(), "--token-file", token]));
        Assert.DoesNotContain(h.Get<ConfigService>().Commands, c => c.Name == "Conflicting CLI command");
    }

    [Theory]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=3", "youtube", "dQw4w9WgXcQ")]
    [InlineData("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC?si=x", "spotify", "4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("https://music.apple.com/us/album/example/123?i=456", "apple", "456")]
    [InlineData("https://music.apple.com/us/song/example/456", "apple", "456")]
    public void Song_links_normalize_to_track_identity(string url, string provider, string id)
    { var link = SongRequestService.Normalize(url); Assert.Equal(provider, link.Provider); Assert.Equal(id, link.TrackId); Assert.DoesNotContain("si=", link.Url); }

    [Theory]
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://open.spotify.com/playlist/4uLU6hMCjMI75M1A2tKUQC")]
    [InlineData("https://music.apple.com/us/album/example/123")]
    [InlineData("file:///etc/passwd")]
    public void Unsupported_song_links_are_rejected(string url) => Assert.Throws<ArgumentException>(() => SongRequestService.Normalize(url));

    [Fact]
    public async Task Song_queue_enforces_approval_providers_duplicates_limits_and_manual_external_completion()
    {
        await using var h = Host(); var songs = h.Get<SongRequestService>();
        songs.Save(new() { Enabled = true, OnlyWhileLive = false, AppleMusic = true, Spotify = true, CooldownSeconds = 0, PerUserLimit = 1 });
        var first = songs.Add("u", "User", "https://youtu.be/dQw4w9WgXcQ", "one"); Assert.Equal("pending", first.Status); Assert.Null(songs.Advance());
        Assert.Equal(first, songs.Add("u", "User", first.Url, "one"));
        Assert.Throws<ArgumentException>(() => songs.Add("another", "Other", first.Url, "two"));
        Assert.Throws<ArgumentException>(() => songs.Add("u", "User", "https://music.apple.com/us/song/example/456", "three"));
        songs.Change(first.Id, "approve"); Assert.Equal(first.Id, songs.Advance()!.Id); songs.Change(first.Id, "complete");
        var external = songs.Add("u", "User", "https://music.apple.com/us/song/example/456", "four"); songs.Change(external.Id, "approve");
        Assert.Equal(external.Id, songs.Advance()!.Id); Assert.Equal(external.Id, songs.Advance()!.Id);
        songs.Change(external.Id, "complete"); Assert.Null(songs.Advance());
    }

    [Fact]
    public async Task Player_capability_cannot_administer_engine_and_only_one_controller_can_play()
    {
        await using var h = Host(); var songs = h.Get<SongRequestService>(); var token = songs.RotatePlayerToken();
        Assert.True(songs.AuthorizePlayer(token)); var lease = songs.Acquire(Guid.NewGuid().ToString());
        Assert.Throws<InvalidOperationException>(() => songs.Acquire(Guid.NewGuid().ToString()));
        Assert.Throws<InvalidOperationException>(() => songs.PlayerState("wrong"));
        songs.RotatePlayerToken(); Assert.False(songs.AuthorizePlayer(token));
        using var anonymous = new HttpClient { BaseAddress = h.Api.BaseAddress }; anonymous.DefaultRequestHeaders.Add("X-Song-Token", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/songs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/song-player/state?leaseId=" + lease.LeaseId)).StatusCode);
    }
}
