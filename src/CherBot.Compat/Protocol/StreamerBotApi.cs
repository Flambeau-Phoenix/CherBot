using System.Runtime.InteropServices;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Core.Platform;
using CherBot.Core.State;
using CherBot.Storage;
using Microsoft.Extensions.Logging;

namespace CherBot.Compat.Protocol;

/// <summary>Settings for the Streamer.bot-compatible servers, supplied by the host.</summary>
public sealed class SbServerSettings
{
    public string InstanceName { get; set; } = Environment.MachineName;
    public string Version { get; set; } = "0.0.0";
    public string Endpoint { get; set; } = "/";
    public string? Password { get; set; }
    public bool EnforceAuth { get; set; }
}

public sealed record SbResult(bool Ok, object? Body = null, string? Error = null)
{
    public static SbResult Success(object? body = null) => new(true, body);
    public static SbResult Fail(string error) => new(false, null, error);
}

/// <summary>Request handling shared by the Streamer.bot-compatible WebSocket, HTTP and UDP servers.</summary>
public sealed class StreamerBotApi
{
    private readonly ConfigService _config;
    private readonly ActionDispatcher _dispatcher;
    private readonly VariableService _vars;
    private readonly CodeTriggerRegistry _codeTriggers;
    private readonly IEnumerable<IChatSender> _chat;
    private readonly IEnumerable<IBroadcasterInfo> _broadcasters;
    private readonly IEnumerable<IActiveViewers> _viewers;
    private readonly CherBotDb _db;
    private readonly EventBus _bus;
    private readonly SbServerSettings _settings;
    private readonly ILogger<StreamerBotApi> _log;

    public StreamerBotApi(ConfigService config, ActionDispatcher dispatcher, VariableService vars, CodeTriggerRegistry codeTriggers,
        IEnumerable<IChatSender> chat, IEnumerable<IBroadcasterInfo> broadcasters, IEnumerable<IActiveViewers> viewers,
        CherBotDb db, EventBus bus, SbServerSettings settings, ILogger<StreamerBotApi> log)
    {
        _config = config;
        _dispatcher = dispatcher;
        _vars = vars;
        _codeTriggers = codeTriggers;
        _chat = chat;
        _broadcasters = broadcasters;
        _viewers = viewers;
        _db = db;
        _bus = bus;
        _settings = settings;
        _log = log;
        InstanceId = db.GetKv("instanceId") is { } id ? JsonSerializer.Deserialize<string>(id)! : NewInstanceId(db);
    }

    public string InstanceId { get; }
    public SbServerSettings Settings => _settings;

    private static string NewInstanceId(CherBotDb db)
    {
        var id = Guid.NewGuid().ToString();
        db.SetKv("instanceId", JsonSerializer.Serialize(id));
        return id;
    }

    public object Info(string source) => new
    {
        instanceId = InstanceId,
        name = _settings.InstanceName,
        os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macosx" : "linux",
        osVersion = Environment.OSVersion.Version.ToString(),
        version = _settings.Version,
        source,
        mode = "headless",
        application = "CherBot",
        runtime = RuntimeInformation.FrameworkDescription,
    };

    /// <summary>Every subscribable source and type, in the GetEvents shape.</summary>
    public Dictionary<string, List<string>> Events()
    {
        var events = EventTypes.All
            .GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Type).Distinct().ToList());
        events["CherBot"] = ["PlaySound", "StopSounds", "Log", "GameResult", "GameAnnouncement", "ChatFiltered", "ModAction"];
        return events;
    }

    // ── actions ────────────────────────────────────────────────────────────

    public object ActionSummary(ActionDef a) => new
    {
        id = a.Id.ToString(),
        name = a.Name,
        group = a.Group,
        enabled = a.Enabled,
        subaction_count = a.AllSubActions().Count(),
    };

    public object GetActions()
    {
        var actions = _config.Actions.Select(ActionSummary).ToList();
        return new { actions, count = actions.Count };
    }

    public SbResult DoAction(JsonElement? action, JsonElement? args, string source)
    {
        Guid? id = null;
        string? name = null;
        if (action is { ValueKind: JsonValueKind.Object } a)
        {
            if (a.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && Guid.TryParse(idEl.GetString(), out var g)) id = g;
            if (a.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String) name = nameEl.GetString();
        }
        else if (action is { ValueKind: JsonValueKind.String } s)
        {
            if (Guid.TryParse(s.GetString(), out var g)) id = g;
            else name = s.GetString();
        }

        var def = _config.FindAction(id, name);
        if (def is null) return SbResult.Fail("Action not found");

        var dict = ArgsToDictionary(args);
        var run = _dispatcher.Dispatch(def, dict, new DispatchSource(source));
        if (run is null) return SbResult.Fail("Action is disabled");

        if (dict.TryGetValue("sbClientResponse", out var responseId) && responseId is string rid && rid.Length > 0)
        {
            // Reply automatically unless the action raised its own custom event response.
            _ = run.Done.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result.Args.TryGetValue("__sbClientResponded", out var done) && done is true) return;
                var resultArgs = t.IsCompletedSuccessfully
                    ? t.Result.Args.Where(kv => !kv.Key.StartsWith("__", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value)
                    : new Dictionary<string, object?>();
                _bus.Publish(BotEvent.FromTypeName("CustomEvent", new Dictionary<string, object?> { ["eventName"] = rid, ["args"] = resultArgs }));
            }, TaskScheduler.Default);
        }
        return SbResult.Success();
    }

    public static Dictionary<string, object?> ArgsToDictionary(JsonElement? args)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (args is { ValueKind: JsonValueKind.Object } o)
            foreach (var p in o.EnumerateObject()) dict[p.Name] = CherBotJson.ToClr(p.Value);
        return dict;
    }

    // ── commands ───────────────────────────────────────────────────────────

    public object GetCommands()
    {
        var commands = _config.Commands.Select(c => new
        {
            id = c.Id.ToString(),
            enabled = c.Enabled,
            name = c.Name,
            group = c.Group,
            commands = c.Aliases().ToList(),
            caseSensitive = c.CaseSensitive,
            ignoreInternal = c.IgnoreInternal,
            ignoreBotAccount = c.IgnoreBotAccount,
            sources = SourceNames(c.Sources),
        }).ToList();
        return new { commands, count = commands.Count };
    }

    private static List<string> SourceNames(int flags)
    {
        var names = new List<string>();
        if ((flags & 1) != 0) names.Add("twitch");
        if ((flags & 2) != 0) names.Add("twitchWhisper");
        if ((flags & 8) != 0) names.Add("twitchResub");
        if ((flags & 0x400) != 0) names.Add("youtube");
        if ((flags & 0x200000) != 0) names.Add("kick");
        return names;
    }

    // ── globals ────────────────────────────────────────────────────────────

    private static object GlobalJson(GlobalVariable v) => new { name = v.Name, value = v.Value, lastWrite = v.LastWrite };

    public object GetGlobals(bool persisted)
    {
        var vars = _vars.ListGlobals(persisted).ToDictionary(v => v.Name, GlobalJson);
        return new { variables = vars, count = vars.Count };
    }

    public SbResult GetGlobal(string name, bool persisted)
    {
        if (!_vars.TryGetGlobal(name, persisted, out _)) return SbResult.Fail("Variable not found");
        var v = _vars.ListGlobals(persisted).First(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        return SbResult.Success(new { variables = new Dictionary<string, object> { [v.Name] = GlobalJson(v) }, count = 1 });
    }

    public object GetUserGlobals(string platform, string? name, bool persisted)
    {
        var vars = _vars.ListUser(platform, name, null, persisted).Select(v => new
        {
            userId = v.UserId, userLogin = v.UserLogin, userName = v.UserName, platform = v.Platform,
            name = v.Name, value = v.Value, lastWrite = v.LastWrite,
        }).ToList();
        return new { variables = vars, count = vars.Count };
    }

    public object GetUserGlobal(string platform, string userId, string? name, bool persisted)
    {
        var vars = _vars.ListUser(platform, name, userId, persisted).Select(v => new { name = v.Name, value = v.Value, lastWrite = v.LastWrite }).ToList();
        return new { variables = vars, count = vars.Count };
    }

    // ── platforms ──────────────────────────────────────────────────────────

    public object GetBroadcaster()
    {
        var platforms = new Dictionary<string, object>();
        var connected = new List<string>();
        var disconnected = new List<string>();
        foreach (var b in _broadcasters)
        {
            var acc = b.GetAccount();
            (acc.Connected ? connected : disconnected).Add(acc.Platform);
            if (acc.BroadcasterId is null) continue;
            platforms[acc.Platform] = new
            {
                broadcastUser = acc.BroadcasterName, broadcastUserName = acc.BroadcasterLogin, broadcastUserId = acc.BroadcasterId,
                broadcastIsAffiliate = acc.IsAffiliate, broadcastIsPartner = acc.IsPartner,
                botUser = acc.BotName, botUserName = acc.BotLogin, botUserId = acc.BotId,
            };
        }
        return new { platforms, connected, disconnected };
    }

    public object GetActiveViewers()
    {
        var viewers = _viewers.SelectMany(v => v.GetActiveViewers()).Select(v => new
        {
            channelPointsUsed = 0, display = v.Display, exempt = false, groups = Array.Empty<string>(), id = v.Id, login = v.Login,
            previousActive = v.LastActive, role = v.Role, subscribed = v.Subscribed,
        }).ToList();
        return new { viewers, count = viewers.Count };
    }

    public async Task<SbResult> SendMessageAsync(string platform, string message, bool bot, bool isInternal, string? replyId, CancellationToken ct)
    {
        var sender = _chat.FirstOrDefault(c => string.Equals(c.Platform, platform, StringComparison.OrdinalIgnoreCase));
        if (sender is null) return SbResult.Fail($"Platform '{platform}' is not available");
        var ok = await sender.SendMessageAsync(message, bot, replyId, ct);
        return ok ? SbResult.Success() : SbResult.Fail("Message was not sent");
    }

    // ── code triggers, credits ─────────────────────────────────────────────

    public object GetCodeTriggers()
    {
        var triggers = _codeTriggers.All.Select(t => new { name = t.Name, eventName = t.EventName, category = string.Join(" / ", t.Categories) }).ToList();
        return new { triggers, count = triggers.Count };
    }

    public void ExecuteCodeTrigger(string triggerName, JsonElement? args) => _codeTriggers.Fire(triggerName, ArgsToDictionary(args));

    /// <summary>Credits are not tracked; this is the documented empty shape (PascalCase, as the HTTP server returns it).</summary>
    public static Dictionary<string, object> EmptyCredits()
    {
        static object[] E() => [];
        return new Dictionary<string, object>
        {
            ["Events"] = new Dictionary<string, object>
            {
                ["Follows"] = E(), ["Cheers"] = E(), ["Subs"] = E(), ["ReSubs"] = E(), ["GiftSubs"] = E(), ["GiftBombs"] = E(),
                ["Raided"] = E(), ["RewardRedemptions"] = E(), ["GoalContributions"] = E(), ["GameUpdates"] = E(), ["Pyramids"] = E(),
            },
            ["HypeTrainConductor"] = E(),
            ["HypeTrainContributors"] = E(),
            ["User"] = new Dictionary<string, object>
            {
                ["Editors"] = E(), ["Moderator"] = E(), ["Subscriber"] = E(), ["VIPs"] = E(), ["Users"] = E(), ["regulars"] = E(),
            },
            ["Custom"] = new Dictionary<string, object>(),
            ["TopBits"] = new Dictionary<string, object> { ["All"] = E(), ["Month"] = E(), ["Week"] = E() },
        };
    }

    public void ClearFirstWords() => _db.ResetFirstWords();

    /// <summary>Converts an event to the Streamer.bot WebSocket event envelope.</summary>
    public static object EventEnvelope(BotEvent ev) => new
    {
        timestamp = ev.Timestamp,
        @event = new { source = ev.Source, type = ev.Type },
        data = ev.Data,
    };
}
