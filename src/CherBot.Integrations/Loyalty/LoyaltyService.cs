using System.Collections.Concurrent;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Events;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Loyalty;

/// <summary>Loyalty settings, editable at runtime (kv "loyalty.settings").</summary>
public sealed class LoyaltySettings
{
    public bool Enabled { get; set; } = true;
    public string CurrencyName { get; set; } = "points";

    public int ChatPoints { get; set; } = 1;
    /// <summary>Minimum seconds between chat-point awards for the same user (0 = every message).</summary>
    public int ChatCooldownSeconds { get; set; }
    public int FirstWordPoints { get; set; } = 10;

    public bool WatchEnabled { get; set; } = true;
    public int WatchIntervalMinutes { get; set; } = 10;
    public int WatchPoints { get; set; } = 5;
    public bool WatchOnlyWhenLive { get; set; } = true;
    /// <summary>0 = everyone in the chatter list; above 0 = only viewers who chatted within this many minutes.</summary>
    public int WatchActiveMinutes { get; set; }

    public double SubscriberMultiplier { get; set; } = 1.0;
    public double VipMultiplier { get; set; } = 1.0;
    public double ModeratorMultiplier { get; set; } = 1.0;

    public int FollowBonus { get; set; } = 50;
    public int SubBonus { get; set; } = 250;
    public int ResubBonus { get; set; } = 250;
    public int GiftSubBonusPerSub { get; set; } = 100;
    public int CheerBonusPer100Bits { get; set; } = 50;
    public int RaidBonus { get; set; } = 100;
}

/// <summary>
/// Awards loyalty points: per chat message (with an optional cooldown), a first-message-of-stream bonus, watch time while
/// live, and bonuses for follows, subs, gifts, cheers and raids. Role multipliers apply to chat and watch points.
/// </summary>
public sealed class LoyaltyService : BackgroundService
{
    public const string Platform = "twitch";
    private const string Key = "loyalty.settings";

    private readonly TwitchService _twitch;
    private readonly EventBus _bus;
    private readonly CherBotDb _db;
    private readonly HelixClient _helix;
    private readonly TwitchAuth _auth;
    private readonly ILogger<LoyaltyService> _log;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastChatAward = new();
    private LoyaltySettings _settings;
    private bool? _live;
    private IDisposable? _sub;

    public LoyaltyService(TwitchService twitch, EventBus bus, CherBotDb db, HelixClient helix, TwitchAuth auth, CommunityOptions community, ILogger<LoyaltyService> log)
    {
        _twitch = twitch;
        _bus = bus;
        _db = db;
        _helix = helix;
        _auth = auth;
        _log = log;
        _settings = db.GetKv(Key) is { } json
            ? JsonSerializer.Deserialize<LoyaltySettings>(json, CherBotJson.Options) ?? new LoyaltySettings()
            : new LoyaltySettings { Enabled = community.Enabled, ChatPoints = community.ChatPoints, FirstWordPoints = community.FirstWordPoints };
    }

    public LoyaltySettings Settings => _settings;
    public string Currency => string.IsNullOrWhiteSpace(_settings.CurrencyName) ? "points" : _settings.CurrencyName;
    public bool? IsLive => _live;

    public void Save(LoyaltySettings settings)
    {
        _settings = settings;
        _db.SetKv(Key, JsonSerializer.Serialize(settings, CherBotJson.Options));
    }

    public long Award(string userId, string userName, long amount) =>
        amount == 0 || userId.Length == 0 ? _db.GetPoints(Platform, userId) : _db.AddPoints(Platform, userId, userName, amount);

    private double Multiplier(IReadOnlyDictionary<string, object?> d)
    {
        var m = 1.0;
        if (d.GetValueOrDefault("isSubscribed") is true) m = Math.Max(m, _settings.SubscriberMultiplier);
        if (d.GetValueOrDefault("isVip") is true) m = Math.Max(m, _settings.VipMultiplier);
        if (d.GetValueOrDefault("isModerator") is true) m = Math.Max(m, _settings.ModeratorMultiplier);
        return m;
    }

    private static string S(IReadOnlyDictionary<string, object?> d, string key) => CherBotJson.Format(d.GetValueOrDefault(key));

    /// <summary>Runs synchronously before a chat message is published, so commands in the same message see the new balance.</summary>
    internal void OnChat(BotEvent chat)
    {
        if (!_settings.Enabled || chat.Source != "Twitch" || chat.Type != "ChatMessage") return;
        if (chat.Data.GetValueOrDefault("filtered") is true || chat.Data.GetValueOrDefault("isBot") is true) return;
        var userId = S(chat.Data, "userId");
        if (userId.Length == 0) return;

        var first = chat.Data.GetValueOrDefault("firstWordOfStream") is true;
        var now = DateTimeOffset.UtcNow;
        if (!first && _settings.ChatCooldownSeconds > 0 &&
            _lastChatAward.TryGetValue(userId, out var last) && (now - last).TotalSeconds < _settings.ChatCooldownSeconds)
            return;
        _lastChatAward[userId] = now;

        var basePoints = first ? _settings.FirstWordPoints : _settings.ChatPoints;
        var amount = (long)Math.Round(basePoints * Multiplier(chat.Data));
        Award(userId, S(chat.Data, "user"), amount);
    }

    private Task OnEventAsync(BotEvent ev)
    {
        if (ev.Source != "Twitch") return Task.CompletedTask;
        switch (ev.Type)
        {
            case "StreamOnline": _live = true; return Task.CompletedTask;
            case "StreamOffline": _live = false; return Task.CompletedTask;
        }
        if (!_settings.Enabled || ev.Data.GetValueOrDefault("anonymous") is true) return Task.CompletedTask;
        var userId = S(ev.Data, "userId");
        var user = S(ev.Data, "user");
        long bonus = ev.Type switch
        {
            "Follow" => _settings.FollowBonus,
            "Sub" => _settings.SubBonus,
            "ReSub" => _settings.ResubBonus,
            "GiftSub" when ev.Data.GetValueOrDefault("fromGiftBomb") is not true => _settings.GiftSubBonusPerSub,
            "GiftBomb" => (long)_settings.GiftSubBonusPerSub * (CherBotJson.TryNumber(ev.Data.GetValueOrDefault("gifts"), out var g) ? (long)g : 0),
            "Cheer" => CherBotJson.TryNumber(ev.Data.GetValueOrDefault("bits"), out var b) ? (long)(b / 100 * _settings.CheerBonusPer100Bits) : 0,
            "Raid" => _settings.RaidBonus,
            _ => 0,
        };
        if (bonus > 0 && userId.Length > 0) Award(userId, user, bonus);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _twitch.BeforePublish += OnChat;
        _sub = _bus.Subscribe("loyalty", OnEventAsync, e => e.Source == "Twitch");
        var lastWatch = DateTimeOffset.UtcNow;
        using var tick = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await tick.WaitForNextTickAsync(stoppingToken))
            {
                if (!_settings.Enabled || !_settings.WatchEnabled || _settings.WatchPoints == 0) continue;
                if (DateTimeOffset.UtcNow - lastWatch < TimeSpan.FromMinutes(Math.Max(1, _settings.WatchIntervalMinutes))) continue;
                lastWatch = DateTimeOffset.UtcNow;
                try { await AwardWatchTimeAsync(stoppingToken); }
                catch (Exception ex) { _log.LogWarning(ex, "Watch-time award failed"); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _twitch.BeforePublish -= OnChat;
            _sub?.Dispose();
        }
    }

    /// <summary>Gives watch-time points to the current audience. Returns how many viewers were paid.</summary>
    public async Task<int> AwardWatchTimeAsync(CancellationToken ct)
    {
        if (_settings.WatchOnlyWhenLive && !await IsLiveAsync(ct)) return 0;
        var viewers = await AudienceAsync(_settings.WatchActiveMinutes, ct);
        foreach (var v in viewers)
        {
            var multiplier = v.Role switch { "Moderator" or "Broadcaster" => _settings.ModeratorMultiplier, "VIP" => _settings.VipMultiplier, _ => 1.0 };
            if (v.Subscribed) multiplier = Math.Max(multiplier, _settings.SubscriberMultiplier);
            Award(v.Id, v.Display, (long)Math.Round(_settings.WatchPoints * multiplier));
        }
        if (viewers.Count > 0) _log.LogInformation("Watch time: {Count} viewers earned {Points} {Currency}", viewers.Count, _settings.WatchPoints, Currency);
        return viewers.Count;
    }

    /// <summary>Gives every viewer currently in chat the same amount. Returns how many were paid.</summary>
    public async Task<int> GiveAllAsync(long amount, CancellationToken ct)
    {
        var viewers = await AudienceAsync(0, ct);
        foreach (var v in viewers) Award(v.Id, v.Display, amount);
        return viewers.Count;
    }

    private async Task<bool> IsLiveAsync(CancellationToken ct)
    {
        if (_live is { } known) return known;
        if (_auth.Get(TwitchRole.Broadcaster) is not { } b) return false;
        try
        {
            var res = await _helix.GetAsync("streams", new Dictionary<string, string?> { ["user_id"] = b.UserId }, ct: ct);
            _live = HelixClient.First(res) is not null;
        }
        catch (HelixException) { _live = false; }
        return _live ?? false;
    }

    /// <summary>Viewers to pay: recent chatters when <paramref name="activeMinutes"/> is set, otherwise Helix chatters (falling back to recent chatters).</summary>
    private async Task<List<CherBot.Core.Platform.ActiveViewer>> AudienceAsync(int activeMinutes, CancellationToken ct)
    {
        var recent = _twitch.GetActiveViewers();
        if (activeMinutes > 0)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-activeMinutes);
            return recent.Where(v => v.LastActive >= cutoff).ToList();
        }
        if (_auth.Get(TwitchRole.Broadcaster) is not { } b) return recent.ToList();
        try
        {
            var list = new List<CherBot.Core.Platform.ActiveViewer>();
            string? cursor = null;
            do
            {
                var res = await _helix.GetAsync("chat/chatters", new Dictionary<string, string?>
                {
                    ["broadcaster_id"] = b.UserId, ["moderator_id"] = b.UserId, ["first"] = "1000", ["after"] = cursor,
                }, ct: ct);
                foreach (var c in res.GetProperty("data").EnumerateArray())
                {
                    var id = c.GetProperty("user_id").GetString()!;
                    var known = recent.FirstOrDefault(r => r.Id == id);
                    list.Add(known ?? new CherBot.Core.Platform.ActiveViewer(Platform, id, c.GetProperty("user_login").GetString()!,
                        c.GetProperty("user_name").GetString()!, "Viewer", false, DateTimeOffset.UtcNow));
                }
                cursor = res.TryGetProperty("pagination", out var p) && p.TryGetProperty("cursor", out var cur) ? cur.GetString() : null;
            } while (!string.IsNullOrEmpty(cursor));
            return list.Where(v => v.Id != b.UserId).ToList();
        }
        catch (Exception ex) when (ex is HelixException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogDebug(ex, "Chatters list unavailable; using recent chatters");
            return recent.ToList();
        }
    }
}
