using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Model;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Core.Commands;

/// <summary>Command source flags, as stored in commands.json <c>sources</c>.</summary>
[Flags]
public enum CommandSource
{
    None = 0,
    Message = 1,
    Whisper = 2,
    BotWhisper = 4,
    Resub = 8,
    TwitchSharedChat = 0x10,
    YouTubeMessage = 0x400,
    KickMessage = 0x200000,
}

/// <summary>
/// Matches chat messages against commands (Start/Exact/Anywhere or regex), applies permissions and cooldowns,
/// and raises Command Triggered / Command Cooldown events that actions trigger on.
/// </summary>
public sealed class CommandService : IHostedService
{
    private readonly EventBus _bus;
    private readonly ConfigService _config;
    private readonly CherBotDb _db;
    private readonly ILogger<CommandService> _log;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _globalLast = new();
    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> _userLast = new();
    private readonly ConcurrentDictionary<Guid, long> _counter = new();
    private readonly ConcurrentDictionary<(Guid, string), long> _userCounter = new();
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();
    private IDisposable? _sub;

    public CommandService(EventBus bus, ConfigService config, CherBotDb db, ILogger<CommandService> log)
    {
        _bus = bus;
        _config = config;
        _db = db;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sub = _bus.Subscribe("commands", ev => { Process(ev); return Task.CompletedTask; }, ev => SourceOf(ev) != CommandSource.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sub?.Dispose();
        return Task.CompletedTask;
    }

    public static CommandSource SourceOf(BotEvent ev) => (ev.Source, ev.Type) switch
    {
        ("Twitch", "ChatMessage") => CommandSource.Message,
        ("Twitch", "Whisper") => CommandSource.Whisper,
        ("Twitch", "ReSub") => CommandSource.Resub,
        ("YouTube", "Message") => CommandSource.YouTubeMessage,
        ("Kick", "ChatMessage") => CommandSource.KickMessage,
        _ => CommandSource.None,
    };

    /// <summary>Checks a chat event against every command. Returns the events raised (for tests and the simulator).</summary>
    public List<BotEvent> Process(BotEvent chat)
    {
        var raised = new List<BotEvent>();
        var source = SourceOf(chat);
        var message = Str(chat, "message");
        if (source == CommandSource.None || string.IsNullOrWhiteSpace(message) || Flag(chat, "filtered")) return raised;

        var userId = Str(chat, "userId");
        var isBroadcaster = Flag(chat, "isBroadcaster");

        foreach (var cmd in _config.Commands)
        {
            if (!cmd.Enabled || (cmd.Sources & (int)source) == 0) continue;
            if (cmd.IgnoreBotAccount && Flag(chat, "isBot")) continue;
            if (cmd.IgnoreInternal && Flag(chat, "isInternal")) continue;

            var args = TryMatch(cmd, message);
            if (args is null) continue;
            if (!isBroadcaster && !IsPermitted(cmd, chat)) continue;

            var now = DateTimeOffset.UtcNow;
            if (!isBroadcaster)
            {
                var globalLeft = Remaining(_globalLast.GetValueOrDefault(cmd.Id), cmd.GlobalCooldown, now);
                var userLeft = Remaining(_userLast.GetValueOrDefault((cmd.Id, userId)), cmd.UserCooldown, now);
                if (globalLeft > 0 || userLeft > 0)
                {
                    var cd = BotEvent.FromTypeName("CommandCooldown", Merge(chat, new Dictionary<string, object?>
                    {
                        ["command"] = args["command"],
                        ["commandId"] = cmd.Id.ToString(),
                        ["commandName"] = cmd.Name,
                        ["globalCooldownLeft"] = globalLeft,
                        ["userCooldownLeft"] = userLeft,
                        ["cooldownLeft"] = Math.Max(globalLeft, userLeft),
                    }));
                    _bus.Publish(cd);
                    raised.Add(cd);
                    continue;
                }
            }

            _globalLast[cmd.Id] = now;
            if (userId.Length > 0) _userLast[(cmd.Id, userId)] = now;

            var counter = cmd.PersistCounter ? _db.IncrementCounter("command", cmd.Id.ToString()) : _counter.AddOrUpdate(cmd.Id, 1, (_, c) => c + 1);
            var userCounter = cmd.PersistUserCounter
                ? _db.IncrementCounter("command", cmd.Id.ToString(), userId)
                : _userCounter.AddOrUpdate((cmd.Id, userId), 1, (_, c) => c + 1);

            args["commandId"] = cmd.Id.ToString();
            args["commandName"] = cmd.Name;
            args["commandSource"] = Str(chat, "platform", chat.Source.ToLowerInvariant());
            args["commandType"] = source is CommandSource.Whisper or CommandSource.BotWhisper ? "whisper" : "message";
            args["counter"] = counter;
            args["userCounter"] = userCounter;
            if (chat.Data.TryGetValue("msgId", out var msgId)) args["msgId"] = msgId;

            var ev = BotEvent.FromTypeName("CommandTriggered", Merge(chat, args));
            _bus.Publish(ev);
            raised.Add(ev);
        }
        return raised;
    }

    private static double Remaining(DateTimeOffset last, int cooldownSeconds, DateTimeOffset now)
    {
        if (cooldownSeconds <= 0 || last == default) return 0;
        var left = cooldownSeconds - (now - last).TotalSeconds;
        return left > 0 ? Math.Ceiling(left) : 0;
    }

    /// <summary>Returns the command arguments when <paramref name="message"/> matches, otherwise null.</summary>
    internal Dictionary<string, object?>? TryMatch(CommandDef cmd, string message)
    {
        var cmp = cmd.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (cmd.Mode == 1)
        {
            var pattern = cmd.Command.Trim();
            if (pattern.Length == 0) return null;
            var options = (cmd.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant |
                          (cmd.RegexExplicitCapture ? RegexOptions.ExplicitCapture : RegexOptions.None);
            Regex rx;
            try { rx = _regexCache.GetOrAdd($"{(int)options}:{pattern}", _ => new Regex(pattern, options, TimeSpan.FromSeconds(1))); }
            catch (ArgumentException ex)
            {
                _log.LogWarning(ex, "Command {Command} has an invalid regex", cmd.Name);
                return null;
            }
            var m = rx.Match(message);
            if (!m.Success) return null;
            var args = InputArgs(m.Value, message);
            args["match.count"] = m.Groups.Count;
            for (var i = 0; i < m.Groups.Count; i++)
            {
                args[$"match[{i}]"] = m.Groups[i].Value;
                if (!int.TryParse(m.Groups[i].Name, out _)) args[m.Groups[i].Name] = m.Groups[i].Value;
            }
            return args;
        }

        foreach (var alias in cmd.Aliases())
        {
            switch (cmd.Location)
            {
                case 1 when string.Equals(message.Trim(), alias, cmp):
                    return InputArgs(alias, "");
                case 2 when message.Contains(alias, cmp):
                    return InputArgs(alias, message);
                case 0 when message.StartsWith(alias, cmp) && (message.Length == alias.Length || char.IsWhiteSpace(message[alias.Length])):
                    return InputArgs(alias, message[alias.Length..].Trim());
            }
        }
        return null;
    }

    private static Dictionary<string, object?> InputArgs(string command, string rawInput)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["command"] = command,
            ["rawInput"] = rawInput,
            ["rawInputEscaped"] = WebUtility.HtmlEncode(rawInput),
            ["rawInputUrlEncoded"] = Uri.EscapeDataString(rawInput),
        };
        var words = rawInput.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            args[$"input{i}"] = words[i];
            args[$"inputEscaped{i}"] = WebUtility.HtmlEncode(words[i]);
            args[$"inputUrlEncoded{i}"] = Uri.EscapeDataString(words[i]);
        }
        return args;
    }

    private bool IsPermitted(CommandDef cmd, BotEvent chat)
    {
        var users = PermittedUsers(cmd);
        var groups = cmd.PermittedGroups;
        var listed = users.Count + groups.Count > 0;

        bool InList()
        {
            var id = Str(chat, "userId");
            var login = Str(chat, "userName");
            var display = Str(chat, "user");
            if (users.Any(u => u.Equals(id, StringComparison.OrdinalIgnoreCase) || u.Equals(login, StringComparison.OrdinalIgnoreCase) ||
                               u.Equals(display, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (groups.Count == 0) return false;
            var platform = Str(chat, "platform", chat.Source.ToLowerInvariant());
            return groups.Any(g => _db.GetGroupUsers(g).Any(m =>
                string.Equals(m.Platform, platform, StringComparison.OrdinalIgnoreCase) && (m.UserId == id || string.Equals(m.Login, login, StringComparison.OrdinalIgnoreCase))) ||
                RoleGroup(g, chat));
        }

        var allowed = cmd.GrantType == 0 ? !listed || InList() : listed && !InList();
        if (!allowed) return false;

        // CherBot extension: optional minimum role ("viewer", "subscriber", "vip", "moderator", "broadcaster").
        var minRole = cmd.Str("minRole");
        return minRole.Length == 0 || RoleRank(chat) >= RoleRank(minRole);
    }

    private static bool RoleGroup(string group, BotEvent chat) => group.ToLowerInvariant() switch
    {
        "moderators" or "mods" => Flag(chat, "isModerator"),
        "vips" => Flag(chat, "isVip"),
        "subscribers" or "subs" => Flag(chat, "isSubscribed"),
        _ => false,
    };

    private static int RoleRank(BotEvent chat) =>
        Flag(chat, "isBroadcaster") ? 4 : Flag(chat, "isModerator") ? 3 : Flag(chat, "isVip") ? 2 : Flag(chat, "isSubscribed") ? 1 : 0;

    private static int RoleRank(string role) => role.ToLowerInvariant() switch
    {
        "broadcaster" => 4,
        "moderator" or "mod" => 3,
        "vip" => 2,
        "subscriber" or "sub" => 1,
        _ => 0,
    };

    private static List<string> PermittedUsers(CommandDef cmd)
    {
        var list = new List<string>();
        if (cmd.Raw("permittedUsers") is not { ValueKind: System.Text.Json.JsonValueKind.Array } arr) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind == System.Text.Json.JsonValueKind.String) list.Add(e.GetString() ?? "");
            else if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var key in new[] { "id", "userId", "login", "userLogin", "name", "userName" })
                    if (e.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String) list.Add(v.GetString() ?? "");
        }
        return list.Where(s => s.Length > 0).ToList();
    }

    public void ResetCounters(Guid commandId, bool users)
    {
        if (users)
        {
            foreach (var k in _userCounter.Keys.Where(k => k.Item1 == commandId).ToList()) _userCounter.TryRemove(k, out _);
        }
        else _counter.TryRemove(commandId, out _);
        _db.ResetCounters("command", commandId.ToString(), users);
    }

    private static Dictionary<string, object?> Merge(BotEvent chat, Dictionary<string, object?> extra)
    {
        var d = new Dictionary<string, object?>(chat.Data, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private static string Str(BotEvent ev, string key, string fallback = "") =>
        ev.Data.TryGetValue(key, out var v) && v is not null ? CherBotJson.Format(v) : fallback;

    private static bool Flag(BotEvent ev, string key) =>
        ev.Data.TryGetValue(key, out var v) && v is true or "true" or "True";
}
