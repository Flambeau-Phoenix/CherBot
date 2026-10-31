using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Moderation;
using CherBot.Integrations.Common;
using CherBot.Integrations.Moderation;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Modules;

/// <summary>Settings shared by every filter: whether it runs, what it does, and who is exempt.</summary>
public class FilterRule
{
    public bool Enabled { get; set; }
    /// <summary>delete, warn (chat message), timeout, or log (only blocks commands and points).</summary>
    public string Action { get; set; } = "delete";
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>Text for {reason} in the warning and the timeout reason.</summary>
    public string Reason { get; set; } = "";
    /// <summary>Lowest role that is exempt: subscriber, vip, moderator, or none. Empty = the module default.</summary>
    public string ExemptRole { get; set; } = "";
}

public sealed class LinkFilter : FilterRule
{
    public List<string> AllowedDomains { get; set; } = ["twitch.tv"];
    public int PermitSeconds { get; set; } = 60;
}

public sealed class CapsFilter : FilterRule
{
    public int MinLength { get; set; } = 12;
    public int MaxPercent { get; set; } = 70;
}

public sealed class SymbolsFilter : FilterRule
{
    public int MinLength { get; set; } = 12;
    public int MaxPercent { get; set; } = 50;
}

public sealed class RepeatFilter : FilterRule
{
    /// <summary>Most identical characters allowed in a row.</summary>
    public int MaxRepeat { get; set; } = 10;
}

public sealed class LengthFilter : FilterRule
{
    public int MaxLength { get; set; } = 400;
}

public sealed class EmoteFilter : FilterRule
{
    public int MaxEmotes { get; set; } = 15;
}

public sealed class WordsFilter : FilterRule
{
    /// <summary>Plain words or phrases, wildcards (<c>bad*</c>), or regexes written as <c>/pattern/</c>.</summary>
    public List<string> Words { get; set; } = [];
}

public sealed class DuplicateFilter : FilterRule
{
    public int WindowSeconds { get; set; } = 30;
}

public sealed class ZalgoFilter : FilterRule
{
    public int MaxCombining { get; set; } = 6;
}

public sealed class EscalationStep
{
    public string Action { get; set; } = "warn";
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class SpamFilterSettings : IModuleSettings
{
    public bool Enabled { get; set; }
    /// <summary>Lowest role that is exempt from every filter unless the filter says otherwise (none = nobody but the broadcaster and bot).</summary>
    public string ExemptRole { get; set; } = "subscriber";
    public List<string> PermittedUsers { get; set; } = [];
    public List<string> PermittedGroups { get; set; } = [];
    /// <summary>Chat message for the warn action. Tokens: {user}, {reason}, {filter}.</summary>
    public string WarnMessage { get; set; } = "@{user}, {reason}";

    /// <summary>When on, punishments follow the ladder by strike count instead of each filter's own action.</summary>
    public bool Escalation { get; set; }
    public List<EscalationStep> Ladder { get; set; } =
    [
        new() { Action = "warn" },
        new() { Action = "timeout", TimeoutSeconds = 10 },
        new() { Action = "timeout", TimeoutSeconds = 600 },
    ];
    public int StrikeDecayMinutes { get; set; } = 30;

    public LinkFilter Links { get; set; } = new() { Enabled = true, Reason = "please ask a moderator before posting links." };
    public CapsFilter Caps { get; set; } = new() { Reason = "please don't shout." };
    public SymbolsFilter Symbols { get; set; } = new() { Reason = "too many symbols." };
    public RepeatFilter Repeats { get; set; } = new() { Reason = "please don't spam characters." };
    public LengthFilter Length { get; set; } = new() { Reason = "that message is too long." };
    public EmoteFilter Emotes { get; set; } = new() { Reason = "too many emotes." };
    public WordsFilter Words { get; set; } = new() { Enabled = true, Reason = "watch your language.", Action = "delete", ExemptRole = "none" };
    public DuplicateFilter Duplicates { get; set; } = new() { Reason = "please don't repeat yourself." };
    public ZalgoFilter Zalgo { get; set; } = new() { Reason = "no zalgo text, please." };
}

/// <summary>
/// Spam filter: checks every chat message before anything else sees it (links with !permit, caps, symbols, repeated
/// characters, length, emote spam, banned words, duplicates, zalgo). Blocked messages are marked filtered and punished
/// by deleting, warning or timing out, optionally on an escalation ladder. Every action is written to the moderation log.
/// </summary>
public sealed partial class SpamFilterModule : BotModule<SpamFilterSettings>, IChatModerator, IHostedService
{
    public const string PermitCommand = "!permit";
    public const string Group = "CherBot Moderation";
    private const string SeedKey = "seed.spamfilter.v1";

    private readonly ChatModerationPipeline _pipeline;
    private readonly ChatModeration _mod;
    private readonly TwitchService _twitch;
    private readonly TwitchAuth _auth;
    private readonly ConfigService _config;
    private readonly ILogger<SpamFilterModule> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _permits = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Last)> _strikes = new();
    private readonly ConcurrentDictionary<string, List<(string Text, DateTimeOffset At)>> _recent = new();
    private readonly ConcurrentDictionary<string, Regex?> _wordRegex = new();

    public SpamFilterModule(CherBotDb db, ChatModerationPipeline pipeline, ChatModeration mod, TwitchService twitch, TwitchAuth auth, ConfigService config,
        ILogger<SpamFilterModule> log) : base(db)
    {
        _pipeline = pipeline;
        _mod = mod;
        _twitch = twitch;
        _auth = auth;
        _config = config;
        _log = log;
    }

    public override string Id => "spam-filter";
    public override string Name => "Spam filter";
    public override string Description => "Blocks links, caps, symbol and emote spam, banned words, duplicates and zalgo before commands run.";
    public override string Summary => $"{ActiveFilters().Count()} filters on{(Settings.Escalation ? ", escalation on" : "")}";
    public ConcurrentQueue<string> Sent { get; } = new();
    /// <summary>The most recent punishment (for tests).</summary>
    public Task LastPunishment { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Db.GetKv(SeedKey) is null)
        {
            Community.Seeding.CommandAction(_config, Group, PermitCommand, PermitCommand,
                [Community.Seeding.Step(ModuleHandlers.Permit), Community.Seeding.Reply()], enabled: Enabled,
                configure: c => { c.Set("minRole", "moderator"); c.GlobalCooldown = 0; c.UserCooldown = 0; });
            Db.SetKv(SeedKey, "true");
        }
        _pipeline.Register(this);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _pipeline.Unregister(this);
        return Task.CompletedTask;
    }

    /// <summary>!permit enables and disables together with the module.</summary>
    protected override void OnSaved()
    {
        if (_config.Commands.FirstOrDefault(c => c.Group == Group && c.Name == PermitCommand) is { } cmd && cmd.Enabled != Enabled)
        {
            cmd.Enabled = Enabled;
            _config.SaveCommand(cmd);
        }
    }

    private IEnumerable<(string Name, FilterRule Rule)> AllFilters()
    {
        var s = Settings;
        yield return ("words", s.Words);
        yield return ("links", s.Links);
        yield return ("zalgo", s.Zalgo);
        yield return ("length", s.Length);
        yield return ("caps", s.Caps);
        yield return ("symbols", s.Symbols);
        yield return ("repeats", s.Repeats);
        yield return ("emotes", s.Emotes);
        yield return ("duplicates", s.Duplicates);
    }

    private IEnumerable<(string Name, FilterRule Rule)> ActiveFilters() => AllFilters().Where(f => f.Rule.Enabled);

    /// <summary>Lets <paramref name="login"/> post one or more links for the link filter's permit window.</summary>
    public int Permit(string login)
    {
        var seconds = Math.Max(5, Settings.Links.PermitSeconds);
        _permits[login.Trim().TrimStart('@').ToLowerInvariant()] = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return seconds;
    }

    // ── checks ─────────────────────────────────────────────────────────────

    public ChatVerdict? Check(BotEvent chat)
    {
        if (!Enabled) return null;
        if (Evaluate(chat.Data) is not { } hit) return null;
        var verdict = new ChatVerdict(hit.Name, hit.Rule.Reason.Length > 0 ? hit.Rule.Reason : hit.Name);
        LastPunishment = Task.Run(() => PunishAsync(chat.Data, hit.Name, hit.Rule, verdict.Reason));
        return verdict;
    }

    /// <summary>The first filter the message breaks, or null. Pure apart from the duplicate history.</summary>
    public (string Name, FilterRule Rule)? Evaluate(IReadOnlyDictionary<string, object?> d)
    {
        var s = Settings;
        if (ChatRoles.Flag(d, "isBroadcaster") || ChatRoles.Flag(d, "isBot")) return null;
        var userId = ChatRoles.Str(d, "userId");
        if (_auth.Get(TwitchRole.Bot)?.UserId is { } botId && botId == userId) return null;
        if (IsPermittedUser(d)) return null;

        var message = ChatRoles.Str(d, "message");
        var stripped = d.ContainsKey("messageStripped") ? ChatRoles.Str(d, "messageStripped") : message;
        var rank = ChatRoles.Rank(d);

        foreach (var (name, rule) in ActiveFilters())
        {
            var exempt = rule.ExemptRole.Length > 0 ? rule.ExemptRole : s.ExemptRole;
            if (!string.Equals(exempt, "none", StringComparison.OrdinalIgnoreCase) && rank >= Math.Max(1, ChatRoles.Rank(exempt))) continue;
            var broken = name switch
            {
                "words" => HasBannedWord(message, s.Words.Words),
                "links" => HasForbiddenLink(message, ChatRoles.Str(d, "userName"), s.Links),
                "zalgo" => message.Count(c => CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) > s.Zalgo.MaxCombining,
                "length" => message.Length > s.Length.MaxLength,
                "caps" => CapsPercent(stripped, s.Caps.MinLength) > s.Caps.MaxPercent,
                "symbols" => SymbolPercent(stripped, s.Symbols.MinLength) > s.Symbols.MaxPercent,
                "repeats" => s.Repeats.MaxRepeat > 0 && Regex.IsMatch(stripped, $@"(.)\1{{{s.Repeats.MaxRepeat},}}"),
                "emotes" => (d.GetValueOrDefault("emoteCount") is int n ? n : 0) > s.Emotes.MaxEmotes,
                "duplicates" => IsDuplicate(userId, message, s.Duplicates.WindowSeconds),
                _ => false,
            };
            if (broken) return (name, rule);
        }
        return null;
    }

    private bool IsPermittedUser(IReadOnlyDictionary<string, object?> d)
    {
        var s = Settings;
        var id = ChatRoles.Str(d, "userId");
        var login = ChatRoles.Str(d, "userName");
        if (s.PermittedUsers.Any(u => u.Equals(id, StringComparison.OrdinalIgnoreCase) || u.TrimStart('@').Equals(login, StringComparison.OrdinalIgnoreCase)))
            return true;
        return s.PermittedGroups.Count > 0 && Db.GetGroupUsers().Any(g =>
            s.PermittedGroups.Contains(g.Group, StringComparer.OrdinalIgnoreCase) && (g.UserId == id || string.Equals(g.Login, login, StringComparison.OrdinalIgnoreCase)));
    }

    internal static double CapsPercent(string text, int minLength)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < Math.Max(1, minLength)) return 0;
        return 100.0 * letters.Count(char.IsUpper) / letters.Count;
    }

    internal static double SymbolPercent(string text, int minLength)
    {
        var chars = text.Where(c => !char.IsWhiteSpace(c)).ToList();
        if (chars.Count < Math.Max(1, minLength)) return 0;
        return 100.0 * chars.Count(c => !char.IsLetterOrDigit(c)) / chars.Count;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s]+|\b(?:[a-z0-9-]+\.)+(?:com|net|org|io|gg|tv|me|co|ly|be|xyz|info|ru|de|uk|us|ca|link|app|dev|shop|site|online|live|to|cc|biz|gl|fm|club|store|fun|top|click)\b(?:/[^\s]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    internal static IEnumerable<string> LinkHosts(string message)
    {
        foreach (Match m in LinkPattern().Matches(message))
        {
            var text = m.Value;
            if (!text.Contains("://")) text = "http://" + text;
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri)) yield return uri.Host.ToLowerInvariant();
        }
    }

    private bool HasForbiddenLink(string message, string login, LinkFilter f)
    {
        var hosts = LinkHosts(message).ToList();
        if (hosts.Count == 0) return false;
        if (_permits.TryGetValue(login, out var until) && until > DateTimeOffset.UtcNow) return false;
        return hosts.Any(h => !f.AllowedDomains.Any(a =>
        {
            var domain = a.Trim().TrimStart('.').ToLowerInvariant();
            return domain.Length > 0 && (h == domain || h.EndsWith("." + domain, StringComparison.Ordinal));
        }));
    }

    private bool HasBannedWord(string message, List<string> words)
    {
        foreach (var w in words)
        {
            var entry = w.Trim();
            if (entry.Length == 0) continue;
            var rx = _wordRegex.GetOrAdd(entry, e =>
            {
                try
                {
                    if (e.Length > 2 && e.StartsWith('/') && e.EndsWith('/'))
                        return new Regex(e[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                    var pattern = string.Join(".*", e.Split('*').Select(Regex.Escape));
                    return new Regex($@"(?<![\p{{L}}\p{{N}}]){pattern}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                }
                catch (ArgumentException) { return null; }
            });
            try
            {
                if (rx?.IsMatch(message) == true) return true;
            }
            catch (RegexMatchTimeoutException) { }
        }
        return false;
    }

    private bool IsDuplicate(string userId, string message, int windowSeconds)
    {
        if (userId.Length == 0 || windowSeconds <= 0) return false;
        var now = DateTimeOffset.UtcNow;
        var text = string.Join(' ', message.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var list = _recent.GetOrAdd(userId, _ => []);
        lock (list)
        {
            list.RemoveAll(x => (now - x.At).TotalSeconds > windowSeconds);
            var dup = list.Any(x => x.Text == text);
            list.Add((text, now));
            return dup;
        }
    }

    // ── punishment ─────────────────────────────────────────────────────────

    /// <summary>The action for this offence: the filter's own, or the ladder step for the user's strike count.</summary>
    internal (string Action, int Seconds) ActionFor(string userId, FilterRule rule)
    {
        var s = Settings;
        if (!s.Escalation || s.Ladder.Count == 0 || userId.Length == 0) return (rule.Action, rule.TimeoutSeconds);
        var now = DateTimeOffset.UtcNow;
        var strikes = _strikes.AddOrUpdate(userId, _ => (1, now),
            (_, old) => (now - old.Last).TotalMinutes > Math.Max(1, s.StrikeDecayMinutes) ? (1, now) : (old.Count + 1, now));
        var step = s.Ladder[Math.Min(strikes.Count, s.Ladder.Count) - 1];
        return (step.Action, step.TimeoutSeconds);
    }

    private async Task PunishAsync(IReadOnlyDictionary<string, object?> d, string filter, FilterRule rule, string reason)
    {
        var userId = ChatRoles.Str(d, "userId");
        var user = ChatRoles.Str(d, "user");
        var (action, seconds) = ActionFor(userId, rule);
        ModResult result;
        try
        {
            switch (action)
            {
                case "timeout":
                    result = await _mod.TimeoutAsync(userId, Math.Max(1, seconds), reason);
                    break;
                case "warn":
                {
                    var del = await _mod.DeleteMessageAsync(ChatRoles.Str(d, "msgId"));
                    var text = Tokens.Fill(Settings.WarnMessage, new Dictionary<string, object?> { ["user"] = user, ["reason"] = reason, ["filter"] = filter });
                    Sent.Enqueue(text);
                    var sent = await _twitch.SendChatAsync(text, useBot: true, fallback: true, null, CancellationToken.None);
                    result = new ModResult(sent.Sent, sent.Error ?? del.Error);
                    break;
                }
                case "log":
                    result = new ModResult(true);
                    break;
                default:
                    action = "delete";
                    result = await _mod.DeleteMessageAsync(ChatRoles.Str(d, "msgId"));
                    break;
            }
        }
        catch (Exception ex)
        {
            result = new ModResult(false, ex.Message);
        }
        var label = action == "timeout" ? $"timeout {seconds}s" : action;
        Db.AddModLog("twitch", userId, user, filter, ChatRoles.Str(d, "message"), label, result.Ok, result.Error ?? "");
        if (!result.Ok) _log.LogInformation("Spam filter {Filter} on {User}: {Action} failed ({Error})", filter, user, label, result.Error);
    }
}
