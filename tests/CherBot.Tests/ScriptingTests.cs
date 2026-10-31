using CherBot.Compat.Import;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Model;
using CherBot.Core.State;
using CherBot.Integrations.Twitch;
using CherBot.Scripting;
using Microsoft.Extensions.DependencyInjection;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class ScriptingTests
{
    private static SubActionDef Code(string source) => Sa(99999, new
    {
        byteCode = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source)),
        references = new[] { @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll" },
    });

    private static Task<RunResult> Run(TestHost h, Dictionary<string, object?>? args, params SubActionDef[] subs)
    {
        var a = new ActionDef { Name = "script " + Guid.NewGuid(), SubActions = [.. subs] };
        for (var i = 0; i < subs.Length; i++) subs[i].Index = i;
        h.Get<ConfigService>().SaveAction(a);
        return h.Get<ActionDispatcher>().RunNowAsync(a, args, new DispatchSource("test"), CancellationToken.None);
    }

    private static TestHost Host(FakeTwitchHandler? fake = null) =>
        new(configure: fake is null ? null : b => b.Services.AddHttpClient("twitch").ConfigurePrimaryHttpMessageHandler(() => fake),
            extraArgs: ["--CherBot:Twitch:AutoConnect", "false", "--CherBot:Twitch:ClientId", "test-client"]);

    [Fact]
    public async Task Streamerbot_style_script_without_base_class_runs_with_cph()
    {
        await using var h = Host();
        var r = await Run(h, new() { ["count"] = "41", ["user"] = "Viewer" },
            Code("""
                using System;
                using Newtonsoft.Json;

                public class CPHInline
                {
                    private int _calls;
                    public override void Init() { _calls = 100; }
                    public bool Execute()
                    {
                        _calls++;
                        CPH.TryGetArg("count", out int count);
                        CPH.TryGetArg<string>("user", out var user);
                        CPH.SetArgument("next", count + 1);
                        CPH.SetArgument("json", JsonConvert.SerializeObject(new { user, calls = _calls }));
                        CPH.SetGlobalVar("lastUser", user, true);
                        CPH.LogInfo($"{user} -> {count}");
                        return true;
                    }
                }
                """),
            SetArg("after", "%next%"));
        Assert.Equal("ok", r.Status);
        Assert.Equal(42, r.Args["next"]);
        Assert.Equal("42", r.Args["after"]);
        Assert.Equal("{\"user\":\"Viewer\",\"calls\":101}", r.Args["json"]);
        Assert.Equal("Viewer", h.Get<VariableService>().GetGlobal("lastUser"));
    }

    [Fact]
    public async Task Script_returning_false_stops_the_action_and_compile_errors_do_not()
    {
        await using var h = Host();
        var stop = await Run(h, null,
            Code("public class CPHInline { public bool Execute() => false; }"),
            SetArg("never", "x"));
        Assert.Equal("break", stop.Status);
        Assert.False(stop.Args.ContainsKey("never"));

        var broken = await Run(h, null,
            Code("public class CPHInline { public bool Execute() { this does not compile } }"),
            SetArg("after", "yes"));
        Assert.Equal("ok", broken.Status);
        Assert.Equal("yes", broken.Args["after"]);
        Assert.Contains(broken.Trace, t => t.Type == 99999 && t.Status == "error" && t.Error!.Contains("compile"));
    }

    [Fact]
    public async Task Same_source_is_compiled_once_and_keeps_instance_state()
    {
        await using var h = Host();
        const string src = "public class CPHInline { int n; public bool Execute() { n++; CPH.SetArgument(\"n\", n); return true; } }";
        var a = await Run(h, null, Code(src));
        var b = await Run(h, null, Code(src));
        Assert.Equal(1, a.Args["n"]);
        Assert.Equal(2, b.Args["n"]);
        Assert.Equal(1, h.Get<ScriptHost>().CachedCount);
    }

    [Fact]
    public async Task Execute_method_custom_triggers_and_event_type()
    {
        await using var h = Host();
        var code = Code("""
            public class CPHInline
            {
                public bool Execute() { CPH.RegisterCustomTrigger("Boss Defeated", "boss.defeated", new[] { "Games" }); return true; }
                public bool Celebrate() { CPH.SetArgument("party", "yes"); CPH.TriggerCodeEvent("boss.defeated", true); return true; }
            }
            """);
        var holder = await Run(h, null, code);
        Assert.Equal("ok", holder.Status);

        var listener = new ActionDef
        {
            Name = "on boss",
            Triggers = [Trigger("CustomCodeEvent", new { eventName = "boss.defeated" })],
            SubActions = [Code("public class CPHInline { public bool Execute() { CPH.SetArgument(\"type\", CPH.GetEventType().ToString()); return true; } }")],
        };
        h.Get<ConfigService>().SaveAction(listener);

        var caller = await Run(h, null, Sa(99998, new { executeCodeId = code.Id, method = "Celebrate" }));
        Assert.Equal("yes", caller.Args["party"]);
        var db = h.Get<CherBot.Storage.CherBotDb>();
        await WaitUntil(() => db.GetRuns(5, listener.Id.ToString()).Count == 1);
        Assert.Contains("CustomCodeEvent", db.GetRuns(5, listener.Id.ToString())[0].Json);
    }

    [Fact]
    public async Task Imported_streamerbot_script_runs_and_sends_chat()
    {
        var fake = new FakeTwitchHandler();
        await using var h = Host(fake);
        h.Get<TwitchAuth>().Save(TwitchRole.Broadcaster, new TwitchToken("b-token", "r", DateTimeOffset.UtcNow.AddHours(4), "streamer", "1001", "Streamer", []));
        h.Get<StreamerBotImporter>().Import(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "export-getcommands.txt")));
        var action = h.Get<ConfigService>().Actions.Single(a => a.Name == "Get Commands");

        var result = await h.Get<ActionDispatcher>().RunNowAsync(action, null, new DispatchSource("test"), CancellationToken.None);
        Assert.Equal("ok", result.Status);
        Assert.DoesNotContain(result.Trace, t => t.Status == "error");
        var sent = fake.Calls.Where(c => c.Url.EndsWith("/helix/chat/messages")).ToList();
        Assert.NotEmpty(sent);
        Assert.Contains("Commands Avaliable to you: ", sent[0].Body);
        Assert.Contains("!points", sent[0].Body);
    }
}
