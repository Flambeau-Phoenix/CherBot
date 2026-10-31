using CherBot.Core.Commands;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Core.Timers;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class TriggerCommandTests
{
    private static BotEvent Chat(string message, string user = "viewer1", bool mod = false, bool broadcaster = false) =>
        BotEvent.FromTypeName("TwitchChatMessage", new Dictionary<string, object?>
        {
            ["message"] = message, ["userId"] = "id-" + user, ["userName"] = user, ["user"] = user, ["platform"] = "twitch",
            ["isModerator"] = mod, ["isBroadcaster"] = broadcaster, ["msgId"] = Guid.NewGuid().ToString(),
        });

    [Fact]
    public async Task Command_start_match_triggers_action_with_input_arguments()
    {
        await using var e = new TestEngine();
        var cmd = e.Config.SaveCommand(new CommandDef { Name = "Shoutout", Command = "!so\n!shoutout" });
        var action = e.AddAction("so", x => x.Triggers.Add(Trigger("CommandTriggered", new { commandId = cmd.Id })),
            SetArg("out", "%command%:%input0%:%rawInput%:%user%"));
        var completed = e.Capture(ev => ev.Type == "ActionCompleted");

        e.Bus.Publish(Chat("!shoutout   SomeStreamer  extra"));
        await WaitUntil(() => { lock (completed) return completed.Count > 0; });
        var run = e.Get<CherBot.Storage.CherBotDb>().GetRuns(5, action.Id.ToString()).Single();
        Assert.Contains("!shoutout:SomeStreamer:SomeStreamer  extra:viewer1", run.Json);
    }

    [Fact]
    public async Task Command_locations_and_regex_mode()
    {
        await using var e = new TestEngine();
        var svc = e.Get<CommandService>();
        var exact = new CommandDef { Command = "!lurk", Location = 1 };
        var anywhere = new CommandDef { Command = "pog", Location = 2 };
        var start = new CommandDef { Command = "!dice" };
        var regex = new CommandDef { Mode = 1, Command = @"^!roll (?<sides>\d+)$" };

        Assert.NotNull(svc.TryMatch(exact, "!LURK"));
        Assert.Null(svc.TryMatch(exact, "!lurk now"));
        Assert.NotNull(svc.TryMatch(anywhere, "that was pog!"));
        Assert.Null(svc.TryMatch(start, "!dicey"));
        var m = svc.TryMatch(regex, "!roll 20");
        Assert.Equal("20", m!["sides"]);
        Assert.Equal(2, m["match.count"]);
    }

    [Fact]
    public async Task Cooldowns_raise_command_cooldown_and_broadcaster_is_exempt()
    {
        await using var e = new TestEngine();
        e.Config.SaveCommand(new CommandDef { Name = "cd", Command = "!cd", GlobalCooldown = 60 });
        var svc = e.Get<CommandService>();
        Assert.Equal("Triggered", svc.Process(Chat("!cd", "a")).Single().Type);
        Assert.Equal("Cooldown", svc.Process(Chat("!cd", "b")).Single().Type);
        Assert.Equal("Triggered", svc.Process(Chat("!cd", "owner", broadcaster: true)).Single().Type);
    }

    [Fact]
    public async Task Grant_type_allow_and_deny_with_groups()
    {
        await using var e = new TestEngine();
        var db = e.Get<CherBot.Storage.CherBotDb>();
        db.AddGroupUser("trusted", "twitch", "id-friend", "friend", "Friend");
        e.Config.SaveCommand(new CommandDef { Name = "allow", Command = "!allow", GrantType = 0, PermittedGroups = ["trusted"] });
        e.Config.SaveCommand(new CommandDef { Name = "deny", Command = "!deny", GrantType = 1, PermittedGroups = ["trusted"] });
        e.Config.SaveCommand(new CommandDef { Name = "denyAll", Command = "!nobody", GrantType = 1 });
        var svc = e.Get<CommandService>();

        Assert.Single(svc.Process(Chat("!allow", "friend")));
        Assert.Empty(svc.Process(Chat("!allow", "stranger")));
        Assert.Empty(svc.Process(Chat("!deny", "friend")));
        Assert.Single(svc.Process(Chat("!deny", "stranger")));
        Assert.Empty(svc.Process(Chat("!nobody", "stranger")));
    }

    [Fact]
    public async Task Min_role_extension_gates_commands()
    {
        await using var e = new TestEngine();
        var cmd = new CommandDef { Name = "mods", Command = "!modonly" };
        cmd.Set("minRole", "moderator");
        e.Config.SaveCommand(cmd);
        var svc = e.Get<CommandService>();
        Assert.Empty(svc.Process(Chat("!modonly")));
        Assert.Single(svc.Process(Chat("!modonly", mod: true)));
    }

    [Fact]
    public void Trigger_filters_identity_and_amount_ranges()
    {
        var reward = Guid.NewGuid().ToString();
        var t = Trigger("TwitchRewardRedemption", new { rewardId = reward });
        Assert.True(TriggerMatcher.Accepts(t, BotEvent.FromTypeName("TwitchRewardRedemption", new Dictionary<string, object?> { ["rewardId"] = reward })));
        Assert.False(TriggerMatcher.Accepts(t, BotEvent.FromTypeName("TwitchRewardRedemption", new Dictionary<string, object?> { ["rewardId"] = "other" })));

        var cheer = Trigger("TwitchCheer", new { min = 100, max = 500 });
        Assert.False(TriggerMatcher.Accepts(cheer, BotEvent.FromTypeName("TwitchCheer", new Dictionary<string, object?> { ["bits"] = 50 })));
        Assert.True(TriggerMatcher.Accepts(cheer, BotEvent.FromTypeName("TwitchCheer", new Dictionary<string, object?> { ["bits"] = 100 })));
        Assert.False(TriggerMatcher.Accepts(cheer, BotEvent.FromTypeName("TwitchCheer", new Dictionary<string, object?> { ["bits"] = 501 })));
    }

    [Fact]
    public async Task Custom_event_trigger_sub_action_starts_matching_actions()
    {
        await using var e = new TestEngine();
        var listener = e.AddAction("listener", x => x.Triggers.Add(Trigger("CustomEvent", new { eventName = "hype" })), SetArg("heard", "%eventName%"));
        e.AddAction("other", x => x.Triggers.Add(Trigger("CustomEvent", new { eventName = "nope" })), SetArg("heard", "wrong"));
        var completed = e.Capture(ev => ev.Type == "ActionCompleted");
        await e.Run(e.AddAction("emitter", Sa(1034, new { eventName = "hype" })));
        await WaitUntil(() => { lock (completed) return completed.Any(c => (string?)c.Data["actionName"] == "listener"); });
        await Task.Delay(150);
        lock (completed) Assert.DoesNotContain(completed, c => (string?)c.Data["actionName"] == "other");
    }

    [Fact]
    public async Task Timer_fires_timed_action_trigger_and_respects_repeat()
    {
        await using var e = new TestEngine();
        var timer = e.Config.SaveTimer(new TimerDef { Name = "promo", Interval = 1, Repeat = false });
        var fired = e.Capture(ev => ev.Type == "TimedAction");
        var svc = e.Get<TimerService>();
        var now = DateTimeOffset.UtcNow;
        svc.Tick(now);
        svc.Tick(now.AddSeconds(2));
        svc.Tick(now.AddSeconds(4));
        await WaitUntil(() => { lock (fired) return fired.Count >= 1; });
        await Task.Delay(100);
        lock (fired)
        {
            Assert.Single(fired);
            Assert.Equal(timer.Id.ToString(), fired[0].Data["timerId"]);
        }
        Assert.False(e.Config.GetTimer(timer.Id)!.Enabled);
    }
}
