using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Integrations.Obs;
using CherBot.Integrations.SpeakerBot;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class ObsSpeakerBotTests
{
    private static TestHost ObsHost(FakeObsServer obs) => new(extraArgs:
    [
        "--CherBot:Twitch:AutoConnect", "false",
        "--CherBot:Obs:Connections:0:Name", "Main OBS",
        "--CherBot:Obs:Connections:0:Host", "127.0.0.1",
        "--CherBot:Obs:Connections:0:Port", obs.Port.ToString(),
        "--CherBot:Obs:Connections:0:Password", obs.Password ?? "",
        "--CherBot:Obs:Connections:0:RetrySeconds", "2",
    ]);

    private static Task<RunResult> Run(TestHost h, params SubActionDef[] subs)
    {
        var a = new ActionDef { Name = "obs " + Guid.NewGuid(), SubActions = [.. subs] };
        for (var i = 0; i < subs.Length; i++) subs[i].Index = i;
        h.Get<ConfigService>().SaveAction(a);
        return h.Get<ActionDispatcher>().RunNowAsync(a, null, new DispatchSource("test"), CancellationToken.None);
    }

    [Fact]
    public async Task Obs_connects_with_password_and_runs_sub_actions()
    {
        await using var obs = new FakeObsServer("hunter2");
        await using var h = ObsHost(obs);
        var events = new List<BotEvent>();
        h.Get<EventBus>().Subscribe("obs-test", e => { lock (events) events.Add(e); return Task.CompletedTask; }, e => e.Source == "Obs");
        await WaitUntil(() => h.Get<ObsService>().Client(null) is not null, 30_000);
        // Under load the connection can complete before the subscription above, so also accept the stored event.
        await WaitUntil(() =>
        {
            lock (events)
                if (events.Any(e => e.Type == "Connected")) return true;
            return h.Get<CherBot.Storage.CherBotDb>().GetLatestEvent("Obs", "Connected") is not null;
        }, 15_000);

        // Unknown connection ids (from imported Streamer.bot actions) fall back to the connected instance.
        var someOtherId = Guid.NewGuid();
        var result = await Run(h,
            Sa(34, new { sceneName = "Gameplay", connectionId = someOtherId }),
            Sa(30, new { sceneName = "Gameplay", sourceName = "Webcam", state = 2 }),
            Sa(35, new { sourceName = "Webcam", filterName = "Blur", state = 1 }),
            Sa(39, new { sourceName = "Ticker", text = "Hello %who%" }),
            Sa(33, new { state = 0 }),
            Sa(43),
            Sa(46, new { name = "GetStats", raw = "{}", resultsToArgs = true, prefix = "stats" }));
        Assert.Equal("ok", result.Status);
        Assert.DoesNotContain(result.Trace, t => t.Status == "error");
        Assert.Equal("Main", result.Args["currentScene"]);

        var reqs = obs.Requests.ToList();
        Assert.Contains(reqs, r => r.Type == "SetCurrentProgramScene" && r.Data.GetProperty("sceneName").GetString() == "Gameplay");
        // Toggle reads the current state (enabled) and turns it off.
        Assert.Contains(reqs, r => r.Type == "SetSceneItemEnabled" && r.Data.GetProperty("sceneItemId").GetInt32() == 7 && !r.Data.GetProperty("sceneItemEnabled").GetBoolean());
        Assert.Contains(reqs, r => r.Type == "SetSourceFilterEnabled" && !r.Data.GetProperty("filterEnabled").GetBoolean());
        Assert.Contains(reqs, r => r.Type == "SetInputSettings" && r.Data.GetProperty("inputSettings").GetProperty("text").GetString() == "Hello %who%");
        Assert.Contains(reqs, r => r.Type == "StartStream");
        Assert.Contains(reqs, r => r.Type == "GetStats");

        await obs.EmitAsync("CurrentProgramSceneChanged", new { sceneName = "BRB" });
        await WaitUntil(() => { lock (events) return events.Any(e => e.Type == "SceneChanged" && (string?)e.Data["sceneName"] == "BRB"); });
    }

    [Fact]
    public async Task Obs_scene_changed_trigger_filters_by_scene()
    {
        await using var obs = new FakeObsServer(null);
        await using var h = ObsHost(obs);
        await WaitUntil(() => h.Get<ObsService>().Client(null) is not null, 10_000);
        var a = new ActionDef { Name = "on brb", Triggers = [Trigger("ObsSceneChanged", new { sceneName = "BRB" })], SubActions = [SetArg("x", "1")] };
        h.Get<ConfigService>().SaveAction(a);
        await obs.EmitAsync("CurrentProgramSceneChanged", new { sceneName = "Gameplay" });
        await obs.EmitAsync("CurrentProgramSceneChanged", new { sceneName = "BRB" });
        var db = h.Get<CherBot.Storage.CherBotDb>();
        await WaitUntil(() => db.GetRuns(10, a.Id.ToString()).Count == 1);
        await Task.Delay(300);
        Assert.Single(db.GetRuns(10, a.Id.ToString()));
    }

    [Fact]
    public void Color_conversion_matches_obs_abgr()
    {
        Assert.Equal(0xFF0000FFu, ObsHandlers.ParseColor("#FF0000"));      // red → ABGR
        Assert.Equal(0x80FF0000u, ObsHandlers.ParseColor("#800000FF"));    // half-transparent blue
    }

    [Fact]
    public async Task SpeakerBot_speaks_over_websocket_and_profiles_over_udp()
    {
        await using var sb = new FakeSpeakerBot();
        await using var h = new TestHost(extraArgs:
        [
            "--CherBot:Twitch:AutoConnect", "false",
            "--CherBot:SpeakerBot:Enabled", "true",
            "--CherBot:SpeakerBot:Port", sb.Port.ToString(),
            "--CherBot:SpeakerBot:UdpPort", sb.UdpPort.ToString(),
        ]);
        await WaitUntil(() => h.Get<SpeakerBotService>().IsConnected, 10_000);
        var result = await Run(h,
            SetArg("who", "chat"),
            Sa(602, new { alias = "EventVoice", message = "hello %who%", badWordFilter = true }),
            Sa(605),
            Sa(607, new { profileName = "Gaming" }),
            Sa(601, new { alias = "Other", message = "via udp" }));
        Assert.Equal("ok", result.Status);
        await WaitUntil(() => sb.WsRequests.Count >= 2 && sb.UdpCommands.Count >= 2);
        var speak = sb.WsRequests.First();
        Assert.Equal("Speak", speak.GetProperty("request").GetString());
        Assert.Equal("EventVoice", speak.GetProperty("voice").GetString());
        Assert.Equal("hello chat", speak.GetProperty("message").GetString());
        Assert.Contains(sb.WsRequests, r => r.GetProperty("request").GetString() == "Stop");
        Assert.Contains(sb.UdpCommands, r => r.GetProperty("command").GetString() == "profile" && r.GetProperty("profile").GetString() == "Gaming");
        Assert.Contains(sb.UdpCommands, r => r.GetProperty("command").GetString() == "speak" && r.GetProperty("message").GetString() == "via udp");
    }
}
