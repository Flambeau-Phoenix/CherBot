using System.Globalization;
using System.Text.RegularExpressions;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Engine;
using CherBot.Core.Generated;
using CherBot.Core.Model;
using CherBot.Integrations.Common;
using CherBot.Integrations.Community;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Moderation;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.CustomCommands;

/// <summary>A Nightbot-style command (doc kind "customCommand"), kept in sync with a real command plus action.</summary>
public sealed class CustomCommandDef
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "!hello";
    public List<string> Aliases { get; set; } = [];
    public bool Enabled { get; set; } = true;
    /// <summary>Response variants, one picked at random. A multi-line variant sends one message per line.</summary>
    public List<string> Responses { get; set; } = ["Hello, {user}!"];
    public int LineDelayMs { get; set; } = 1000;
    /// <summary>chat, reply or announcement.</summary>
    public string ReplyMode { get; set; } = "chat";
    public string Color { get; set; } = "primary";
    public string MinRole { get; set; } = "everyone";
    public List<string> PermittedGroups { get; set; } = [];
    public int GlobalCooldown { get; set; } = 5;
    public int UserCooldown { get; set; } = 10;
    public long Cost { get; set; }
    public bool OnlyWhileLive { get; set; }
    /// <summary>Set when the action was edited in the visual builder; the simple form then leaves the action alone.</summary>
    public bool Advanced { get; set; }
    public Guid? CommandId { get; set; }
    public Guid? ActionId { get; set; }
}

/// <summary>Stores custom commands, syncs them to commands and actions in the "Custom Commands" group, and renders their responses.</summary>
public sealed partial class CustomCommandService : IHostedService
{
    public const string Kind = "customCommand";
    public const string Group = "Custom Commands";

    private readonly ConfigService _config;
    private readonly CherBotDb _db;
    private readonly LoyaltyService _loyalty;
    private readonly HelixClient _helix;
    private readonly TwitchAuth _auth;
    private readonly TwitchService _twitch;
    private readonly ChatModeration _mod;
    private readonly TemplateEngine _templates;
    private readonly ILogger<CustomCommandService> _log;
    private readonly Dictionary<Guid, CustomCommandDef> _defs = [];
    private readonly object _lock = new();
    private bool _syncing;

    public CustomCommandService(ConfigService config, CherBotDb db, LoyaltyService loyalty, HelixClient helix, TwitchAuth auth, TwitchService twitch,
        ChatModeration mod, TemplateEngine templates, ILogger<CustomCommandService> log)
    {
        _config = config;
        _db = db;
        _loyalty = loyalty;
        _helix = helix;
        _auth = auth;
        _twitch = twitch;
        _mod = mod;
        _templates = templates;
        _log = log;
        foreach (var d in config.GetDocuments<CustomCommandDef>(Kind)) _defs[d.Id] = d;
    }

    public Func<int, int, int> Random { get; set; } = (min, maxExclusive) => System.Random.Shared.Next(min, maxExclusive);
    public Func<TimeSpan, Task> Delay { get; set; } = t => Task.Delay(t);

    public IReadOnlyList<CustomCommandDef> All
    {
        get { lock (_lock) return _defs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public CustomCommandDef? Get(Guid id)
    {
        lock (_lock) return _defs.GetValueOrDefault(id);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _config.Changed += OnConfigChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _config.Changed -= OnConfigChanged;
        return Task.CompletedTask;
    }

    /// <summary>An action saved by someone else (the builder) marks its custom command advanced.</summary>
    private void OnConfigChanged(string kind, Guid id)
    {
        if (_syncing || kind != "action") return;
        CustomCommandDef? def;
        lock (_lock) def = _defs.Values.FirstOrDefault(d => d.ActionId == id && !d.Advanced);
        if (def is null || _config.GetAction(id) is null) return;
        def.Advanced = true;
        _config.SaveDocument(Kind, def.Id.ToString(), def.Name, def);
    }

    /// <summary>Every trigger word of a definition (name plus aliases).</summary>
    public static IEnumerable<string> Words(CustomCommandDef d) =>
        new[] { d.Name }.Concat(d.Aliases).Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns an error when another custom command already uses one of the words.</summary>
    public string? Validate(CustomCommandDef def)
    {
        if (string.IsNullOrWhiteSpace(def.Name)) return "The command needs a name, such as !hello.";
        if (def.Responses.All(string.IsNullOrWhiteSpace)) return "Add at least one response.";
        var words = Words(def).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clash = All.Where(o => o.Id != def.Id).SelectMany(o => Words(o).Select(w => (o, w))).FirstOrDefault(x => words.Contains(x.w));
        return clash.o is null ? null : $"{clash.w} is already used by the custom command {clash.o.Name}.";
    }

    public CustomCommandDef Save(CustomCommandDef def)
    {
        if (def.Id == Guid.Empty) def.Id = Guid.NewGuid();
        var existing = Get(def.Id);
        if (existing is not null)
        {
            def.CommandId ??= existing.CommandId;
            def.ActionId ??= existing.ActionId;
        }
        def.Name = def.Name.Trim();
        def.Aliases = def.Aliases.Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        def.Responses = def.Responses.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        Sync(def);
        lock (_lock) _defs[def.Id] = def;
        _config.SaveDocument(Kind, def.Id.ToString(), def.Name, def);
        return def;
    }

    public bool Delete(Guid id)
    {
        CustomCommandDef? def;
        lock (_lock)
        {
            if (!_defs.Remove(id, out def)) return false;
        }
        _syncing = true;
        try
        {
            if (def.CommandId is { } c) _config.DeleteCommand(c);
            if (def.ActionId is { } a) _config.DeleteAction(a);
        }
        finally { _syncing = false; }
        _config.DeleteDocument(Kind, id.ToString());
        return true;
    }

    /// <summary>Writes the command (words, cooldowns, roles) and creates the action when missing. An advanced action is never rewritten.</summary>
    private void Sync(CustomCommandDef def)
    {
        _syncing = true;
        try
        {
            var cmd = def.CommandId is { } cid ? _config.GetCommand(cid) : null;
            cmd ??= new CommandDef { Group = Group };
            def.CommandId = cmd.Id;
            cmd.Name = def.Name;
            cmd.Command = string.Join("\n", Words(def));
            cmd.Enabled = def.Enabled;
            cmd.GlobalCooldown = Math.Max(0, def.GlobalCooldown);
            cmd.UserCooldown = Math.Max(0, def.UserCooldown);
            cmd.PermittedGroups = [.. def.PermittedGroups];
            cmd.Set("minRole", ChatRoles.Rank(def.MinRole) == 0 ? "" : def.MinRole);
            _config.SaveCommand(cmd);

            if (def.ActionId is { } aid && _config.GetAction(aid) is { } action)
            {
                if (!def.Advanced && action.Name != def.Name)
                {
                    action.Name = def.Name;
                    _config.SaveAction(action);
                }
                return;
            }
            var trigger = new TriggerDef { Type = EventTypes.Id("CommandTriggered") };
            trigger.Set("commandId", cmd.Id);
            var created = new ActionDef
            {
                Name = def.Name, Group = Group, Triggers = [trigger],
                SubActions = [Seeding.Step(CustomCommandHandlers.Response, new { customCommandId = def.Id })],
            };
            _config.SaveAction(created);
            def.ActionId = created.Id;
            def.Advanced = false;
        }
        finally { _syncing = false; }
    }

    /// <summary>Creates definitions from "!command response" lines. Returns how many were added and any lines skipped.</summary>
    public (int Added, List<string> Skipped) Import(string text)
    {
        var added = 0;
        var skipped = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var space = line.IndexOf(' ');
            if (space <= 0 || !line.StartsWith('!'))
            {
                skipped.Add(line);
                continue;
            }
            var def = new CustomCommandDef { Name = line[..space], Responses = [line[(space + 1)..].Trim()] };
            if (Validate(def) is not null)
            {
                skipped.Add(line);
                continue;
            }
            Save(def);
            added++;
        }
        return (added, skipped);
    }

    // ── running ────────────────────────────────────────────────────────────

    /// <summary>Runs the command's response for a chat invocation: live check, cost, then the messages. Returns the lines sent.</summary>
    public async Task<List<string>> RespondAsync(CustomCommandDef def, IReadOnlyDictionary<string, object?> args, CancellationToken ct, bool send = true)
    {
        if (def.OnlyWhileLive && send && !await IsLiveAsync(ct)) return [];
        var userId = ChatRoles.Str(args, "userId");
        var user = ChatRoles.Str(args, "user");
        if (def.Cost > 0 && send && ChatRoles.Rank(args) < 4)
        {
            if (_db.GetPoints(LoyaltyService.Platform, userId) < def.Cost)
            {
                var msg = $"{user}, {def.Name} costs {def.Cost} {_loyalty.Currency}.";
                await SendAsync(def, msg, args, ct);
                return [msg];
            }
            _loyalty.Award(userId, user, -def.Cost);
        }

        var variants = def.Responses.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (variants.Count == 0) return [];
        var template = variants[Random(0, variants.Count)];
        var text = await ExpandAsync(def, template, args, increment: send, ct);
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!send) return lines;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0 && def.LineDelayMs > 0) await Delay(TimeSpan.FromMilliseconds(def.LineDelayMs));
            await SendAsync(def, lines[i], args, ct);
        }
        return lines;
    }

    private async Task SendAsync(CustomCommandDef def, string text, IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        if (def.ReplyMode == "announcement" && (await _mod.AnnounceAsync(text, def.Color, useBot: true)).Ok) return;
        var replyTo = def.ReplyMode == "reply" ? ChatRoles.Str(args, "msgId") : "";
        await _twitch.SendChatAsync(text, useBot: true, fallback: true, replyTo.Length > 0 ? replyTo : null, ct);
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z0-9]+)(?::(?<arg>[^}]*))?\}")]
    private static partial Regex TokenPattern();

    /// <summary>Expands the friendly tokens, then %args% and ~globals~.</summary>
    public async Task<string> ExpandAsync(CustomCommandDef def, string template, IReadOnlyDictionary<string, object?> args, bool increment, CancellationToken ct)
    {
        var input = ChatRoles.Str(args, "rawInput").Trim();
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var user = ChatRoles.Str(args, "user");
        var userId = ChatRoles.Str(args, "userId");
        ChannelInfo? channel = null;
        async Task<ChannelInfo> Channel() => channel ??= await GetChannelAsync(ct);

        var result = new System.Text.StringBuilder();
        var last = 0;
        foreach (Match m in TokenPattern().Matches(template))
        {
            result.Append(template, last, m.Index - last);
            last = m.Index + m.Length;
            var name = m.Groups["name"].Value.ToLowerInvariant();
            var arg = m.Groups["arg"].Success ? m.Groups["arg"].Value : null;
            string? value = name switch
            {
                "user" => user,
                "touser" => words.Length > 0 ? words[0].TrimStart('@') : user,
                "args" => input,
                "channel" => _auth.Get(TwitchRole.Broadcaster)?.DisplayName ?? "",
                "count" => (increment ? _db.IncrementCounter("customCommand", def.Id.ToString()) : _db.GetCounter("customCommand", def.Id.ToString()) + 1)
                    .ToString(CultureInfo.InvariantCulture),
                "random" => RandomRange(arg),
                "pick" when arg is not null => arg.Split('|') is { Length: > 0 } opts ? opts[Random(0, opts.Length)].Trim() : "",
                "points" => _db.GetPoints(LoyaltyService.Platform, userId).ToString(CultureInfo.InvariantCulture),
                "currency" => _loyalty.Currency,
                "uptime" => (await Channel()).StartedAt is { } started ? Tokens.Duration((DateTimeOffset.UtcNow - started).TotalSeconds) : "offline",
                "game" => (await Channel()).Game,
                "title" => (await Channel()).Title,
                "followage" => await FollowAgeAsync(arg == "touser" && words.Length > 0 ? words[0] : null, userId, ct),
                _ when int.TryParse(name, out var n) && n is >= 1 and <= 9 => n <= words.Length ? words[n - 1] : "",
                _ => null,
            };
            result.Append(value ?? m.Value);
        }
        result.Append(template, last, template.Length - last);
        return _templates.Parse(result.ToString(), args);
    }

    private string RandomRange(string? arg)
    {
        var (min, max) = (1, 100);
        if (arg is not null && arg.Split('-') is [var a, var b] && int.TryParse(a, out var lo) && int.TryParse(b, out var hi)) (min, max) = (Math.Min(lo, hi), Math.Max(lo, hi));
        return Random(min, max + 1).ToString(CultureInfo.InvariantCulture);
    }

    private sealed record ChannelInfo(string Title, string Game, DateTimeOffset? StartedAt);

    private async Task<ChannelInfo> GetChannelAsync(CancellationToken ct)
    {
        if (_auth.Get(TwitchRole.Broadcaster) is not { } b) return new ChannelInfo("", "", null);
        try
        {
            var ch = HelixClient.First(await _helix.GetAsync("channels", new Dictionary<string, string?> { ["broadcaster_id"] = b.UserId }, ct: ct));
            var stream = HelixClient.First(await _helix.GetAsync("streams", new Dictionary<string, string?> { ["user_id"] = b.UserId }, ct: ct));
            DateTimeOffset? started = stream is { } s && DateTimeOffset.TryParse(s.GetProperty("started_at").GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var t) ? t : null;
            return new ChannelInfo(ch?.GetProperty("title").GetString() ?? "", ch?.GetProperty("game_name").GetString() ?? "", started);
        }
        catch (Exception ex) when (ex is HelixException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogDebug(ex, "Channel info unavailable");
            return new ChannelInfo("", "", null);
        }
    }

    private async Task<string> FollowAgeAsync(string? login, string userId, CancellationToken ct)
    {
        if (_auth.Get(TwitchRole.Broadcaster) is not { } b) return "unknown";
        try
        {
            var id = userId;
            if (login is not null && await _helix.GetUserByLoginAsync(login, ct) is { } u) id = u.GetProperty("id").GetString() ?? userId;
            var f = HelixClient.First(await _helix.GetAsync("channels/followers", new Dictionary<string, string?> { ["broadcaster_id"] = b.UserId, ["user_id"] = id }, ct: ct));
            if (f is not { } follow || !DateTimeOffset.TryParse(follow.GetProperty("followed_at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since))
                return "not following";
            return Age(DateTimeOffset.UtcNow - since);
        }
        catch (Exception ex) when (ex is HelixException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogDebug(ex, "Follow age unavailable");
            return "unknown";
        }
    }

    internal static string Age(TimeSpan t)
    {
        var days = (int)t.TotalDays;
        if (days >= 365) return $"{days / 365} year{(days / 365 == 1 ? "" : "s")}{(days % 365 / 30 > 0 ? $", {days % 365 / 30} month{(days % 365 / 30 == 1 ? "" : "s")}" : "")}";
        if (days >= 30) return $"{days / 30} month{(days / 30 == 1 ? "" : "s")}{(days % 30 > 0 ? $", {days % 30} day{(days % 30 == 1 ? "" : "s")}" : "")}";
        if (days >= 1) return $"{days} day{(days == 1 ? "" : "s")}";
        return $"{Math.Max(1, (int)t.TotalHours)} hour{((int)t.TotalHours == 1 ? "" : "s")}";
    }

    private async Task<bool> IsLiveAsync(CancellationToken ct)
    {
        if (_loyalty.IsLive is { } known) return known;
        return (await GetChannelAsync(ct)).StartedAt is not null;
    }
}
