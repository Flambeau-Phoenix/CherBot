using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Core.Moderation;
using CherBot.Core.Platform;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Twitch;

public sealed record EventSubSubscriptionStatus(string Type, string Version, string Status, string? Error);

/// <summary>
/// Twitch connection: an EventSub WebSocket session (welcome, keepalive, reconnect, revocation) that creates the configured
/// subscriptions with the broadcaster token, plus chat sending through Helix with the bot or broadcaster account.
/// </summary>
public sealed class TwitchService : BackgroundService, IIntegrationStatus, IChatSender, IBroadcasterInfo, IActiveViewers, IEventCatalog
{
    public const string DefaultEventSubUrl = "wss://eventsub.wss.twitch.tv/ws";

    private readonly TwitchAuth _auth;
    private readonly HelixClient _helix;
    private readonly TwitchEventMapper _mapper;
    private readonly EventBus _bus;
    private readonly TwitchOptions _options;
    private readonly ConfigService _config;
    private readonly ILogger<TwitchService> _log;
    private readonly ConcurrentDictionary<string, ActiveViewer> _viewers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenMessages = new();
    private CancellationTokenSource? _sessionCts;
    private volatile string _state = "disconnected";
    private string? _sessionId;
    private string? _lastError;
    private DateTimeOffset? _connectedAt;
    private List<EventSubSubscriptionStatus> _subscriptions = [];

    /// <summary>Overridable for tests (Twitch CLI mock server).</summary>
    public string EventSubUrl { get; set; } = DefaultEventSubUrl;

    private readonly ChatModerationPipeline _moderation;

    public TwitchService(TwitchAuth auth, HelixClient helix, TwitchEventMapper mapper, EventBus bus, TwitchOptions options, ConfigService config,
        ChatModerationPipeline moderation, ILogger<TwitchService> log)
    {
        _moderation = moderation;
        _auth = auth;
        _helix = helix;
        _mapper = mapper;
        _bus = bus;
        _options = options;
        _config = config;
        _log = log;
        _mapper.RewardSkipsQueue = id => RewardSettings().TryGetValue(id, out var s) && s.SkipQueue;
        _auth.TokenChanged += role =>
        {
            if (role == TwitchRole.Broadcaster) Reconnect();
        };
    }

    public sealed record RewardSetting(string RewardId, bool SkipQueue, string? Group = null);

    public Dictionary<string, RewardSetting> RewardSettings() =>
        _config.GetDocuments<RewardSetting>("twitch-reward").ToDictionary(r => r.RewardId);

    public void SaveRewardSetting(RewardSetting setting) => _config.SaveDocument("twitch-reward", setting.RewardId, setting.RewardId, setting);

    public string Platform => "twitch";
    public bool IsConnected => _state == "connected";

    /// <summary>Drops the current EventSub session so the loop reconnects (after a token change, or on request).</summary>
    public void Reconnect()
    {
        _sessionCts?.Cancel();
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.AutoConnect)
        {
            _state = "disabled";
            return;
        }
        _ = PresentViewersLoopAsync(stoppingToken);
        var attempt = 0;
        string? reconnectUrl = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_auth.ClientId is null || _auth.Get(TwitchRole.Broadcaster) is null)
            {
                _state = _auth.ClientId is null ? "not configured" : "not linked";
                await WaitForWakeAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            var url = reconnectUrl ?? EventSubUrl;
            reconnectUrl = null;

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            string? next = null;
            try
            {
                _state = "connecting";
                next = await RunSessionAsync(url, _sessionCts.Token);
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("Twitch EventSub session restarting");
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _log.LogWarning(ex, "Twitch EventSub session failed");
            }
            finally
            {
                _state = "disconnected";
                _sessionId = null;
                _bus.Publish(BotEvent.FromTypeName("TwitchBroadcasterChatDisconnected", new Dictionary<string, object?>()));
            }

            if (next is not null)
            {
                reconnectUrl = next;
                continue;
            }
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempt++, 6))));
            await WaitForWakeAsync(delay, stoppingToken);
        }
    }

    private readonly SemaphoreSlim _wake = new(0);

    private async Task WaitForWakeAsync(TimeSpan max, CancellationToken ct)
    {
        try { await _wake.WaitAsync(max, ct); }
        catch (OperationCanceledException) { /* stopping */ }
    }

    /// <summary>Runs one WebSocket session. Returns a reconnect URL when Twitch asks us to move, otherwise null.</summary>
    private async Task<string?> RunSessionAsync(string url, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        var keepalive = TimeSpan.FromSeconds(30);
        var buffer = new byte[64 * 1024];
        using var msg = new MemoryStream();
        var reconnectingTo = url != EventSubUrl;

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(keepalive + TimeSpan.FromSeconds(10));
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning("Twitch EventSub keepalive missed; reconnecting");
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                _lastError = $"closed by Twitch: {(int?)ws.CloseStatus} {ws.CloseStatusDescription}";
                _log.LogWarning("Twitch EventSub closed: {Status} {Reason}", ws.CloseStatus, ws.CloseStatusDescription);
                return null;
            }
            msg.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            using var doc = JsonDocument.Parse(msg.ToArray());
            msg.SetLength(0);
            var root = doc.RootElement;
            var metadata = root.GetProperty("metadata");
            var type = metadata.GetProperty("message_type").GetString();
            var payload = root.GetProperty("payload");

            switch (type)
            {
                case "session_welcome":
                {
                    var session = payload.GetProperty("session");
                    _sessionId = session.GetProperty("id").GetString();
                    if (session.TryGetProperty("keepalive_timeout_seconds", out var k) && k.ValueKind == JsonValueKind.Number)
                        keepalive = TimeSpan.FromSeconds(k.GetInt32());
                    _state = "connected";
                    _connectedAt = DateTimeOffset.UtcNow;
                    _lastError = null;
                    _log.LogInformation("Twitch EventSub connected (session {Session})", _sessionId);
                    if (!reconnectingTo) await CreateSubscriptionsAsync(_sessionId!, ct);
                    _bus.Publish(BotEvent.FromTypeName("TwitchBroadcasterChatConnected", new Dictionary<string, object?>()));
                    break;
                }
                case "session_keepalive":
                    break;
                case "session_reconnect":
                {
                    var next = payload.GetProperty("session").GetProperty("reconnect_url").GetString();
                    _log.LogInformation("Twitch asked to reconnect to {Url}", next);
                    return next;
                }
                case "revocation":
                {
                    var sub = payload.GetProperty("subscription");
                    _log.LogWarning("Twitch revoked subscription {Type}: {Status}", sub.GetProperty("type").GetString(), sub.GetProperty("status").GetString());
                    break;
                }
                case "notification":
                {
                    var messageId = metadata.TryGetProperty("message_id", out var mid) ? mid.GetString() : null;
                    if (messageId is not null && !_seenMessages.TryAdd(messageId, DateTimeOffset.UtcNow)) break; // duplicate delivery
                    PruneSeen();
                    var subType = payload.GetProperty("subscription").GetProperty("type").GetString()!;
                    HandleNotification(subType, payload.GetProperty("event"));
                    break;
                }
            }
        }
        return null;
    }

    /// <summary>Runs synchronously for each mapped event before it is published (for state that commands must see, such as points).</summary>
    public event Action<BotEvent>? BeforePublish;

    /// <summary>Publishes the events for one EventSub notification (also used by the loopback test endpoint).</summary>
    public IReadOnlyList<BotEvent> HandleNotification(string subscriptionType, JsonElement ev)
    {
        var events = _mapper.Map(subscriptionType, ev);
        BotEvent? chat = null;
        foreach (var e in events)
        {
            if (e.Type == "FirstWord" && chat is not null)
                foreach (var key in (string[])["filtered", "filterName", "filterReason"])
                    if (chat.Data.TryGetValue(key, out var v)) e.Data[key] = v;
            Publish(e);
            if (e.Type == "ChatMessage") chat = e;
        }
        return events;
    }

    /// <summary>
    /// Publishes a Twitch event the way EventSub notifications are: chat runs through the moderation pipeline and the
    /// BeforePublish hooks first. Also used for simulated chat.
    /// </summary>
    public void Publish(BotEvent e)
    {
        if (e.Type == "ChatMessage")
        {
            TrackViewer(e);
            if (e.Data.GetValueOrDefault("isBroadcaster") is not true && e.Data.GetValueOrDefault("filtered") is not true) _moderation.Apply(e);
        }
        try { BeforePublish?.Invoke(e); }
        catch (Exception ex) { _log.LogWarning(ex, "BeforePublish handler failed for {Event}", e); }
        _bus.Publish(e);
    }

    private void TrackViewer(BotEvent chat)
    {
        var id = CherBotJson.Format(chat.Data.GetValueOrDefault("userId"));
        if (id.Length == 0) return;
        var role = CherBotJson.Format(chat.Data.GetValueOrDefault("role")) switch { "4" => "Broadcaster", "3" => "Moderator", "2" => "VIP", _ => "Viewer" };
        _viewers[id] = new ActiveViewer("twitch", id, CherBotJson.Format(chat.Data.GetValueOrDefault("userName")),
            CherBotJson.Format(chat.Data.GetValueOrDefault("user")), role, chat.Data.GetValueOrDefault("isSubscribed") is true, DateTimeOffset.UtcNow);
    }

    /// <summary>Raises Present Viewers every five minutes while connected, like Streamer.bot.</summary>
    private async Task PresentViewersLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var viewers = GetActiveViewers();
                if (!IsConnected || viewers.Count == 0) continue;
                _bus.Publish(BotEvent.FromTypeName("TwitchPresentViewers", new Dictionary<string, object?>
                {
                    ["users"] = viewers.Select(v => new Dictionary<string, object?>
                    {
                        ["id"] = v.Id, ["login"] = v.Login, ["display"] = v.Display, ["role"] = v.Role, ["subscribed"] = v.Subscribed,
                    }).ToList(),
                    ["count"] = viewers.Count,
                }));
            }
        }
        catch (OperationCanceledException) { }
    }

    private void PruneSeen()
    {
        if (_seenMessages.Count < 2000) return;
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var (k, v) in _seenMessages)
            if (v < cutoff) _seenMessages.TryRemove(k, out _);
    }

    private async Task CreateSubscriptionsAsync(string sessionId, CancellationToken ct)
    {
        var broadcaster = _auth.Get(TwitchRole.Broadcaster)!;
        await CleanupStaleAsync(ct);
        var results = new List<EventSubSubscriptionStatus>();
        foreach (var type in _options.EventSub.Distinct())
        {
            var (version, condition) = Condition(type, broadcaster.UserId);
            try
            {
                await _helix.SendAsync(HttpMethod.Post, "eventsub/subscriptions", body: new
                {
                    type,
                    version,
                    condition,
                    transport = new { method = "websocket", session_id = sessionId },
                }, ct: ct);
                results.Add(new EventSubSubscriptionStatus(type, version, "enabled", null));
            }
            catch (HelixException ex)
            {
                results.Add(new EventSubSubscriptionStatus(type, version, "failed", ex.Message));
                _log.LogWarning("EventSub {Type} not created: {Error}", type, ex.Message);
            }
        }
        _subscriptions = results;
    }

    /// <summary>Removes subscriptions left behind by earlier disconnected sessions.</summary>
    private async Task CleanupStaleAsync(CancellationToken ct)
    {
        try
        {
            var stale = await _helix.GetAsync("eventsub/subscriptions", new Dictionary<string, string?> { ["status"] = "websocket_disconnected" }, ct: ct);
            if (stale.ValueKind != JsonValueKind.Object || !stale.TryGetProperty("data", out var data)) return;
            foreach (var s in data.EnumerateArray())
                await _helix.SendAsync(HttpMethod.Delete, "eventsub/subscriptions", new Dictionary<string, string?> { ["id"] = s.GetProperty("id").GetString() }, ct: ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stale EventSub cleanup skipped");
        }
    }

    internal static (string Version, Dictionary<string, string> Condition) Condition(string type, string broadcasterId) => type switch
    {
        "channel.follow" => ("2", new() { ["broadcaster_user_id"] = broadcasterId, ["moderator_user_id"] = broadcasterId }),
        "channel.update" => ("2", new() { ["broadcaster_user_id"] = broadcasterId }),
        "channel.raid" => ("1", new() { ["to_broadcaster_user_id"] = broadcasterId }),
        "channel.chat.message" or "channel.chat.notification" or "channel.chat.message_delete" =>
            ("1", new() { ["broadcaster_user_id"] = broadcasterId, ["user_id"] = broadcasterId }),
        "channel.shoutout.create" or "channel.shoutout.receive" =>
            ("1", new() { ["broadcaster_user_id"] = broadcasterId, ["moderator_user_id"] = broadcasterId }),
        _ => ("1", new() { ["broadcaster_user_id"] = broadcasterId }),
    };

    // ── chat ───────────────────────────────────────────────────────────────

    public async Task<bool> SendMessageAsync(string text, bool useBot, string? replyToMessageId, CancellationToken ct) =>
        (await SendChatAsync(text, useBot, fallback: true, replyToMessageId, ct)).Sent;

    public sealed record ChatSendResult(bool Sent, string? MessageId, string? Error, string Account);

    /// <summary>Sends a chat message as the bot (falling back to the broadcaster when allowed) or as the broadcaster.</summary>
    public async Task<ChatSendResult> SendChatAsync(string text, bool useBot, bool fallback, string? replyTo, CancellationToken ct)
    {
        var broadcaster = _auth.Get(TwitchRole.Broadcaster);
        if (broadcaster is null) return new ChatSendResult(false, null, "broadcaster account is not linked", "none");
        var bot = useBot ? _auth.Get(TwitchRole.Bot) : null;
        var role = bot is not null ? TwitchRole.Bot : TwitchRole.Broadcaster;
        if (useBot && bot is null && !fallback) return new ChatSendResult(false, null, "bot account is not linked", "bot");

        var sender = role == TwitchRole.Bot ? bot!.UserId : broadcaster.UserId;
        string? lastId = null;
        foreach (var chunk in Chunk(text, 500))
        {
            try
            {
                var resp = await _helix.SendAsync(HttpMethod.Post, "chat/messages", body: new Dictionary<string, object?>
                {
                    ["broadcaster_id"] = broadcaster.UserId,
                    ["sender_id"] = sender,
                    ["message"] = chunk,
                    ["reply_parent_message_id"] = replyTo,
                }, role: role, ct: ct);
                var first = HelixClient.First(resp);
                if (first is { } f && f.TryGetProperty("is_sent", out var sent) && sent.ValueKind == JsonValueKind.False)
                {
                    var reason = f.TryGetProperty("drop_reason", out var dr) && dr.ValueKind == JsonValueKind.Object ? dr.GetProperty("message").GetString() : "dropped";
                    return new ChatSendResult(false, null, reason, role.ToString().ToLowerInvariant());
                }
                if (first is { } ok && ok.TryGetProperty("message_id", out var id)) lastId = id.GetString();
            }
            catch (HelixException ex)
            {
                if (role == TwitchRole.Bot && fallback)
                {
                    _log.LogWarning("Bot chat send failed ({Error}); falling back to broadcaster", ex.Message);
                    return await SendChatAsync(text, false, false, replyTo, ct);
                }
                return new ChatSendResult(false, null, ex.Message, role.ToString().ToLowerInvariant());
            }
        }
        return new ChatSendResult(true, lastId, null, role.ToString().ToLowerInvariant());
    }

    internal static IEnumerable<string> Chunk(string text, int max)
    {
        if (text.Length <= max) { yield return text; yield break; }
        var rest = text;
        while (rest.Length > max)
        {
            var cut = rest.LastIndexOf(' ', max);
            if (cut <= 0) cut = max;
            yield return rest[..cut].TrimEnd();
            rest = rest[cut..].TrimStart();
        }
        if (rest.Length > 0) yield return rest;
    }

    // ── status and info ────────────────────────────────────────────────────

    public IntegrationStatus GetStatus()
    {
        var b = _auth.Get(TwitchRole.Broadcaster);
        var bot = _auth.Get(TwitchRole.Bot);
        return new IntegrationStatus("twitch", _state, IsConnected, _lastError, new Dictionary<string, object?>
        {
            ["clientConfigured"] = _auth.ClientId is not null,
            ["broadcaster"] = b is null ? null : new { b.Login, b.UserId, b.DisplayName, b.ExpiresAt, scopes = b.Scopes },
            ["bot"] = bot is null ? null : new { bot.Login, bot.UserId, bot.DisplayName, bot.ExpiresAt, scopes = bot.Scopes },
            ["sessionId"] = _sessionId,
            ["connectedAt"] = _connectedAt,
            ["subscriptions"] = _subscriptions,
            ["deviceFlows"] = new[] { _auth.Flow(TwitchRole.Broadcaster), _auth.Flow(TwitchRole.Bot) }.Where(f => f is not null),
        });
    }

    public BroadcasterAccount GetAccount()
    {
        var b = _auth.Get(TwitchRole.Broadcaster);
        var bot = _auth.Get(TwitchRole.Bot);
        return new BroadcasterAccount("twitch", IsConnected, b?.Login, b?.DisplayName, b?.UserId, bot?.Login, bot?.DisplayName, bot?.UserId,
            b?.BroadcasterType == "affiliate", b?.BroadcasterType == "partner");
    }

    public IReadOnlyList<ActiveViewer> GetActiveViewers()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
        return _viewers.Values.Where(v => v.LastActive >= cutoff).OrderByDescending(v => v.LastActive).ToList();
    }

    public IEnumerable<int> EmittedEventTypes =>
    [
        101, 102, 103, 104, 105, 106, 107, 112, 118, 120, 124, 133, 135, 138, 139, 144, 154, 155, 156, 173, 174, 175,
    ];
}
