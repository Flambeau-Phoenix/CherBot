using System.Text.Json;
using CherBot.Compat.Import;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using CherBot.Storage;
using Microsoft.Extensions.DependencyInjection;
using static CherBot.Tests.TestEngine;

namespace CherBot.Tests;

public class ImporterTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static TestEngine Engine(EngineOptions? options = null) => new(services =>
    {
        services.AddSingleton<StreamerBotImporter>();
        if (options is not null) services.AddSingleton(options);
    });

    [Fact]
    public void Generated_type_maps_match_the_streamerbot_enums()
    {
        Assert.Equal(133, EventTypes.Id("TwitchChatMessage"));
        Assert.Equal(("Twitch", "ChatMessage"), (EventTypes.Get(133)!.Source, EventTypes.Get(133)!.Type));
        Assert.Equal(401, EventTypes.Get("Command", "Triggered")!.Id);
        Assert.Equal(18001, EventTypes.Get("Custom", "Event")!.Id);
        Assert.Equal("DelayAction", SubActionTypes.Get(1002)!.Name);
        Assert.Equal("ExecuteCode", SubActionTypes.Get(99999)!.Name);
        Assert.True(EventTypes.All.Length >= 460);
        Assert.True(SubActionTypes.All.Length >= 390);
    }

    [Fact]
    public async Task Legacy_0_2_4_export_with_actions_key_imports()
    {
        await using var e = Engine();
        var report = e.Get<StreamerBotImporter>().Import(Fixture("export-gradient.txt"));
        Assert.Equal("export", report.Format);
        Assert.Equal("0.2.4", report.SourceVersion);
        var action = Assert.Single(e.Config.Actions);
        Assert.Equal(69, action.SubActions.Count);
        Assert.Equal(Enumerable.Range(0, 69), action.SubActions.Select(s => s.Index));
        Assert.All(action.SubActions, s => Assert.Equal(1009, s.Type));
        Assert.Empty(report.Unsupported);
    }

    [Fact]
    public async Task Quotes_export_imports_commands_nested_logic_and_runs_end_to_end()
    {
        await using var e = Engine();
        var report = e.Get<StreamerBotImporter>().Import(Fixture("export-quotes.txt"));
        Assert.Equal(4, report.Actions);
        Assert.Equal(3, report.Commands);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("Command Triggered"));
        Assert.DoesNotContain(report.Unsupported, u => u.Type is 120 or 1037 or 1009 or 124 or 401);

        var add = e.Config.Actions.Single(a => a.Name == "[Quotes] Add a Quote");
        var ifSa = add.SubActions.Single(s => s.Index == 1);
        Assert.Equal(120, ifSa.Type);
        Assert.Equal([99901, 99902], ifSa.SubActions!.Select(s => s.Type));

        // The export ships its commands disabled and limited to the Moderators group, as Streamer.bot does.
        var cmd = e.Config.Commands.Single(c => c.Command == "!quoteadd");
        Assert.False(cmd.Enabled);
        Assert.Equal(["Moderators"], cmd.PermittedGroups);
        cmd.Enabled = true;
        e.Config.SaveCommand(cmd);

        var completed = e.Capture(ev => ev.Type == "ActionCompleted");
        e.Bus.Publish(BotEvent.FromTypeName("TwitchChatMessage", new Dictionary<string, object?>
        {
            ["message"] = "!quoteadd not a mod", ["userId"] = "41", ["userName"] = "pleb", ["platform"] = "twitch",
        }));
        e.Bus.Publish(BotEvent.FromTypeName("TwitchChatMessage", new Dictionary<string, object?>
        {
            ["message"] = "!quoteadd the stream is live", ["userId"] = "42", ["userName"] = "viewer", ["user"] = "Viewer", ["platform"] = "twitch",
            ["isModerator"] = true,
        }));
        await WaitUntil(() => { lock (completed) return completed.Any(c => (string?)c.Data["actionName"] == add.Name); });
        var quote = Assert.Single(e.Get<CherBotDb>().GetQuotes());
        Assert.Equal("the stream is live", quote.Quote);
        Assert.Equal("Viewer", quote.User);

        // An empty !quoteadd stops at the Break inside the If's true branch.
        e.Bus.Publish(BotEvent.FromTypeName("TwitchChatMessage", new Dictionary<string, object?>
        {
            ["message"] = "!quoteadd", ["userId"] = "43", ["userName"] = "other", ["platform"] = "twitch", ["isModerator"] = true,
        }));
        await WaitUntil(() => { lock (completed) return completed.Count(c => (string?)c.Data["actionName"] == add.Name) >= 2; });
        Assert.Single(e.Get<CherBotDb>().GetQuotes());
    }

    [Fact]
    public async Task Execute_code_byte_code_is_base64_source()
    {
        await using var e = Engine();
        e.Get<StreamerBotImporter>().Import(Fixture("export-getcommands.txt"));
        var code = e.Config.Actions.Single().AllSubActions().Single(s => s.Type == 99999);
        var source = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(code.Str("byteCode")));
        Assert.Contains("public class CPHInline", source);
        Assert.Contains("CPH.TryGetArg(\"commandsList\"", source);
    }

    [Fact]
    public async Task Dry_run_reports_without_saving()
    {
        await using var e = Engine();
        var report = e.Get<StreamerBotImporter>().Import(Fixture("export-quotes.txt"), new ImportOptions { DryRun = true });
        Assert.True(report.DryRun);
        Assert.Equal(4, report.Actions);
        Assert.Empty(e.Config.Actions);
        Assert.Empty(e.Config.Commands);
    }

    [Fact]
    public async Task Reimport_updates_by_id_and_overwrite_by_name_replaces()
    {
        await using var e = Engine();
        var importer = e.Get<StreamerBotImporter>();
        importer.Import(Fixture("export-quotes.txt"));
        var again = importer.Import(Fixture("export-quotes.txt"));
        Assert.All(again.Items, i => Assert.Equal("updated", i.Status));
        Assert.Equal(4, e.Config.Actions.Count);

        var sameName = new ActionDef { Name = "[Quotes] Get Quote" };
        var json = JsonSerializer.Serialize(new { actions = new[] { sameName } }, CherBot.Core.CherBotJson.Options);
        var replaced = importer.Import(json);
        Assert.Equal("replaced", replaced.Items.Single().Status);
        Assert.Equal(4, e.Config.Actions.Count);
        Assert.Contains(e.Config.Actions, a => a.Id == sameName.Id);
    }

    [Fact]
    public async Task Unknown_types_round_trip_through_export_and_paths_are_mapped()
    {
        var options = new EngineOptions { PathMappings = new() { [@"C:\Stream\Sounds"] = "/srv/sounds" } };
        await using var e = Engine(options);
        var elgato = Sa(3001, new { elgatoSecret = "keep-me", nested = new { a = 1 } });
        var sound = Sa(1, new { soundFile = @"C:\Stream\Sounds\ding.wav", volume = 0.5 });
        var trigger = Trigger("ElgatoWaveLinkConnected");
        var action = new ActionDef { Name = "mixed", SubActions = [elgato, sound], Triggers = [trigger] };
        sound.Index = 1;
        var actionsJson = JsonSerializer.Serialize(new { version = 24, actions = new[] { action }, queues = Array.Empty<object>() }, CherBot.Core.CherBotJson.Options);

        var importer = e.Get<StreamerBotImporter>();
        var report = importer.Import(actionsJson);
        Assert.Equal("actions.json", report.Format);
        Assert.Contains(report.Unsupported, u => u.Kind == "subAction" && u.Type == 3001);
        Assert.Contains(report.Unsupported, u => u.Kind == "trigger" && u.TypeName == "ElgatoWaveLinkConnected");
        var path = Assert.Single(report.Paths);
        Assert.StartsWith("/srv/sounds", path.Mapped);

        var export = importer.Export([action.Id]);
        e.Config.DeleteAction(action.Id);
        importer.Import(export);
        var back = e.Config.GetAction(action.Id)!;
        var kept = back.SubActions.Single(s => s.Type == 3001);
        Assert.Equal("keep-me", kept.Str("elgatoSecret"));
        Assert.Equal(1, kept.Raw("nested")!.Value.GetProperty("a").GetInt32());

        // Unsupported sub-actions are skipped at run time without failing the action.
        var run = await e.Get<ActionDispatcher>().RunNowAsync(back, null, new DispatchSource("test"), CancellationToken.None);
        Assert.Equal("ok", run.Status);
    }

    [Fact]
    public async Task Live_0_2_4_actions_json_parses()
    {
        await using var e = Engine();
        var report = e.Get<StreamerBotImporter>().Import("""{"version":12,"actions":[],"queues":[]}""");
        Assert.Equal("actions.json", report.Format);
        Assert.Equal("12", report.SourceVersion);
        Assert.Contains(report.Warnings, w => w.StartsWith("Nothing to import"));
    }
}
