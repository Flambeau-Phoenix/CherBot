using System.Diagnostics;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Core.State;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class EngineTests
{
    [Fact]
    public async Task Sequential_sub_actions_run_in_index_order_and_expand_templates()
    {
        await using var e = new TestEngine();
        var a = e.AddAction("seq",
            SetArg("first", "hello"),
            SetArg("second", "%first% world"),
            SetArg("third", "%second%! %missing%"));
        var r = await e.Run(a);
        Assert.Equal("ok", r.Status);
        Assert.Equal("hello world! %missing%", r.Args["third"]);
    }

    [Fact]
    public async Task Persisted_globals_expand_with_tilde_syntax()
    {
        await using var e = new TestEngine();
        e.Get<VariableService>().SetGlobal("streamTitle", "Speedrun", persisted: true);
        var r = await e.Run(e.AddAction("g", SetArg("x", "now: ~streamTitle~")));
        Assert.Equal("now: Speedrun", r.Args["x"]);
    }

    [Theory]
    [InlineData("5", 0, "5", true)]          // Equals, auto-typed
    [InlineData("5", 0, "5.0", true)]        // numeric equality
    [InlineData("abc", 1, "abd", true)]      // Not Equals
    [InlineData("hello world", 2, "lo w", true)] // Contains
    [InlineData("15", 4, "2", false)]        // Less Than is numeric, not lexicographic
    [InlineData("15", 5, "2", true)]         // Greater Than
    [InlineData("HeLLo", 7, "hello", true)]  // Equals (Ignore Case)
    [InlineData("HeLLo", 8, "hello", false)] // Not Equals (Ignore Case)
    [InlineData("", 9, "", true)]            // Is Null or Empty
    public async Task If_operators_follow_streamerbot_numbering(string input, int op, string value, bool expected)
    {
        await using var e = new TestEngine();
        var a = e.AddAction("if",
            SetArg("in", input),
            If("%in%", op, value, [SetArg("branch", "true")], [SetArg("branch", "false")]));
        var r = await e.Run(a);
        Assert.Equal(expected ? "true" : "false", r.Args["branch"]);
    }

    [Fact]
    public async Task If_does_not_exist_and_regex_match_set_arguments()
    {
        await using var e = new TestEngine();
        var a = e.AddAction("if2",
            If("nothingHere", 6, "", [SetArg("missing", "yes")]),
            SetArg("msg", "!give 50 coins"),
            If("%msg%", 3, @"!give (?<amount>\d+) (\w+)", [SetArg("matched", "%match.count%|%match[1]%|%amount%")]));
        var r = await e.Run(a);
        Assert.Equal("yes", r.Args["missing"]);
        Assert.Equal("3|coins|50", r.Args["matched"]);
    }

    [Fact]
    public async Task Unresolved_placeholder_counts_as_null_or_empty()
    {
        await using var e = new TestEngine();
        var r = await e.Run(e.AddAction("n", If("%notSet%", 9, "", [SetArg("empty", "yes")], [SetArg("empty", "no")])));
        Assert.Equal("yes", r.Args["empty"]);
    }

    [Fact]
    public async Task Switch_runs_first_matching_case_or_default()
    {
        await using var e = new TestEngine();
        SubActionDef Switch(string input) => Sa(127, new { input, autoType = true },
            Sa(99903, new { values = new[] { "a", "b" } }, SetArg("hit", "ab")),
            Sa(99903, new { values = new[] { "3" } }, SetArg("hit", "three")),
            Sa(99904, null, SetArg("hit", "default")));

        Assert.Equal("ab", (await e.Run(e.AddAction("s1", Switch("B")))).Args["hit"]);
        Assert.Equal("three", (await e.Run(e.AddAction("s2", Switch("3.0")))).Args["hit"]);
        Assert.Equal("default", (await e.Run(e.AddAction("s3", Switch("zzz")))).Args["hit"]);
    }

    [Fact]
    public async Task While_loops_with_continue_and_break()
    {
        await using var e = new TestEngine();
        var a = e.AddAction("loop",
            Sa(122, new { variableName = "i", value = "0", persisted = false, destination = 0, source = 0, autoType = true }),
            Sa(128, new { input = "~i~", operation = 4, value = "100", autoType = true },
                Sa(122, new { variableName = "i", value = "1", persisted = true, destination = 0, source = 1 }),
                Sa(121, new { variableName = "i", persisted = true, destinationVariable = "i" }),
                If("%i%", 0, "2", [Sa(130)]),
                SetArg("last", "%i%"),
                If("%i%", 0, "5", [Sa(129)])));
        e.Get<VariableService>().SetGlobal("i", 0L, persisted: true);
        var r = await e.Run(a);
        Assert.Equal("5", r.Args["last"]);
        Assert.Equal(5L, e.Get<VariableService>().GetGlobal("i"));
    }

    [Fact]
    public async Task Break_stops_the_action()
    {
        await using var e = new TestEngine();
        var r = await e.Run(e.AddAction("b", SetArg("a", "1"), Sa(124), SetArg("b", "2")));
        Assert.Equal("break", r.Status);
        Assert.True(r.Args.ContainsKey("a"));
        Assert.False(r.Args.ContainsKey("b"));
    }

    [Fact]
    public async Task Random_action_picks_by_weight()
    {
        await using var e = new TestEngine();
        var a = e.AddAction("rnd", x => x.RandomAction = true,
            Sa(123, new { variableName = "pick", value = "never", weight = 0.0 }),
            Sa(123, new { variableName = "pick", value = "always", weight = 5.0 }));
        for (var i = 0; i < 20; i++)
            Assert.Equal("always", (await e.Run(a)).Args["pick"]);
    }

    [Fact]
    public async Task Concurrent_action_starts_all_sub_actions_together()
    {
        await using var e = new TestEngine();
        // Sequential would take at least 4500 ms; the margin absorbs thread-pool load from parallel tests and slow CI runners.
        var a = e.AddAction("conc", x => x.Concurrent = true, Delay(1500), Delay(1500), Delay(1500));
        var sw = Stopwatch.StartNew();
        await e.Run(a);
        Assert.True(sw.ElapsedMilliseconds < 4000, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Disabled_sub_actions_are_skipped_and_unsupported_types_do_not_fail_the_run()
    {
        await using var e = new TestEngine();
        var r = await e.Run(e.AddAction("skip",
            Sa(123, new { variableName = "x", value = "1", enabled = false }),
            Sa(4242),
            SetArg("y", "2")));
        Assert.Equal("ok", r.Status);
        Assert.False(r.Args.ContainsKey("x"));
        Assert.Contains(r.Trace, t => t.Type == 4242 && t.Status == "unsupported_on_host");
    }

    [Fact]
    public async Task Run_action_immediately_returns_variables_to_caller()
    {
        await using var e = new TestEngine();
        var inner = e.AddAction("inner", SetArg("fromInner", "%seed%-done"));
        var outer = e.AddAction("outer", SetArg("seed", "abc"), Sa(4, new { actionId = inner.Id, runImmedately = true }));
        var r = await e.Run(outer);
        Assert.Equal("abc-done", r.Args["fromInner"]);
    }

    [Fact]
    public async Task Flat_parentId_lists_are_nested_on_save()
    {
        await using var e = new TestEngine();
        var ifSa = new SubActionDef { Type = 120, Index = 0 };
        ifSa.Set("input", "1"); ifSa.Set("operation", 0); ifSa.Set("value", "1");
        var t = new SubActionDef { Type = 99901, ParentId = ifSa.Id, Index = 0 };
        var child = new SubActionDef { Type = 123, ParentId = t.Id, Index = 0 };
        child.Set("variableName", "nested"); child.Set("value", "ok");
        var a = e.Config.SaveAction(new ActionDef { Name = "flat", SubActions = [ifSa, t, child] });
        Assert.Single(a.SubActions);
        Assert.Equal("ok", (await e.Run(a)).Args["nested"]);
    }

    [Fact]
    public async Task Signals_deliver_arguments_and_time_out()
    {
        await using var e = new TestEngine();
        var waiter = e.AddAction("waiter", Sa(1032, new { signalName = "go", timeout = "3000", overwrite = true }));
        var sender = e.AddAction("sender", SetArg("payload", "42"), Sa(1031, new { signalName = "go", useArge = true }));

        var waiting = e.Run(waiter);
        await Task.Delay(100);
        await e.Run(sender);
        var r = await waiting;
        Assert.Equal(false, r.Args["signalTimeout"]);
        Assert.Equal("42", r.Args["payload"]);

        var lonely = await e.Run(e.AddAction("lonely", Sa(1032, new { signalName = "never", timeout = "200" })));
        Assert.Equal(true, lonely.Args["signalTimeout"]);
    }

    [Fact]
    public async Task Global_set_increment_and_user_globals_raise_events()
    {
        await using var e = new TestEngine();
        var events = e.Capture(ev => ev.Source == "Misc");
        var a = e.AddAction("vars",
            Sa(122, new { variableName = "count", value = "", persisted = true, destination = 0, source = 1 }),
            Sa(122, new { variableName = "count", value = "4", persisted = true, destination = 0, source = 1 }),
            Sa(122, new { variableName = "pts", value = "10", persisted = true, destination = 1, source = 1 }),
            Sa(121, new { variableName = "pts", persisted = true, source = 1, destinationVariable = "myPts" }));
        var r = await e.Run(a, new() { ["userId"] = "u1", ["userName"] = "viewer1", ["userType"] = "twitch" });
        Assert.Equal(5L, e.Get<VariableService>().GetGlobal("count"));
        Assert.Equal(10L, r.Args["myPts"]);
        await WaitUntil(() => { lock (events) return events.Any(x => x.Type == "UserGlobalVariableUpdated"); });
        lock (events)
        {
            Assert.Contains(events, x => x.Type == "GlobalVariableCreated");
            Assert.Contains(events, x => x.Type == "GlobalVariableUpdated");
        }
    }

    [Fact]
    public async Task Persisted_globals_survive_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cherbot-tests", Guid.NewGuid().ToString("N"));
        await using (var e1 = new TestEngine(dir: dir))
        {
            e1.Get<VariableService>().SetGlobal("keep", "me", true);
            e1.Get<VariableService>().SetGlobal("temp", "gone", false);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var e2 = new TestEngine(dir: dir);
            Assert.Equal("me", e2.Get<VariableService>().GetGlobal("keep"));
            Assert.Null(e2.Get<VariableService>().GetGlobal("temp", false));
        }
    }
}
