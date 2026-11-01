using System.Text.Json;
using CherBot.Core;
using CherBot.Core.Events;
using CherBot.Integrations.Twitch;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CherBot.Integrations.Community;

public sealed class TrackingSettings
{
    public bool Enabled { get; set; }
    public bool ChatArchive { get; set; }
    public int DetailDays { get; set; } = 90;
    public int ChatDays { get; set; } = 30;
    public string? StartedAt { get; set; }
}

public sealed record LiveMessage(string Id, string UserId, string UserName, string Login, string Text, string Timestamp, bool Filtered);
public sealed record LiveUser(string UserId, string Login, string UserName, string Role, string LastSeen, long Messages, long Points, int Rank);

/// <summary>Bounded in-memory live data works with tracking off. Detailed persistence is separately opt-in.</summary>
public sealed class UserTrackingService : BackgroundService
{
    private readonly CherBotDb _db;
    private readonly StreamSessionService _sessions;
    private readonly TwitchService _twitch;
    private readonly EventBus _bus;
    private readonly ILogger<UserTrackingService> _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, LiveUser> _users = new();
    private readonly Queue<LiveMessage> _messages = new();
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    private string _sessionKey = "";
    public TrackingSettings Settings { get; private set; }
    public string? LastError { get; private set; }

    public UserTrackingService(CherBotDb db, StreamSessionService sessions, TwitchService twitch, EventBus bus, ILogger<UserTrackingService> log)
    {
        _db = db; _sessions = sessions; _twitch = twitch; _bus = bus; _log = log;
        Settings = db.GetKv("tracking.settings") is { } text ? JsonSerializer.Deserialize<TrackingSettings>(text, CherBotJson.Options) ?? new() : new();
        db.PointsContext = () => new PointContext(sessions.BroadcasterId, sessions.StreamId, Settings.Enabled && sessions.BroadcasterId.Length > 0);
    }

    public void Save(TrackingSettings settings)
    {
        if (settings.DetailDays is < 1 or > 3650 || settings.ChatDays is < 1 or > 3650) throw new ArgumentException("Retention must be 1–3650 days.");
        if (settings.ChatArchive && !settings.Enabled) throw new ArgumentException("Enable extended statistics before the chat archive.");
        settings.StartedAt = Settings.StartedAt ?? (settings.Enabled ? DateTimeOffset.UtcNow.ToString("O") : null);
        _db.SetKv("tracking.settings", JsonSerializer.Serialize(settings, CherBotJson.Options));
        Settings = settings;
    }

    public IReadOnlyList<LiveUser> Users(string? search = null, int offset = 0, int limit = 100)
    {
        lock (_gate)
            return _users.Values.Where(u => string.IsNullOrWhiteSpace(search) || (u.UserName + " " + u.Login + " " + u.UserId).Contains(search, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(u => u.LastSeen).ThenBy(u => u.UserId).Skip(offset).Take(limit)
                .Select(u => u with { Points = _db.GetPoints("twitch", u.UserId), Rank = _db.GetRank("twitch", u.UserId) }).ToList();
    }

    public IReadOnlyList<LiveMessage> Messages(string? userId = null) { lock (_gate) return _messages.Where(m => userId is null || m.UserId == userId).ToList(); }

    private static string S(BotEvent e, string key) => CherBotJson.Format(e.Data.GetValueOrDefault(key));

    public void Observe(BotEvent ev)
    {
        if (ev.Source == "Twitch" && ev.Type is "StreamOnline" or "StreamOffline")
        {
            var session = _sessions.BroadcasterId + ":" + _sessions.StreamId;
            lock (_gate) if (_sessionKey != session) { _users.Clear(); _messages.Clear(); _seen.Clear(); _seenOrder.Clear(); _sessionKey = session; }
            return;
        }
        if (ev.Source != "Twitch" || ev.Type != "ChatMessage") return;
        var id = S(ev, "userId");
        if (id.Length == 0) return;
        var sourceId = S(ev, "msgId");
        if (sourceId.Length == 0) sourceId = Guid.NewGuid().ToString("N");
        var key = _sessions.BroadcasterId + ":" + _sessions.StreamId;
        var name = S(ev, "user");
        var login = S(ev, "userName");
        var role = ev.Data.GetValueOrDefault("isBroadcaster") is true ? "Broadcaster" : ev.Data.GetValueOrDefault("isModerator") is true ? "Moderator" : ev.Data.GetValueOrDefault("isVip") is true ? "VIP" : "Viewer";
        var filtered = ev.Data.GetValueOrDefault("filtered") is true;
        lock (_gate)
        {
            if (_sessionKey != key) { _users.Clear(); _messages.Clear(); _seen.Clear(); _seenOrder.Clear(); _sessionKey = key; }
            if (!_seen.Add(sourceId)) return;
            _seenOrder.Enqueue(sourceId);
            while (_seenOrder.Count > 5000) _seen.Remove(_seenOrder.Dequeue());
            _users.TryGetValue(id, out var previous);
            _users[id] = new(id, login, name, role, ev.Timestamp.ToString("O"), (previous?.Messages ?? 0) + 1, 0, 0);
            if (_users.Count > 2000) _users.Remove(_users.Values.MinBy(u => u.LastSeen)!.UserId);
            _messages.Enqueue(new(sourceId, id, name, login, S(ev, "message"), ev.Timestamp.ToString("O"), filtered));
            while (_messages.Count > 500) _messages.Dequeue();
        }
        // Do not retain filtered message text. Basic live counters can still display the moderation event.
        if (!Settings.Enabled || _sessions.BroadcasterId.Length == 0) return;
        try
        {
            _db.RecordActivity(_sessions.BroadcasterId, _sessions.StreamId, id, login, name, role, false, sourceId,
                Settings.ChatArchive && !filtered ? S(ev, "message") : null);
            LastError = null;
        }
        catch (Exception ex) { LastError = ex.Message; _log.LogWarning(ex, "Optional history recording failed"); }
    }

    public void Forget(string? userId)
    {
        lock (_gate)
        {
            if (userId is null) { _users.Clear(); _messages.Clear(); }
            else
            {
                _users.Remove(userId);
                var remaining = _messages.Where(m => m.UserId != userId).ToArray();
                _messages.Clear(); foreach (var message in remaining) _messages.Enqueue(message);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _twitch.BeforePublish += Observe;
        using var interactions = _bus.Subscribe("user-interaction-history", ev =>
        {
            if (!Settings.Enabled) return Task.CompletedTask;
            var id = S(ev, "userId");
            if (id.Length > 0 && _sessions.BroadcasterId.Length > 0)
                _db.Execute("INSERT OR IGNORE INTO user_history(broadcaster,stream_id,user_id,user_name,ts,kind,detail,source_id) VALUES($b,$s,$u,$n,$t,'interaction',$d,$source)",
                    ("$b", _sessions.BroadcasterId), ("$s", _sessions.StreamId), ("$u", id), ("$n", S(ev, "user")), ("$t", ev.Timestamp.ToString("O")), ("$d", ev.Source + "." + ev.Type), ("$source", "interaction:" + ev.Seq));
            return Task.CompletedTask;
        }, ev => ev.Source == "Twitch" && ev.Type is "Follow" or "Sub" or "ReSub" or "GiftSub" or "GiftBomb" or "Cheer" or "Raid" or "RewardRedemption");
        using var commands = _bus.Subscribe("user-command-history", ev =>
        {
            var id = S(ev, "userId");
            if (Settings.Enabled && id.Length > 0)
            {
                _db.Execute("UPDATE user_activity SET commands=commands+1 WHERE broadcaster=$b AND user_id=$u AND stream_id=$s",
                    ("$b", _sessions.BroadcasterId), ("$u", id), ("$s", _sessions.StreamId));
                _db.Execute("INSERT INTO user_history(broadcaster,stream_id,user_id,user_name,ts,kind,detail,source_id) VALUES($b,$s,$u,$n,$t,'command',$d,$source)",
                    ("$b", _sessions.BroadcasterId), ("$s", _sessions.StreamId), ("$u", id), ("$n", S(ev, "user")), ("$t", ev.Timestamp.ToString("O")),
                    ("$d", S(ev, "command")), ("$source", "command:" + ev.Seq));
            }
            return Task.CompletedTask;
        }, ev => ev.Source == "Command" && ev.Type == "Triggered");
        try
        {
            using var tick = new PeriodicTimer(TimeSpan.FromMinutes(15));
            do
            {
                try { _db.PruneTracking(Settings.DetailDays, Settings.ChatDays); }
                catch (Exception ex) { LastError = ex.Message; _log.LogWarning(ex, "History retention failed"); }
            } while (await tick.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { _twitch.BeforePublish -= Observe; }
    }
}
