using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CherBot.Core;
using CherBot.Core.Config;
using CherBot.Core.Events;
using CherBot.Integrations.Community;
using CherBot.Storage;
using Microsoft.Extensions.Hosting;

namespace CherBot.Integrations.Songs;

public sealed class SongSettings
{
    public bool Enabled { get; set; }
    public bool YouTube { get; set; } = true;
    public bool AppleMusic { get; set; }
    public bool Spotify { get; set; }
    public bool ApprovalRequired { get; set; } = true;
    public bool IntakePaused { get; set; }
    public bool OnlyWhileLive { get; set; } = true;
    public int CooldownSeconds { get; set; } = 30;
    public int PerUserLimit { get; set; } = 3;
    public int QueueLimit { get; set; } = 100;
}

public sealed record SongLink(string Provider, string TrackId, string Url);
public sealed record SongRequest(Guid Id, string Provider, string TrackId, string Url, string UserId, string UserName, string Status, long Position, string CreatedAt, string SourceId, string? Error = null);
public sealed record PlayerLease(string LeaseId, DateTimeOffset ExpiresAt);

/// <summary>A free request queue; provider links never cause arbitrary server-side URL fetching.</summary>
public sealed class SongRequestService : IHostedService
{
    private const string Kind = "songRequest";
    private readonly CherBotDb _db;
    private readonly ConfigService _config;
    private readonly EventBus _bus;
    private readonly StreamSessionService _sessions;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SongRequest> _requests = [];
    private readonly Dictionary<string, DateTimeOffset> _lastRequest = [];
    private string? _leaseId, _clientId;
    private DateTimeOffset _expiresAt;
    private DateTimeOffset _startupTakeoverAfter;
    private readonly TimeProvider _clock;
    public SongSettings Settings { get; private set; }
    public bool PlayerConnected { get { lock (_gate) return _leaseId is not null && _expiresAt > _clock.GetUtcNow(); } }

    public SongRequestService(CherBotDb db, ConfigService config, EventBus bus, StreamSessionService sessions, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _db = db; _config = config; _bus = bus; _sessions = sessions;
        Settings = db.GetKv("songs.settings") is { } text ? JsonSerializer.Deserialize<SongSettings>(text, CherBotJson.Options) ?? new() : new();
        foreach (var row in db.GetDocs(Kind))
        {
            var request = JsonSerializer.Deserialize<SongRequest>(row.Json, CherBotJson.Options);
            if (request is null) continue;
            if (request.Status == "playing" && request.Provider == "youtube")
            {
                request = request with { Status = "approved" };
                _startupTakeoverAfter = _clock.GetUtcNow().AddSeconds(20);
            }
            _requests[request.Id] = request;
        }
    }

    public static SongLink Normalize(string input)
    {
        if (input.Length > 2048 || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new ArgumentException("Use a full HTTPS YouTube video, Apple Music song, or Spotify track link.");
        var host = uri.Host.ToLowerInvariant(); var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtu.be")
        {
            var id = host == "youtu.be" && path.Length == 1 ? path[0] : path.Length == 1 && path[0] == "watch" ? query["v"] : path.Length == 2 && path[0] is "shorts" or "embed" ? path[1] : null;
            if (id is not null && Regex.IsMatch(id, "^[a-zA-Z0-9_-]{11}$")) return new("youtube", id, "https://www.youtube.com/watch?v=" + id);
        }
        else if (host == "open.spotify.com")
        {
            var index = path.Length == 3 && path[0].StartsWith("intl-") ? 1 : 0;
            if (path.Length == index + 2 && path[index] == "track" && Regex.IsMatch(path[index + 1], "^[a-zA-Z0-9]{22}$")) return new("spotify", path[index + 1], "https://open.spotify.com/track/" + path[index + 1]);
        }
        else if (host == "music.apple.com" && path.Length >= 3 && Regex.IsMatch(path[0], "^[a-zA-Z]{2}$"))
        {
            var id = path[1] == "song" ? path[^1] : path[1] == "album" ? query["i"] : null;
            if (id is not null && Regex.IsMatch(id, "^[0-9]+$")) return new("apple", id, $"https://music.apple.com/{path[0].ToLowerInvariant()}/song/{id}");
        }
        throw new ArgumentException("Unsupported link. Use a YouTube video, Spotify track, or Apple Music song (not a playlist or album).");
    }

    private static bool Active(SongRequest r) => r.Status is "pending" or "approved" or "playing";
    public void ReloadSettings()
    {
        lock (_gate) if (_db.GetKv("songs.settings") is { } json) Settings = JsonSerializer.Deserialize<SongSettings>(json, CherBotJson.Options) ?? new();
    }
    public IReadOnlyList<SongRequest> All { get { lock (_gate) return _requests.Values.OrderBy(r => r.Position).ToList(); } }

    public void Save(SongSettings settings)
    {
        if (settings.CooldownSeconds is < 0 or > 3600 || settings.PerUserLimit is < 1 or > 100 || settings.QueueLimit is < 1 or > 1000) throw new ArgumentException("Invalid queue limits or cooldown.");
        if (settings.Enabled && !settings.YouTube && !settings.Spotify && !settings.AppleMusic) throw new ArgumentException("Choose at least one provider.");
        lock (_gate)
        {
            foreach (var word in new[] { "!sr", "!songrequest", "!songqueue", "!songskip" })
                if (settings.Enabled && _config.Commands.Any(c => c.Aliases().Contains(word, StringComparer.OrdinalIgnoreCase) && c.Group != "Song Requests")) throw new ArgumentException($"Command {word} is already used by another workflow.");
            _db.SetKv("songs.settings", CherBotJson.Serialize(settings)); Settings = settings;
            SeedCommands(settings.Enabled); Notify();
        }
    }

    private void SeedCommands(bool enabled)
    {
        foreach (var (name, text, type, mod) in new[] { ("!sr", "!sr\n!songrequest", SongHandlers.Request, false), ("!songqueue", "!songqueue", SongHandlers.Queue, true), ("!songskip", "!songskip", SongHandlers.Skip, true) })
        {
            var pair = Seeding.CommandAction(_config, "Song Requests", name, text, [Seeding.Step(type), Seeding.Reply()], false, cmd => { cmd.GlobalCooldown = 0; cmd.UserCooldown = 0; if (mod) cmd.Set("minRole", "moderator"); });
            if (pair.Command.Group == "Song Requests") { pair.Command.Enabled = enabled; _config.SaveCommand(pair.Command); }
        }
    }

    public SongRequest Add(string userId, string userName, string url, string sourceId)
    {
        var link = Normalize(url);
        lock (_gate)
        {
            if (_requests.Values.FirstOrDefault(r => r.SourceId == sourceId && sourceId.Length > 0) is { } duplicate) return duplicate;
            if (!Settings.Enabled || Settings.IntakePaused) throw new ArgumentException("Song requests are paused or disabled.");
            if (Settings.OnlyWhileLive && !_sessions.Live) throw new ArgumentException("Song requests open when the stream is live.");
            if (userId.Length == 0) throw new ArgumentException("A viewer identity is required.");
            if (!(link.Provider switch { "youtube" => Settings.YouTube, "apple" => Settings.AppleMusic, "spotify" => Settings.Spotify, _ => false })) throw new ArgumentException("This music provider is disabled.");
            var active = _requests.Values.Where(Active).ToList();
            if (active.Count >= Settings.QueueLimit || active.Count(r => r.UserId == userId) >= Settings.PerUserLimit) throw new ArgumentException("The queue or your request limit is full.");
            if (active.Any(r => r.Provider == link.Provider && r.TrackId == link.TrackId)) throw new ArgumentException("That track is already in the queue.");
            if (_lastRequest.TryGetValue(userId, out var last) && (DateTimeOffset.UtcNow - last).TotalSeconds < Settings.CooldownSeconds) throw new ArgumentException("Please wait for your song request cooldown.");
            var request = new SongRequest(Guid.NewGuid(), link.Provider, link.TrackId, link.Url, userId, userName, Settings.ApprovalRequired ? "pending" : "approved", _requests.Count == 0 ? 1 : _requests.Values.Max(r => r.Position) + 1, DateTimeOffset.UtcNow.ToString("O"), sourceId);
            Persist(request); _lastRequest[userId] = DateTimeOffset.UtcNow;
            if (_lastRequest.Count > 5000) _lastRequest.Remove(_lastRequest.MinBy(p => p.Value).Key);
            foreach (var old in _requests.Values.Where(r => !Active(r)).OrderByDescending(r => r.CreatedAt).Skip(100).ToArray()) { _requests.Remove(old.Id); _db.DeleteDoc(Kind, old.Id.ToString()); }
            Notify(); return request;
        }
    }

    public void Change(Guid id, string operation)
    {
        lock (_gate)
        {
            if (!_requests.TryGetValue(id, out var request)) throw new ArgumentException("Song request not found.");
            var status = operation switch
            {
                "approve" when request.Status == "pending" => "approved",
                "reject" when request.Status is "pending" or "approved" => "rejected",
                "complete" when request.Status == "playing" => "completed",
                "skip" when Active(request) => "completed",
                "fail" when Active(request) => "failed",
                _ => throw new ArgumentException("Invalid request transition.")
            };
            Persist(request with { Status = status }); Notify();
        }
    }

    public void Remove(Guid id) { lock (_gate) { if (!_requests.ContainsKey(id)) throw new ArgumentException("Song request not found."); _requests.Remove(id); _db.DeleteDoc(Kind, id.ToString()); Notify(); } }
    public void Clear() { lock (_gate) { foreach (var r in _requests.Values.ToArray()) _db.DeleteDoc(Kind, r.Id.ToString()); _requests.Clear(); Notify(); } }
    public void Reorder(IReadOnlyList<Guid> ids)
    {
        lock (_gate)
        {
            var active = _requests.Values.Where(Active).Select(r => r.Id).ToHashSet();
            if (ids.Count != active.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => !active.Contains(id))) throw new ArgumentException("Reorder must contain every active request exactly once.");
            for (var i = 0; i < ids.Count; i++) Persist(_requests[ids[i]] with { Position = i + 1 }); Notify();
        }
    }
    public SongRequest? Advance() { lock (_gate) return AdvanceLocked(); }
    private SongRequest? AdvanceLocked()
    {
        if (!Settings.Enabled) return null;
        var current = _requests.Values.FirstOrDefault(r => r.Status == "playing"); if (current is not null) return current;
        var next = _requests.Values.Where(r => r.Status == "approved").OrderBy(r => r.Position).FirstOrDefault();
        if (next is null) return null; next = next with { Status = "playing" }; Persist(next); Notify(); return next;
    }
    private void Persist(SongRequest request) { _db.UpsertDoc(Kind, request.Id.ToString(), request.Url, CherBotJson.Serialize(request)); _requests[request.Id] = request; }
    private void Notify() => _bus.Publish(new BotEvent("CherBot", "SongQueueChanged") { Persist = false });

    public string RotatePlayerToken()
    {
        lock (_gate)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _db.SetKv("songs.player.hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
            // Keep any old lease until expiry so a revoked player stops before a replacement can start.
            return token;
        }
    }
    public bool AuthorizePlayer(string token)
    {
        var hash = _db.GetKv("songs.player.hash");
        return token.Length is > 0 and < 256 && hash is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)))));
    }
    public PlayerLease Acquire(string clientId)
    {
        if (!Guid.TryParse(clientId, out _)) throw new ArgumentException("Invalid player identity.");
        lock (_gate)
        {
            if (_clock.GetUtcNow() < _startupTakeoverAfter) throw new InvalidOperationException("The engine restarted during playback. Wait 20 seconds for the previous player to stop, then reconnect.");
            if (_expiresAt > _clock.GetUtcNow() && _leaseId is not null && _clientId != clientId) throw new InvalidOperationException("Another song player owns playback. Close it or wait 20 seconds for its lease to expire.");
            if (_clientId != clientId || _expiresAt <= _clock.GetUtcNow() || _leaseId is null)
            {
                foreach (var r in _requests.Values.Where(r => r.Status == "playing" && r.Provider == "youtube").ToArray()) Persist(r with { Status = "approved" });
                _leaseId = Guid.NewGuid().ToString("N"); _clientId = clientId;
            }
            _expiresAt = _clock.GetUtcNow().AddSeconds(20); return new(_leaseId, _expiresAt);
        }
    }
    public SongRequest? PlayerState(string lease)
    {
        lock (_gate) { CheckLease(lease); _expiresAt = _clock.GetUtcNow().AddSeconds(20); return AdvanceLocked(); }
    }
    public void PlayerResult(string lease, Guid requestId, bool failed, string? error)
    {
        lock (_gate)
        {
            CheckLease(lease);
            if (!_requests.TryGetValue(requestId, out var request) || request.Status != "playing" || request.Provider != "youtube") throw new ArgumentException("This player does not own that active YouTube request.");
            Persist(request with { Status = failed ? "failed" : "completed", Error = failed ? (error ?? "Playback failed")[..Math.Min(200, (error ?? "Playback failed").Length)] : null }); Notify();
        }
    }
    private void CheckLease(string lease) { if (_leaseId != lease || _expiresAt <= _clock.GetUtcNow()) throw new InvalidOperationException("Player lease expired. Stop playback and reconnect."); }
    public Task StartAsync(CancellationToken ct)
    {
        SeedCommands(Settings.Enabled);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
