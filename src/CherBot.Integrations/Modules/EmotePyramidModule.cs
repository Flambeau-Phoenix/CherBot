using System.Collections.Concurrent;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Integrations.Common;
using CherBot.Integrations.Loyalty;
using CherBot.Integrations.Moderation;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Modules;

public sealed class EmotePyramidSettings : IModuleSettings
{
    public bool Enabled { get; set; }
    public int MinWidth { get; set; } = 3;
    /// <summary>0 = no limit.</summary>
    public int MaxWidth { get; set; } = 10;
    /// <summary>Only emotes count; off = any repeated word.</summary>
    public bool EmotesOnly { get; set; } = true;
    public bool AnyUserCanContinue { get; set; }
    public string SuccessMessage { get; set; } = "{user} built a {width}-wide {emote} pyramid! 🏆";
    /// <summary>Empty = say nothing when a pyramid is broken.</summary>
    public string BrokenMessage { get; set; } = "";
    public long RewardBase { get; set; }
    public long RewardPerWidth { get; set; }
    /// <summary>off, delete or timeout: for channels that don't want pyramids.</summary>
    public string BlockMode { get; set; } = "off";
    public int BlockFromWidth { get; set; } = 3;
    public int BlockTimeoutSeconds { get; set; } = 10;
}

/// <summary>Detects emote pyramids (E, E E, E E E, E E, E) built from consecutive chat messages; cheers, rewards or blocks them.</summary>
public sealed class EmotePyramidModule : BotModule<EmotePyramidSettings>, IHostedService
{
    private sealed class Build
    {
        public required string UserId { get; init; }
        public required string User { get; init; }
        public required string Word { get; init; }
        public int Current { get; set; } = 1;
        public int Peak { get; set; } = 1;
        public bool Descending { get; set; }
    }

    private readonly EventBus _bus;
    private readonly TwitchService _twitch;
    private readonly ChatModeration _mod;
    private readonly LoyaltyService _loyalty;
    private readonly TemplateEngine _templates;
    private readonly ILogger<EmotePyramidModule> _log;
    private readonly object _lock = new();
    private Build? _build;
    private IDisposable? _sub;

    public EmotePyramidModule(CherBotDb db, EventBus bus, TwitchService twitch, ChatModeration mod, LoyaltyService loyalty, TemplateEngine templates,
        ILogger<EmotePyramidModule> log) : base(db)
    {
        _bus = bus;
        _twitch = twitch;
        _mod = mod;
        _loyalty = loyalty;
        _templates = templates;
        _log = log;
    }

    public override string Id => "emote-pyramid";
    public override string Name => "Emote pyramids";
    public override string Description => "Celebrate (or block) emote pyramids built in chat, with optional point rewards.";
    public override string Summary => Settings.BlockMode != "off" ? $"Blocking pyramids ({Settings.BlockMode})" : $"Celebrating pyramids of width {Settings.MinWidth}+";
    public int Completed { get; private set; }
    public ConcurrentQueue<string> Sent { get; } = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sub = _bus.Subscribe("emote-pyramid", OnChatAsync, e => e.Source == "Twitch" && e.Type == "ChatMessage");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sub?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The repeated word when every token of the message is the same (and an emote, when required); otherwise null.</summary>
    internal static (string Word, int Count)? Row(IReadOnlyDictionary<string, object?> d, bool emotesOnly)
    {
        var words = ChatRoles.Str(d, "message").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Any(w => w != words[0])) return null;
        if (emotesOnly && d.GetValueOrDefault("emotes") is IEnumerable<string> emotes && !emotes.Contains(words[0])) return null;
        return (words[0], words.Length);
    }

    internal async Task OnChatAsync(BotEvent chat)
    {
        if (!Enabled || chat.Data.GetValueOrDefault("filtered") is true || chat.Data.GetValueOrDefault("isBot") is true) return;
        var s = Settings;
        var d = chat.Data;
        var userId = ChatRoles.Str(d, "userId");
        var user = ChatRoles.Str(d, "user");
        var row = Row(d, s.EmotesOnly);

        Build? completed = null, broken = null;
        var blockRow = false;
        lock (_lock)
        {
            var b = _build;
            var sameUser = b is not null && b.UserId == userId;
            if (b is not null && !sameUser && !s.AnyUserCanContinue)
            {
                if (b.Peak >= 2) broken = b;
                b = _build = null;
            }

            if (b is null || row is null || row.Value.Word != b.Word)
            {
                if (b is not null && b.Peak >= 2 && !sameUser) broken ??= b;
                _build = row is { Count: 1 } r ? new Build { UserId = userId, User = user, Word = r.Word } : null;
            }
            else
            {
                var n = row.Value.Count;
                if (!b.Descending && n == b.Current + 1 && (s.MaxWidth <= 0 || n <= s.MaxWidth))
                {
                    b.Current = b.Peak = n;
                    blockRow = s.BlockMode != "off" && n >= s.BlockFromWidth;
                }
                else if (n == b.Current - 1 && b.Current >= 2)
                {
                    b.Descending = true;
                    b.Current = n;
                    blockRow = s.BlockMode != "off" && b.Peak >= s.BlockFromWidth;
                    if (n == 1)
                    {
                        if (b.Peak >= Math.Max(2, s.MinWidth)) completed = b;
                        _build = null;
                    }
                }
                else _build = n == 1 ? new Build { UserId = userId, User = user, Word = row.Value.Word } : null;
            }
        }

        if (blockRow) await BlockAsync(d, userId, s);
        if (broken is not null)
        {
            var tokens = new Dictionary<string, object?> { ["user"] = broken.User, ["emote"] = broken.Word, ["width"] = broken.Peak, ["breaker"] = user };
            await SayAsync(s.BrokenMessage, tokens, d);
            Raise("pyramid.broken", tokens);
        }
        if (completed is not null && s.BlockMode == "off")
        {
            Completed++;
            var reward = s.RewardBase + s.RewardPerWidth * completed.Peak;
            if (reward > 0) _loyalty.Award(completed.UserId, completed.User, reward);
            var tokens = new Dictionary<string, object?>
            {
                ["user"] = completed.User, ["emote"] = completed.Word, ["width"] = completed.Peak, ["points"] = reward, ["currency"] = _loyalty.Currency,
            };
            await SayAsync(s.SuccessMessage, tokens, d);
            Raise("pyramid.completed", tokens);
        }
    }

    private async Task BlockAsync(IReadOnlyDictionary<string, object?> d, string userId, EmotePyramidSettings s)
    {
        var result = s.BlockMode == "timeout"
            ? await _mod.TimeoutAsync(userId, Math.Max(1, s.BlockTimeoutSeconds), "Emote pyramids are not allowed")
            : await _mod.DeleteMessageAsync(ChatRoles.Str(d, "msgId"));
        Db.AddModLog("twitch", userId, ChatRoles.Str(d, "user"), "emote-pyramid", ChatRoles.Str(d, "message"), s.BlockMode, result.Ok, result.Error ?? "");
    }

    private async Task SayAsync(string template, Dictionary<string, object?> tokens, IReadOnlyDictionary<string, object?> args)
    {
        if (string.IsNullOrWhiteSpace(template)) return;
        var text = _templates.Parse(Tokens.Fill(template, tokens), args);
        Sent.Enqueue(text);
        while (Sent.Count > 50) Sent.TryDequeue(out _);
        try { await _twitch.SendChatAsync(text, useBot: true, fallback: true, null, CancellationToken.None); }
        catch (Exception ex) { _log.LogWarning(ex, "Pyramid message failed"); }
    }

    private void Raise(string eventName, Dictionary<string, object?> tokens)
    {
        var data = new Dictionary<string, object?>(tokens, StringComparer.OrdinalIgnoreCase) { ["eventName"] = eventName };
        data["args"] = new Dictionary<string, object?>(data, StringComparer.OrdinalIgnoreCase);
        _bus.Publish(BotEvent.FromTypeName("CustomEvent", data));
    }
}
