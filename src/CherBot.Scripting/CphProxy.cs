using System.Globalization;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Core.Generated;
using CherBot.Core.Platform;
using CherBot.Core.State;
using CherBot.Integrations.Obs;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Streamer.bot.Common.Events;
using Streamer.bot.Plugin.Interface;
using Streamer.bot.Plugin.Interface.Enums;
using Streamer.bot.Plugin.Interface.Model;

namespace CherBot.Scripting;

/// <summary>The CPH object handed to scripts, bound to one run. Calls block on the engine's async services.</summary>
internal sealed class CphProxy(RunContext ctx, IServiceProvider sp) : IInlineInvokeProxy
{
    private T S<T>() where T : notnull => sp.GetRequiredService<T>();
    private CancellationToken Ct => ctx.Cancellation;
    private static void Sync(Task t) => t.GetAwaiter().GetResult();
    private static T Sync<T>(Task<T> t) => t.GetAwaiter().GetResult();

    /// <summary>Converts loosely typed values (JSON numbers, strings, lists) to the type a script asks for.</summary>
    internal static T ConvertTo<T>(object? value)
    {
        if (value is null) return default!;
        if (value is T t) return t;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        try
        {
            if (target == typeof(string)) return (T)(object)CherBotJson.Format(value);
            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
                return (T)Convert.ChangeType(value is string s && target != typeof(string) ? CherBotJson.AutoType(s)! : value, target, CultureInfo.InvariantCulture);
            return JsonSerializer.SerializeToElement(value, CherBotJson.Options).Deserialize<T>(CherBotJson.Options)!;
        }
        catch
        {
            return default!;
        }
    }

    // ── arguments ────────────────────────────────────────────────────────────

    public bool TryGetArg(string argName, out object value)
    {
        var found = ctx.Args.TryGetValue(argName, out var v) && v is not null;
        value = v!;
        return found;
    }

    public bool TryGetArg<T>(string argName, out T value)
    {
        if (ctx.Args.TryGetValue(argName, out var v) && v is not null)
        {
            value = ConvertTo<T>(v);
            return value is not null;
        }
        value = default!;
        return false;
    }

    public void SetArgument(string variableName, object value) => ctx.SetArg(variableName, value);

    public EventType GetEventType() =>
        Enum.TryParse<EventType>(ctx.ArgString("__source"), out var t) ? t : EventType.Unknown;

    // ── globals ──────────────────────────────────────────────────────────────

    public T GetGlobalVar<T>(string varName, bool persisted = true) => ConvertTo<T>(S<VariableService>().GetGlobal(varName, persisted));
    public void SetGlobalVar(string varName, object value, bool persisted = true) => S<VariableService>().SetGlobal(varName, value, persisted);
    public void UnsetGlobalVar(string varName, bool persisted = true) => S<VariableService>().UnsetGlobal(varName, persisted);

    public List<GlobalVariableValue> GetGlobalVarValues(bool persisted = true) =>
        S<VariableService>().ListGlobals(persisted).Select(v => new GlobalVariableValue { VariableName = v.Name, Value = v.Value, LastWrite = v.LastWrite.UtcDateTime }).ToList();

    private string TwitchIdForLogin(string login)
    {
        login = login.TrimStart('@');
        var vars = S<VariableService>();
        var known = vars.ListUser("twitch").FirstOrDefault(v => string.Equals(v.UserLogin, login, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known.UserId;
        if (sp.GetService<TwitchAuth>()?.Get(TwitchRole.Broadcaster) is not null &&
            Sync(S<HelixClient>().GetUserByLoginAsync(login, Ct)) is { } u)
            return u.GetProperty("id").GetString()!;
        return "login:" + login.ToLowerInvariant();
    }

    public T GetTwitchUserVar<T>(string userLogin, string varName, bool persisted = true) =>
        ConvertTo<T>(S<VariableService>().GetUserByLogin("twitch", userLogin.TrimStart('@'), varName, persisted)?.Value);

    public void SetTwitchUserVar(string userLogin, string varName, object value, bool persisted = true) =>
        S<VariableService>().SetUser("twitch", TwitchIdForLogin(userLogin), varName, value, persisted, userLogin.TrimStart('@').ToLowerInvariant());

    public T GetTwitchUserVarById<T>(string userId, string varName, bool persisted = true) =>
        ConvertTo<T>(S<VariableService>().GetUser("twitch", userId, varName, persisted));

    public void SetTwitchUserVarById(string userId, string varName, object value, bool persisted = true) =>
        S<VariableService>().SetUser("twitch", userId, varName, value, persisted);

    public void UnsetTwitchUserVar(string userLogin, string varName, bool persisted = true)
    {
        var v = S<VariableService>().GetUserByLogin("twitch", userLogin.TrimStart('@'), varName, persisted);
        if (v is not null) S<VariableService>().UnsetUser("twitch", v.UserId, varName, persisted);
    }

    // ── actions, queues, commands, timers ───────────────────────────────────

    public bool ActionExists(string actionName) => S<ConfigService>().FindAction(null, actionName) is not null;

    public List<ActionData> GetActions() =>
        S<ConfigService>().Actions.Select(a => new ActionData
        {
            Id = a.Id, Name = a.Name, Enabled = a.Enabled, Group = a.Group, QueueId = a.QueueId ?? Guid.Empty,
            Queue = a.QueueId is { } q ? S<ConfigService>().GetQueue(q)?.Name ?? "" : "",
        }).ToList();

    private bool Run(Guid? id, string? name, bool immediate)
    {
        if (S<ConfigService>().FindAction(id, name) is not { } action) return false;
        var args = new Dictionary<string, object?>(ctx.Args, StringComparer.OrdinalIgnoreCase);
        var source = new DispatchSource($"C# ({ctx.Action.Name})");
        if (!immediate) return S<ActionDispatcher>().Dispatch(action, args, source) is not null;
        var result = Sync(S<ActionDispatcher>().RunNowAsync(action, args, source, Ct, ctx.Depth + 1));
        return result.Success;
    }

    public bool RunAction(string actionName, bool runImmediately = true) => Run(null, actionName, runImmediately);
    public bool RunActionById(string actionId, bool runImmediately = true) => Guid.TryParse(actionId, out var g) && Run(g, null, runImmediately);

    private void SetAction(Guid? id, string? name, bool enabled)
    {
        var cfg = S<ConfigService>();
        if (cfg.FindAction(id, name) is not { } a) return;
        a.Enabled = enabled;
        cfg.SaveAction(a);
    }

    public void EnableAction(string actionName) => SetAction(null, actionName, true);
    public void DisableAction(string actionName) => SetAction(null, actionName, false);
    public void EnableActionById(string actionId) => SetAction(Guid.TryParse(actionId, out var g) ? g : null, null, true);
    public void DisableActionById(string actionId) => SetAction(Guid.TryParse(actionId, out var g) ? g : null, null, false);

    private Guid? QueueId(string name) => S<ConfigService>().FindQueue(null, name)?.Id ?? (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ? QueueManager.DefaultQueueId : null);
    public void PauseActionQueue(string queueName) { if (QueueId(queueName) is { } id) S<QueueManager>().Pause(id); }
    public void ResumeActionQueue(string queueName, bool clear = false) { if (QueueId(queueName) is { } id) S<QueueManager>().Resume(id, clear); }
    public void PauseAllActionQueues() => S<QueueManager>().Pause(null);
    public void ResumeAllActionQueues(bool clear = false) => S<QueueManager>().Resume(null, clear);

    private void SetCommand(string id, bool enabled)
    {
        var cfg = S<ConfigService>();
        if (cfg.FindCommand(Guid.TryParse(id, out var g) ? g : null, id) is not { } c) return;
        c.Enabled = enabled;
        cfg.SaveCommand(c);
    }

    public void EnableCommand(string commandId) => SetCommand(commandId, true);
    public void DisableCommand(string commandId) => SetCommand(commandId, false);

    private void SetTimer(Guid? id, string? name, Action<CherBot.Core.Model.TimerDef> change)
    {
        var cfg = S<ConfigService>();
        if (cfg.FindTimer(id, name) is not { } t) return;
        change(t);
        cfg.SaveTimer(t);
    }

    public void EnableTimer(string timerName) => SetTimer(null, timerName, t => t.Enabled = true);
    public void DisableTimer(string timerName) => SetTimer(null, timerName, t => t.Enabled = false);
    public void EnableTimerById(string timerId) => SetTimer(Guid.TryParse(timerId, out var g) ? g : null, null, t => t.Enabled = true);
    public void DisableTimerById(string timerId) => SetTimer(Guid.TryParse(timerId, out var g) ? g : null, null, t => t.Enabled = false);
    public void SetTimerInterval(string timerName, int interval) => SetTimer(null, timerName, t => t.Interval = Math.Max(1, interval));

    public bool ExecuteMethod(string executeCode, string methodName)
    {
        var host = S<ScriptHost>();
        var sa = (Guid.TryParse(executeCode, out var g) ? host.FindCode(g) : null) ?? host.FindCodeByName(executeCode);
        return sa is not null && Sync(host.RunAsync(sa, ctx, methodName));
    }

    // ── triggers and events ──────────────────────────────────────────────────

    public bool RegisterCustomTrigger(string triggerName, string eventName, string[] categories) =>
        S<CodeTriggerRegistry>().Register(triggerName, eventName, categories);

    public void TriggerCodeEvent(string eventName, bool useArgs = true) =>
        S<CodeTriggerRegistry>().Fire(eventName, useArgs ? new Dictionary<string, object?>(ctx.Args) : null);

    public void TriggerCodeEvent(string eventName, Dictionary<string, object> args) =>
        S<CodeTriggerRegistry>().Fire(eventName, args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));

    public void TriggerCodeEvent(string eventName, string json)
    {
        using var doc = JsonDocument.Parse(json);
        S<CodeTriggerRegistry>().Fire(eventName, doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => CherBotJson.ToClr(p.Value)));
    }

    public void WebsocketBroadcastJson(string data) => Sync(sp.GetService<IWebSocketBroadcaster>()?.BroadcastAsync(data) ?? Task.CompletedTask);
    public void WebsocketBroadcastString(string data) => WebsocketBroadcastJson(data);

    // ── logging and utilities ────────────────────────────────────────────────

    private ILogger Log => ctx.Log;
    public void LogInfo(string logLine) => Log.LogInformation("[C# {Action}] {Line}", ctx.Action.Name, logLine);
    public void LogWarn(string logLine) => Log.LogWarning("[C# {Action}] {Line}", ctx.Action.Name, logLine);
    public void LogDebug(string logLine) => Log.LogDebug("[C# {Action}] {Line}", ctx.Action.Name, logLine);
    public void LogError(string logLine) => Log.LogError("[C# {Action}] {Line}", ctx.Action.Name, logLine);
    public void LogVerbose(string logLine) => Log.LogTrace("[C# {Action}] {Line}", ctx.Action.Name, logLine);
    public void Wait(int milliseconds) => Task.Delay(milliseconds, Ct).GetAwaiter().GetResult();
    public int Between(int min, int max) => Random.Shared.Next(Math.Min(min, max), Math.Max(min, max) + 1);
    public double NextDouble() => Random.Shared.NextDouble();

    public double PlaySound(string fileName, float volume = 1f, bool finishBeforeContinuing = false, string? name = null, bool useFileName = true)
    {
        var file = S<IPathMapper>().Map(fileName);
        Sync(S<ISoundPlayer>().PlayAsync(file, volume > 1 ? volume / 100f : volume, finishBeforeContinuing, Ct));
        return 0;
    }

    public double PlaySoundFromFolder(string path, float volume = 1f, bool recursive = false, bool finishBeforeContinuing = false, string? name = null, bool useFileName = true)
    {
        var folder = S<IPathMapper>().Map(path);
        if (!Directory.Exists(folder)) return 0;
        var files = Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".wav" or ".mp3" or ".ogg" or ".flac" or ".m4a").ToList();
        return files.Count == 0 ? 0 : PlaySound(files[Random.Shared.Next(files.Count)], volume, finishBeforeContinuing, name, useFileName);
    }

    // ── users, groups, quotes ────────────────────────────────────────────────

    private static string PlatformName(Platform p) => p.ToString().ToLowerInvariant();

    public bool AddUserToGroup(string userName, Platform platform, string groupName)
    {
        var id = platform == Platform.Twitch ? TwitchIdForLogin(userName) : "login:" + userName.ToLowerInvariant();
        return S<CherBotDb>().AddGroupUser(groupName, PlatformName(platform), id, userName.ToLowerInvariant(), userName);
    }

    public bool RemoveUserFromGroup(string userName, Platform platform, string groupName)
    {
        var member = S<CherBotDb>().GetGroupUsers(groupName).FirstOrDefault(m =>
            m.Platform == PlatformName(platform) && string.Equals(m.Login, userName, StringComparison.OrdinalIgnoreCase));
        return member is not null && S<CherBotDb>().RemoveGroupUser(groupName, member.Platform, member.UserId);
    }

    public bool UserInGroup(string userName, Platform platform, string groupName) =>
        S<CherBotDb>().GetGroupUsers(groupName).Any(m => m.Platform == PlatformName(platform) && string.Equals(m.Login, userName, StringComparison.OrdinalIgnoreCase));

    public QuoteData? GetQuote(int quoteId) =>
        S<CherBotDb>().GetQuote(quoteId) is { } q
            ? new QuoteData { Id = (int)q.Id, Quote = q.Quote, User = q.User, UserId = q.UserId, Platform = q.Platform, GameName = q.Game, Timestamp = DateTimeOffset.Parse(q.Timestamp, CultureInfo.InvariantCulture).UtcDateTime }
            : null;

    public int AddQuoteForTwitch(string userId, string quote, bool captureGame = false)
    {
        var user = S<VariableService>().ListUser("twitch", null, userId).FirstOrDefault()?.UserName ?? ctx.ArgString("user");
        return (int)S<CherBotDb>().AddQuote(userId, user, "twitch", captureGame ? ctx.ArgString("game") : "", quote);
    }

    // ── Twitch ───────────────────────────────────────────────────────────────

    private TwitchService Twitch => S<TwitchService>();
    private HelixClient Helix => S<HelixClient>();
    private string BroadcasterId => S<TwitchAuth>().Get(TwitchRole.Broadcaster)?.UserId ?? throw new InvalidOperationException("Twitch broadcaster is not linked");

    public void SendMessage(string message, bool bot = true, bool fallback = true) =>
        Sync(Twitch.SendChatAsync(message, bot, fallback, null, Ct));

    public void SendAction(string action, bool bot = true, bool fallback = true) =>
        Sync(Twitch.SendChatAsync(action, bot, fallback, null, Ct));

    public void TwitchReplyToMessage(string message, string replyId, bool useBot = true, bool fallback = true) =>
        Sync(Twitch.SendChatAsync(message, useBot, fallback, replyId, Ct));

    public void TwitchAnnounce(string message, bool bot = false, string? color = null, bool fallback = false)
    {
        var role = bot && S<TwitchAuth>().Get(TwitchRole.Bot) is not null ? TwitchRole.Bot : TwitchRole.Broadcaster;
        Sync(Helix.SendAsync(HttpMethod.Post, "chat/announcements",
            new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId, ["moderator_id"] = S<TwitchAuth>().Get(role)!.UserId },
            new { message, color = string.IsNullOrWhiteSpace(color) ? "primary" : color.ToLowerInvariant() }, role, Ct));
    }

    public bool TwitchTimeoutUser(string username, int duration, string? reason = null, bool bot = false)
    {
        if (Sync(Helix.GetUserByLoginAsync(username, Ct)) is not { } u) return false;
        Sync(Helix.SendAsync(HttpMethod.Post, "moderation/bans",
            new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId, ["moderator_id"] = BroadcasterId },
            new { data = new { user_id = u.GetProperty("id").GetString(), duration, reason = reason ?? "" } }, ct: Ct));
        return true;
    }

    public TwitchUserInfoEx? TwitchGetUserInfoByLogin(string userLogin)
    {
        if (Sync(Helix.GetUserByLoginAsync(userLogin, Ct)) is not { } u) return null;
        var created = u.GetProperty("created_at").GetDateTimeOffset();
        return new TwitchUserInfoEx
        {
            UserId = u.GetProperty("id").GetString()!, UserLogin = u.GetProperty("login").GetString()!, UserName = u.GetProperty("display_name").GetString()!,
            Description = u.GetProperty("description").GetString() ?? "", ProfileImageUrl = u.GetProperty("profile_image_url").GetString() ?? "",
            UserType = u.GetProperty("broadcaster_type").GetString() ?? "", CreatedAt = created.UtcDateTime, AccountAge = (DateTimeOffset.UtcNow - created).TotalSeconds,
        };
    }

    public bool TwitchIsUserSubscribed(string userId, out string tier)
    {
        var res = Sync(Helix.GetAsync("subscriptions", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId, ["user_id"] = userId }, ct: Ct));
        var sub = HelixClient.First(res);
        tier = sub is { } s ? s.GetProperty("tier").GetString() ?? "" : "";
        return sub is not null;
    }

    private bool Redemption(string rewardId, string redemptionId, string status)
    {
        Sync(Helix.SendAsync(new HttpMethod("PATCH"), "channel_points/custom_rewards/redemptions",
            new Dictionary<string, string?> { ["id"] = redemptionId, ["broadcaster_id"] = BroadcasterId, ["reward_id"] = rewardId }, new { status }, ct: Ct));
        return true;
    }

    public bool TwitchRedemptionFulfill(string rewardId, string redemptionId) => Redemption(rewardId, redemptionId, "FULFILLED");
    public bool TwitchRedemptionCancel(string rewardId, string redemptionId) => Redemption(rewardId, redemptionId, "CANCELED");

    public bool SetChannelTitle(string title)
    {
        Sync(Helix.SendAsync(new HttpMethod("PATCH"), "channels", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId }, new { title }, ct: Ct));
        return true;
    }

    public GameInfo? SetChannelGame(string game)
    {
        var found = HelixClient.First(Sync(Helix.GetAsync("games", new Dictionary<string, string?> { ["name"] = game }, ct: Ct)));
        if (found is not { } g) return null;
        var id = g.GetProperty("id").GetString()!;
        Sync(Helix.SendAsync(new HttpMethod("PATCH"), "channels", new Dictionary<string, string?> { ["broadcaster_id"] = BroadcasterId }, new { game_id = id }, ct: Ct));
        return new GameInfo { Id = id, Name = g.GetProperty("name").GetString()!, BoxArtUrl = g.GetProperty("box_art_url").GetString() ?? "" };
    }

    public StreamMarker? CreateStreamMarker(string description)
    {
        var m = HelixClient.First(Sync(Helix.SendAsync(HttpMethod.Post, "streams/markers", body: new { user_id = BroadcasterId, description }, ct: Ct)));
        return m is { } x
            ? new StreamMarker { Id = x.GetProperty("id").GetString()!, Description = description, PositionSeconds = x.GetProperty("position_seconds").GetInt32(), CreatedAt = DateTime.UtcNow }
            : null;
    }

    // ── OBS ──────────────────────────────────────────────────────────────────

    private ObsClient? ObsClient(int connection)
    {
        var obs = S<ObsService>();
        var defs = obs.Definitions();
        Guid? id = connection >= 0 && connection < defs.Count ? defs[connection].Id : null;
        return obs.Client(id);
    }

    private ObsClient Obs(int connection) => ObsClient(connection) ?? throw new InvalidOperationException("No OBS connection is available");

    public bool ObsIsConnected(int connection = 0) => ObsClient(connection) is not null;

    public string ObsGetCurrentScene(int connection = 0)
    {
        var r = Sync(Obs(connection).RequestAsync("GetCurrentProgramScene", null, Ct));
        return r.TryGetProperty("currentProgramSceneName", out var n) ? n.GetString()! : r.GetProperty("sceneName").GetString()!;
    }

    public void ObsSetScene(string sceneName, int connection = 0) =>
        Sync(Obs(connection).RequestAsync("SetCurrentProgramScene", new { sceneName }, Ct));

    public void ObsSetSourceVisibility(string scene, string source, bool visible, int connection = 0)
    {
        var obs = Obs(connection);
        var id = Sync(obs.RequestAsync("GetSceneItemId", new { sceneName = scene, sourceName = source }, Ct)).GetProperty("sceneItemId").GetInt32();
        Sync(obs.RequestAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = id, sceneItemEnabled = visible }, Ct));
    }

    public void ObsShowSource(string scene, string source, int connection = 0) => ObsSetSourceVisibility(scene, source, true, connection);
    public void ObsHideSource(string scene, string source, int connection = 0) => ObsSetSourceVisibility(scene, source, false, connection);

    public void ObsSetFilterState(string source, string filterName, int state, int connection = 0)
    {
        var obs = Obs(connection);
        var enabled = state switch
        {
            0 => true,
            1 => false,
            _ => !Sync(obs.RequestAsync("GetSourceFilter", new { sourceName = source, filterName }, Ct)).GetProperty("filterEnabled").GetBoolean(),
        };
        Sync(obs.RequestAsync("SetSourceFilterEnabled", new { sourceName = source, filterName, filterEnabled = enabled }, Ct));
    }

    public void ObsSetFilterState(string scene, string source, string filterName, int state, int connection = 0) => ObsSetFilterState(source, filterName, state, connection);
    public void ObsShowFilter(string source, string filterName, int connection = 0) => ObsSetFilterState(source, filterName, 0, connection);
    public void ObsShowFilter(string scene, string source, string filterName, int connection = 0) => ObsSetFilterState(source, filterName, 0, connection);
    public void ObsHideFilter(string source, string filterName, int connection = 0) => ObsSetFilterState(source, filterName, 1, connection);
    public void ObsHideFilter(string scene, string source, string filterName, int connection = 0) => ObsSetFilterState(source, filterName, 1, connection);

    public void ObsSetGdiText(string scene, string source, string text, int connection = 0) =>
        Sync(Obs(connection).RequestAsync("SetInputSettings", new { inputName = source, inputSettings = new { text } }, Ct));

    public string ObsSendRaw(string requestType, string data, int connection = 0)
    {
        object? payload = null;
        if (!string.IsNullOrWhiteSpace(data))
            using (var doc = JsonDocument.Parse(data)) payload = doc.RootElement.Clone();
        return Sync(Obs(connection).RequestAsync(requestType, payload, Ct)).GetRawText();
    }
}
