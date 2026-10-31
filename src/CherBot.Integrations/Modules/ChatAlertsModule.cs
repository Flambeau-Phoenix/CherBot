using System.Collections.Concurrent;
using CherBot.Core;
using CherBot.Core.Engine;
using CherBot.Core.Events;
using CherBot.Integrations.Common;
using CherBot.Integrations.Moderation;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Modules;

public sealed class AlertSettings
{
    public bool Enabled { get; set; }
    /// <summary>One or more variants; one is picked at random.</summary>
    public List<string> Messages { get; set; } = [];
    /// <summary>Minimum bits, raid viewers, gift count or resub months (0 = any).</summary>
    public int MinAmount { get; set; }
    /// <summary>chat, reply or announcement.</summary>
    public string Mode { get; set; } = "chat";
    public string Color { get; set; } = "primary";
    public bool UseBot { get; set; } = true;
    public int CooldownSeconds { get; set; }
    public int UserCooldownSeconds { get; set; }
    /// <summary>Reward alerts only: reward ids to announce (empty = every reward).</summary>
    public List<string> RewardIds { get; set; } = [];
}

public sealed class ChatAlertsSettings : IModuleSettings
{
    public bool Enabled { get; set; }
    public Dictionary<string, AlertSettings> Alerts { get; set; } = ChatAlertsModule.Defaults();
}

/// <summary>Chat messages for follows, subs, gifts, cheers, raids and other channel events.</summary>
public sealed class ChatAlertsModule : BotModule<ChatAlertsSettings>, IHostedService
{
    /// <summary>Alert key → (event type, label, amount argument, sample data).</summary>
    public static readonly IReadOnlyList<AlertKind> Kinds =
    [
        new("follow", "Follow", "Follow", null, new() { ["user"] = "NewViewer" }),
        new("sub", "Subscription", "Sub", null, new() { ["user"] = "SubFan", ["tier"] = "tier 1" }),
        new("resub", "Resubscription", "ReSub", "cumulativeMonths", new() { ["user"] = "LoyalFan", ["tier"] = "tier 1", ["cumulativeMonths"] = 12, ["monthStreak"] = 6, ["message"] = "Love the streams!" }),
        new("giftSub", "Gift sub", "GiftSub", null, new() { ["user"] = "GenerousGifter", ["recipientUser"] = "LuckyViewer", ["tier"] = "tier 1", ["totalSubsGifted"] = 5 }),
        new("giftBomb", "Gift bomb", "GiftBomb", "gifts", new() { ["user"] = "GenerousGifter", ["gifts"] = 10, ["tier"] = "tier 1", ["totalGifts"] = 50 }),
        new("cheer", "Cheer", "Cheer", "bits", new() { ["user"] = "BitsDonor", ["bits"] = 500, ["message"] = "Cheer500 great play!" }),
        new("raid", "Raid", "Raid", "viewers", new() { ["user"] = "FriendlyStreamer", ["viewers"] = 42 }),
        new("firstWords", "First words", "FirstWord", null, new() { ["user"] = "Newcomer", ["message"] = "hello everyone!" }),
        new("reward", "Reward redemption", "RewardRedemption", "rewardCost", new() { ["user"] = "PointSpender", ["rewardName"] = "Hydrate", ["rewardCost"] = 500, ["rawInput"] = "" }),
        new("streamOnline", "Stream online", "StreamOnline", null, new() { ["user"] = "" }),
        new("streamOffline", "Stream offline", "StreamOffline", null, new() { ["user"] = "" }),
        new("shoutoutReceived", "Shoutout received", "ShoutoutReceived", "viewers", new() { ["user"] = "BigStreamer", ["viewers"] = 1200 }),
        new("adStarted", "Ad break started", "AdRun", "adLength", new() { ["adLength"] = 90 }),
    ];

    public sealed record AlertKind(string Key, string Label, string EventType, string? AmountArg, Dictionary<string, object?> Sample);

    public static Dictionary<string, AlertSettings> Defaults()
    {
        AlertSettings A(bool on, params string[] messages) => new() { Enabled = on, Messages = [.. messages] };
        return new Dictionary<string, AlertSettings>(StringComparer.OrdinalIgnoreCase)
        {
            ["follow"] = A(true, "Thanks for the follow, {user}! 💜", "Welcome aboard, {user}! Thanks for following!"),
            ["sub"] = A(true, "{user} just subscribed ({tier})! Thank you! 🎉"),
            ["resub"] = A(true, "{user} resubscribed for {months} months! Thank you for the support! 🎉"),
            ["giftSub"] = A(true, "{user} gifted a sub to {recipient}! 🎁"),
            ["giftBomb"] = A(true, "{user} is gifting {gifts} subs to the community! Thank you! 🎁🎁🎁"),
            ["cheer"] = A(true, "{user} cheered {bits} bits! Thank you! 💎"),
            ["raid"] = A(true, "Incoming raid! {user} is here with {viewers} viewers! Welcome, raiders! 🚀"),
            ["firstWords"] = A(false, "Welcome to the stream, {user}! 👋"),
            ["reward"] = A(false, "{user} redeemed {reward}!"),
            ["streamOnline"] = A(false, "We're live! Welcome in, everyone! 🎬"),
            ["streamOffline"] = A(false, "Thanks for watching! See you next stream. 💤"),
            ["shoutoutReceived"] = A(false, "Huge thanks to {user} for the shoutout! 🙌"),
            ["adStarted"] = A(false, "A {length}s ad break is starting. Grab a drink, we'll be right back!"),
        };
    }

    private readonly EventBus _bus;
    private readonly TwitchService _twitch;
    private readonly ChatModeration _mod;
    private readonly TemplateEngine _templates;
    private readonly ILogger<ChatAlertsModule> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();
    private IDisposable? _sub;

    public ChatAlertsModule(CherBotDb db, EventBus bus, TwitchService twitch, ChatModeration mod, TemplateEngine templates, ILogger<ChatAlertsModule> log) : base(db)
    {
        _bus = bus;
        _twitch = twitch;
        _mod = mod;
        _templates = templates;
        _log = log;
        // Alerts added in later versions get their defaults.
        foreach (var (k, v) in Defaults()) Settings.Alerts.TryAdd(k, v);
    }

    public override string Id => "chat-alerts";
    public override string Name => "Chat alerts";
    public override string Description => "Thank followers, subs, gifters, cheerers and raiders in chat.";
    public override string Summary => $"{Settings.Alerts.Count(a => a.Value.Enabled)} of {Kinds.Count} alerts on";

    /// <summary>Messages sent by the module (latest last), for tests and the UI.</summary>
    public ConcurrentQueue<string> Sent { get; } = new();
    public Func<int, int> Pick { get; set; } = max => Random.Shared.Next(max);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sub = _bus.Subscribe("chat-alerts", OnEventAsync, e => e.Source == "Twitch");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sub?.Dispose();
        return Task.CompletedTask;
    }

    internal async Task OnEventAsync(BotEvent ev)
    {
        if (!Enabled) return;
        var kind = Kinds.FirstOrDefault(k => k.EventType == ev.Type);
        if (kind is null || !Settings.Alerts.TryGetValue(kind.Key, out var alert) || !alert.Enabled) return;
        if (kind.Key == "giftSub" && ev.Data.GetValueOrDefault("fromGiftBomb") is true) return;
        if (kind.Key == "reward" && alert.RewardIds.Count > 0 && !alert.RewardIds.Contains(ChatRoles.Str(ev.Data, "rewardId"))) return;
        if (alert.MinAmount > 0 && kind.AmountArg is { } arg &&
            (!CherBotJson.TryNumber(ev.Data.GetValueOrDefault(arg), out var amount) || amount < alert.MinAmount)) return;

        var now = DateTimeOffset.UtcNow;
        var user = ChatRoles.Str(ev.Data, "userId");
        if (alert.CooldownSeconds > 0 && _last.TryGetValue(kind.Key, out var last) && (now - last).TotalSeconds < alert.CooldownSeconds) return;
        if (alert.UserCooldownSeconds > 0 && user.Length > 0 && _last.TryGetValue($"{kind.Key}:{user}", out var lastUser) &&
            (now - lastUser).TotalSeconds < alert.UserCooldownSeconds) return;
        _last[kind.Key] = now;
        if (user.Length > 0) _last[$"{kind.Key}:{user}"] = now;

        await SendAsync(alert, Render(alert, ev.Data), ChatRoles.Str(ev.Data, "msgId"));
    }

    /// <summary>Friendly tokens for an alert, from the event data.</summary>
    public static Dictionary<string, object?> TokensFor(IReadOnlyDictionary<string, object?> d)
    {
        object? G(string k) => d.GetValueOrDefault(k);
        var amount = G("bits") ?? G("gifts") ?? G("viewers") ?? G("cumulativeMonths") ?? G("rewardCost") ?? G("adLength");
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = G("user"), ["amount"] = amount, ["months"] = G("cumulativeMonths"), ["streak"] = G("monthStreak"), ["tier"] = G("tier"),
            ["viewers"] = G("viewers"), ["gifts"] = G("gifts"), ["bits"] = G("bits"), ["recipient"] = G("recipientUser"),
            ["reward"] = G("rewardName"), ["cost"] = G("rewardCost"), ["input"] = G("rawInput"), ["message"] = G("message"), ["length"] = G("adLength"),
            ["total"] = G("totalGifts") ?? G("totalSubsGifted"),
        };
    }

    /// <summary>Renders one random variant with the event data (tokens, then %args% and ~globals~).</summary>
    public string Render(AlertSettings alert, IReadOnlyDictionary<string, object?> data)
    {
        var variants = alert.Messages.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        if (variants.Count == 0) return "";
        var template = variants[Pick(variants.Count)];
        return _templates.Parse(Tokens.Fill(template, TokensFor(data)), data);
    }

    /// <summary>Renders an alert with sample data for the Test button; sends it when <paramref name="send"/> is set.</summary>
    public async Task<string?> TestAsync(string key, bool send)
    {
        if (Kinds.FirstOrDefault(k => string.Equals(k.Key, key, StringComparison.OrdinalIgnoreCase)) is not { } kind) return null;
        var alert = Settings.Alerts.GetValueOrDefault(kind.Key) ?? new AlertSettings();
        var text = Render(alert, kind.Sample);
        if (send) await SendAsync(alert, text, null);
        return text;
    }

    private async Task SendAsync(AlertSettings alert, string text, string? msgId)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Sent.Enqueue(text);
        while (Sent.Count > 50) Sent.TryDequeue(out _);
        try
        {
            if (alert.Mode == "announcement")
            {
                var res = await _mod.AnnounceAsync(text, alert.Color, alert.UseBot);
                if (res.Ok) return;
                _log.LogDebug("Announcement failed ({Error}); sending as chat", res.Error);
            }
            var replyTo = alert.Mode == "reply" && !string.IsNullOrEmpty(msgId) ? msgId : null;
            await _twitch.SendChatAsync(text, alert.UseBot, fallback: true, replyTo, CancellationToken.None);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Chat alert failed"); }
    }
}
